using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.OnSiteSurveys;
using SmartSolar.Modules.PreSurvey.Enums;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.SchedulingTestContext;

namespace SmartSolar.Tests.Unit.InstallationWorkflow;

public sealed class OnSiteSurveyTests
{
    public static OnSiteSurveyData FullData(IReadOnlyList<ObstacleInput>? obstacles = null) => new(
        "FLAT_CONCRETE", "CONCRETE", 12m, 8m, 80m, 10m, 180m, "GOOD", "Main cabinet 63A, space for breaker", "Single phase", "FEASIBLE",
        "Garage wall", "Two rows facing south", 10.765300m, 106.603700m, "Ladder access from the back",
        obstacles ?? [new ObstacleInput("WATER_TANK", "Inox tank", 1.5m, 2m)]);

    private static async Task<(InstallationWorkflowTestContext Ctx, AcknowledgedSurvey Survey)> ReadyAsync()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        var survey = await ctx.AcknowledgedSurveyAsync();
        ctx.Clock.Now = At(10).ToUniversalTime();
        return (ctx, survey);
    }

    /// <summary>Start → save → roof photo → complete; returns the completed view.</summary>
    public static async Task<OnSiteSurveyView> CompleteAsync(InstallationWorkflowTestContext ctx, AcknowledgedSurvey s)
    {
        ctx.Clock.Now = At(10).ToUniversalTime();
        var started = await ctx.OnSite().StartAsync(s.Technician.Id, s.AppointmentId, CancellationToken.None);
        Assert.True(started.Succeeded, started.Message);
        var saved = await ctx.OnSite().SaveAsync(new SaveOnSiteSurveyCommand(s.Technician.Id, s.AppointmentId, started.Value!.Revision, FullData()), CancellationToken.None);
        Assert.True(saved.Succeeded, saved.Message);
        var photo = await ctx.VerifiedFileAsync(s.Technician.Id, FilePurposes.OnSiteSurveyEvidence);
        var attached = await ctx.OnSite().AddAttachmentAsync(new AddOnSiteSurveyAttachmentCommand(s.Technician.Id, s.AppointmentId, photo, "ROOF", "South side"), CancellationToken.None);
        Assert.True(attached.Succeeded, attached.Message);
        var completed = await ctx.OnSite().CompleteAsync(new CompleteOnSiteSurveyCommand(s.Technician.Id, s.AppointmentId, attached.Value!.Revision), CancellationToken.None);
        Assert.True(completed.Succeeded, completed.Message);
        return completed.Value!;
    }

    [Fact]
    public async Task Assigned_technician_completes_the_survey_and_the_request_becomes_completed()
    {
        var (ctx, s) = await ReadyAsync();
        await using var _ = ctx;

        var view = await CompleteAsync(ctx, s);

        Assert.Equal(OnSiteSurveyStatuses.Completed, view.Status);
        Assert.Equal(1, view.Version);
        Assert.NotNull(view.StartedAt);
        Assert.NotNull(view.CompletedAt);
        Assert.Single(view.Obstacles);
        Assert.Single(view.Attachments);
        Assert.Equal(SurveyRequestStatus.Completed, (await ctx.Db.SurveyRequests.AsNoTracking().SingleAsync(r => r.Id == s.Request.Request.Id)).Status);
        // The customer's pre-survey is untouched.
        var preSurvey = await ctx.Db.PreSurveys.AsNoTracking().SingleAsync(p => p.Id == s.Request.PreSurvey.Id);
        Assert.Equal(s.Request.PreSurvey.TotalAreaM2, preSurvey.TotalAreaM2);
        // Repeating completion is idempotent.
        var again = await ctx.OnSite().CompleteAsync(new CompleteOnSiteSurveyCommand(s.Technician.Id, s.AppointmentId, 0), CancellationToken.None);
        Assert.Equal(view.Revision, again.Value!.Revision);
    }

    [Fact]
    public async Task Only_the_assigned_technician_of_an_acknowledged_active_appointment_may_work()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var s = await ctx.AcknowledgedSurveyAsync();
        var stranger = await ctx.Scheduling.SeedTechnicianAsync("Stranger", District7);
        ctx.Clock.Now = At(10).ToUniversalTime();

        Assert.Equal(OnSiteSurveyErrorCodes.AppointmentNotFound, (await ctx.OnSite().StartAsync(stranger.Id, s.AppointmentId, CancellationToken.None)).ErrorCode);
        Assert.Equal(OnSiteSurveyErrorCodes.AppointmentNotFound, (await ctx.OnSite().StartAsync(s.Technician.Id, Guid.NewGuid(), CancellationToken.None)).ErrorCode);

        ctx.Clock.Now = At(6).ToUniversalTime();
        Assert.Equal(OnSiteSurveyErrorCodes.TooEarly, (await ctx.OnSite().StartAsync(s.Technician.Id, s.AppointmentId, CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task Unacknowledged_assignments_cannot_be_surveyed()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        ctx.Scheduling.Travel.Minutes = _ => 20;
        await ctx.Scheduling.SeedTechnicianAsync("Tech", District1);
        var request = await ctx.Scheduling.SeedSurveyRequestAsync(BinhTan);
        Assert.True((await ctx.Scheduling.Book().HandleAsync(new Modules.Scheduling.PreSurveyAppointments.BookSurveyAppointmentCommand(request.Customer.Id, request.Request.Id, At(10), null), CancellationToken.None)).Succeeded);
        var sales = await ctx.Scheduling.ClaimAsync(request);
        var appointment = await ctx.Scheduling.ActiveAppointmentAsync(request.Request.Id);
        await ctx.Scheduling.Assign().HandleAsync(new Modules.Scheduling.PreSurveyAppointments.AssignTechnicianCommand(sales.Id, request.Request.Id, appointment.ReservedTechnicianUserId), CancellationToken.None);
        ctx.Clock.Now = At(10).ToUniversalTime();

        Assert.Equal(OnSiteSurveyErrorCodes.NotAcknowledged, (await ctx.OnSite().StartAsync(appointment.ReservedTechnicianUserId, appointment.Id, CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task Completion_requires_measurements_a_verified_roof_photo_and_a_started_survey()
    {
        var (ctx, s) = await ReadyAsync();
        await using var _ = ctx;

        var draft = await ctx.OnSite().SaveAsync(new SaveOnSiteSurveyCommand(s.Technician.Id, s.AppointmentId, null,
            FullData() with { RoofLengthM = null }), CancellationToken.None);
        Assert.Equal(OnSiteSurveyStatuses.Draft, draft.Value!.Status);
        Assert.Equal(OnSiteSurveyErrorCodes.NotStarted,
            (await ctx.OnSite().CompleteAsync(new CompleteOnSiteSurveyCommand(s.Technician.Id, s.AppointmentId, draft.Value.Revision), CancellationToken.None)).ErrorCode);

        var started = await ctx.OnSite().StartAsync(s.Technician.Id, s.AppointmentId, CancellationToken.None);
        var incomplete = await ctx.OnSite().CompleteAsync(new CompleteOnSiteSurveyCommand(s.Technician.Id, s.AppointmentId, started.Value!.Revision), CancellationToken.None);
        Assert.Equal(OnSiteSurveyErrorCodes.Incomplete, incomplete.ErrorCode);

        var saved = await ctx.OnSite().SaveAsync(new SaveOnSiteSurveyCommand(s.Technician.Id, s.AppointmentId, started.Value.Revision, FullData()), CancellationToken.None);
        Assert.Equal(OnSiteSurveyErrorCodes.EvidenceRequired,
            (await ctx.OnSite().CompleteAsync(new CompleteOnSiteSurveyCommand(s.Technician.Id, s.AppointmentId, saved.Value!.Revision), CancellationToken.None)).ErrorCode);

        // A pending (unverified) file is never usable as evidence.
        ctx.Storage.Unavailable = true;
        var key = $"onsite-surveys/{s.Technician.Id}/pending.jpg";
        ctx.Storage.Put(InstallationWorkflowTestContext.Bucket, key, FakeStorageObjectVerifier.JpegBytes);
        var pending = await ctx.Files().RegisterAsync(new RegisterFileCommand(s.Technician.Id, FilePurposes.OnSiteSurveyEvidence, null, key, "p.jpg", "image/jpeg",
            FakeStorageObjectVerifier.JpegBytes.Length, null), CancellationToken.None);
        Assert.Equal(FileErrorCodes.NotVerified, (await ctx.OnSite().AddAttachmentAsync(
            new AddOnSiteSurveyAttachmentCommand(s.Technician.Id, s.AppointmentId, pending.Value!.FileId, "ROOF", null), CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public void Invalid_measurements_and_vocabulary_are_rejected()
    {
        var errors = OnSiteSurveyHandlers.Validate(FullData() with
        {
            RoofLengthM = -1, TiltDegree = 95, AzimuthDegree = 360, UsableAreaM2 = 10_000, RoofCondition = "SHINY",
            MeasuredLatitude = 10m, MeasuredLongitude = null, Obstacles = [new ObstacleInput("DRAGON", null, null, null)],
        }, forCompletion: true)!;

        Assert.Contains("roofLengthM", errors.Keys);
        Assert.Contains("tiltDegree", errors.Keys);
        Assert.Contains("azimuthDegree", errors.Keys);
        Assert.Contains("roofCondition", errors.Keys);
        Assert.Contains("measuredLatitude", errors.Keys);
        Assert.Contains("obstacles", errors.Keys);
        Assert.Null(OnSiteSurveyHandlers.Validate(FullData(), forCompletion: true));
    }

    [Fact]
    public async Task A_stale_revision_is_rejected_so_concurrent_edits_never_overwrite_each_other()
    {
        var (ctx, s) = await ReadyAsync();
        await using var _ = ctx;
        var started = await ctx.OnSite().StartAsync(s.Technician.Id, s.AppointmentId, CancellationToken.None);
        var revision = started.Value!.Revision;

        var first = await ctx.OnSite().SaveAsync(new SaveOnSiteSurveyCommand(s.Technician.Id, s.AppointmentId, revision, FullData()), CancellationToken.None);
        var second = await ctx.OnSite().SaveAsync(new SaveOnSiteSurveyCommand(s.Technician.Id, s.AppointmentId, revision, FullData() with { Notes = "lost update" }), CancellationToken.None);

        Assert.True(first.Succeeded);
        Assert.Equal(OnSiteSurveyErrorCodes.StaleRevision, second.ErrorCode);
        Assert.NotEqual("lost update", (await ctx.OnSite().GetForTechnicianAsync(s.Technician.Id, s.AppointmentId, CancellationToken.None)).Value!.Data.Notes);
    }

    [Fact]
    public async Task Completed_surveys_are_locked_and_corrected_only_through_versioned_amendments()
    {
        var (ctx, s) = await ReadyAsync();
        await using var _ = ctx;
        var completed = await CompleteAsync(ctx, s);

        Assert.Equal(OnSiteSurveyErrorCodes.AlreadyCompleted, (await ctx.OnSite().SaveAsync(
            new SaveOnSiteSurveyCommand(s.Technician.Id, s.AppointmentId, completed.Revision, FullData()), CancellationToken.None)).ErrorCode);

        var extra = await ctx.VerifiedFileAsync(s.Technician.Id, FilePurposes.OnSiteSurveyEvidence, "pdf");
        var amended = await ctx.OnSite().AmendAsync(new AmendOnSiteSurveyCommand(s.Technician.Id, s.AppointmentId, "Re-measured the west edge",
            new OnSiteSurveyData(null, null, null, 9m, null, null, null, null, null, null, null, null, null, null, null, null, null), [extra], completed.Revision), CancellationToken.None);

        Assert.True(amended.Succeeded, amended.Message);
        Assert.Equal(2, amended.Value!.Version);
        Assert.Equal(9m, amended.Value.Data.RoofWidthM);
        var amendment = Assert.Single(amended.Value.Amendments);
        Assert.Equal(8m, amendment.Changes.GetProperty("roofWidthM").GetProperty("old").GetDecimal());
        Assert.Equal(2, amended.Value.Attachments.Count);
        Assert.Equal(CommonErrorCodes.ValidationFailed, (await ctx.OnSite().AmendAsync(new AmendOnSiteSurveyCommand(s.Technician.Id, s.AppointmentId, "nothing",
            new OnSiteSurveyData(null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null), null, amended.Value.Revision), CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task One_file_cannot_be_attached_twice_and_another_users_file_is_refused()
    {
        var (ctx, s) = await ReadyAsync();
        await using var _ = ctx;
        await ctx.OnSite().StartAsync(s.Technician.Id, s.AppointmentId, CancellationToken.None);
        var photo = await ctx.VerifiedFileAsync(s.Technician.Id, FilePurposes.OnSiteSurveyEvidence);
        var foreign = await ctx.VerifiedFileAsync(s.Sales.Id, FilePurposes.OnSiteSurveyEvidence);

        Assert.True((await ctx.OnSite().AddAttachmentAsync(new AddOnSiteSurveyAttachmentCommand(s.Technician.Id, s.AppointmentId, photo, "ROOF", null), CancellationToken.None)).Succeeded);
        Assert.Equal(FileErrorCodes.AlreadyLinked, (await ctx.OnSite().AddAttachmentAsync(new AddOnSiteSurveyAttachmentCommand(s.Technician.Id, s.AppointmentId, photo, "ROOF", null), CancellationToken.None)).ErrorCode);
        Assert.Equal(FileErrorCodes.NotOwned, (await ctx.OnSite().AddAttachmentAsync(new AddOnSiteSurveyAttachmentCommand(s.Technician.Id, s.AppointmentId, foreign, "ROOF", null), CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task Sales_owner_and_managers_can_read_the_report_others_cannot()
    {
        var (ctx, s) = await ReadyAsync();
        await using var _ = ctx;
        await CompleteAsync(ctx, s);

        Assert.True((await ctx.OnSite().GetForStaffAsync(s.Request.Request.Id, s.Sales.Id, false, sale => sale == s.Sales.Id, CancellationToken.None)).Succeeded);
        Assert.True((await ctx.OnSite().GetForStaffAsync(s.Request.Request.Id, Guid.NewGuid(), true, _ => false, CancellationToken.None)).Succeeded);
        Assert.Equal(OnSiteSurveyErrorCodes.ReportNotFound,
            (await ctx.OnSite().GetForStaffAsync(s.Request.Request.Id, Guid.NewGuid(), false, _ => false, CancellationToken.None)).ErrorCode);
    }
}
