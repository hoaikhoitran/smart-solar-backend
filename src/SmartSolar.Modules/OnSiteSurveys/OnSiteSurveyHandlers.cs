using System.Text.Json;
using Microsoft.Extensions.Logging;
using SmartSolar.Modules.Common.Persistence;
using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.Scheduling.Constants;

namespace SmartSolar.Modules.OnSiteSurveys;

public sealed record ObstacleInput(string Kind, string? Description, decimal? HeightM, decimal? AreaM2);

/// <summary>One confirmed line of the bill of materials: a catalog product and its quantity in the product's own unit. No price.</summary>
public sealed record BillOfMaterialsItem(Guid ProductId, decimal Quantity);

/// <summary>
/// The technician's confirmed equipment (<see cref="Materials"/>: panels, inverters, mounting, cables…) and priced service items
/// (<see cref="ServiceItems"/>: catalog products of type INSTALLATION_LABOR, TRANSPORT or OTHER_SERVICE). Prices always come from
/// the catalog when the quotation is generated.
/// </summary>
public sealed record BillOfMaterialsInput(IReadOnlyList<BillOfMaterialsItem>? Materials, IReadOnlyList<BillOfMaterialsItem>? ServiceItems, string? InstallationNotes);

/// <summary>Survey fields. In a draft every field is optional; completion requires the measured essentials.</summary>
public sealed record OnSiteSurveyData(
    string? RoofType, string? RoofMaterial, decimal? RoofLengthM, decimal? RoofWidthM, decimal? UsableAreaM2,
    decimal? TiltDegree, decimal? AzimuthDegree, string? RoofCondition, string? ElectricalCabinetNotes,
    string? ElectricalConnectionNotes, string? InstallationFeasibility, string? ProposedInverterLocation,
    string? ProposedPanelLayoutNotes, decimal? MeasuredLatitude, decimal? MeasuredLongitude, string? Notes,
    IReadOnlyList<ObstacleInput>? Obstacles, BillOfMaterialsInput? BillOfMaterials = null);

public sealed record SaveOnSiteSurveyCommand(Guid TechnicianUserId, Guid AppointmentId, int? ExpectedRevision, OnSiteSurveyData Data);
public sealed record AddOnSiteSurveyAttachmentCommand(Guid TechnicianUserId, Guid AppointmentId, Guid FileId, string Category, string? Caption);
public sealed record CompleteOnSiteSurveyCommand(Guid TechnicianUserId, Guid AppointmentId, int ExpectedRevision);
public sealed record AmendOnSiteSurveyCommand(Guid TechnicianUserId, Guid AppointmentId, string Reason, OnSiteSurveyData Changes, IReadOnlyList<Guid>? AdditionalFileIds, int ExpectedRevision);

public sealed record ObstacleView(string Kind, string? Description, decimal? HeightM, decimal? AreaM2);
public sealed record AttachmentView(Guid AttachmentId, Guid FileId, string Category, string? Caption, DateTimeOffset AddedAt, Guid? AmendmentId);
public sealed record AmendmentView(int AmendmentNumber, Guid AuthorUserId, string Reason, JsonElement Changes, DateTimeOffset CreatedAt);

public sealed record OnSiteSurveyView(
    Guid ReportId, Guid SurveyRequestId, Guid AppointmentId, Guid TechnicianUserId, string Status, int Version, int Revision,
    DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, OnSiteSurveyData Data, IReadOnlyList<ObstacleView> Obstacles,
    IReadOnlyList<AttachmentView> Attachments, IReadOnlyList<AmendmentView> Amendments, DateTimeOffset UpdatedAt,
    string? AutomaticQuotationOutcome = null);

public sealed class OnSiteSurveyOptions
{
    public const string SectionName = "OnSiteSurvey";
    /// <summary>
    /// Bootstrap default of the system setting <c>onsiteSurvey.earlyStartMinutes</c> (how long before the appointment start the technician
    /// may start the survey; also the release cut-off). Handlers read the effective value from ISchedulingPolicyProvider.
    /// </summary>
    public int EarlyStartMinutes { get; set; } = 120;
    public int MaxObstacles { get; set; } = 50;
    public int MaxAttachments { get; set; } = 60;
}

/// <summary>
/// Technician on-site survey lifecycle: DRAFT → IN_PROGRESS → COMPLETED, then amendments only.
/// Only the confirmed technician of the active Flow 1 appointment (ACCEPTED or ASSIGNED) may act.
/// Completion locks the report, moves the appointment to COMPLETED and the survey request to Completed in the same transaction and
/// makes it available to Sales for quotation (no approval step).
/// </summary>
public sealed class OnSiteSurveyHandlers(IOnSiteSurveyUnitOfWork unitOfWork, FileService files, OnSiteSurveyOptions options,
    SmartSolar.Modules.SystemSettings.ISchedulingPolicyProvider policies, TimeProvider time,
    Quotations.QuotationHandlers? quotations = null, Microsoft.Extensions.Logging.ILogger<OnSiteSurveyHandlers>? logger = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<OperationResult<OnSiteSurveyView?>> GetForTechnicianAsync(Guid technicianUserId, Guid appointmentId, CancellationToken cancellationToken)
    {
        var (context, error) = await AuthorizeAsync(technicianUserId, appointmentId, requireConfirmed: false, cancellationToken);
        if (error is not null) return error.As<OnSiteSurveyView?>();
        var report = await unitOfWork.FindReportBySurveyRequestAsync(context!.SurveyRequestId, false, cancellationToken);
        return OperationResult<OnSiteSurveyView?>.Ok(report is null ? null : await ViewAsync(report, cancellationToken));
    }

    public async Task<OperationResult<OnSiteSurveyView>> GetForStaffAsync(Guid surveyRequestId, Guid actorUserId, bool isManager, Func<Guid?, bool> isOwner, CancellationToken cancellationToken)
    {
        var report = await unitOfWork.FindReportBySurveyRequestAsync(surveyRequestId, false, cancellationToken);
        if (report is null) return OperationResult<OnSiteSurveyView>.Fail(OnSiteSurveyErrorCodes.ReportNotFound, "No on-site survey exists for this request.");
        var context = await unitOfWork.GetActiveAppointmentAsync(report.AppointmentId, cancellationToken);
        if (!isManager && !isOwner(context?.AssignedSaleId))
            return OperationResult<OnSiteSurveyView>.Fail(OnSiteSurveyErrorCodes.ReportNotFound, "No on-site survey exists for this request.");
        return OperationResult<OnSiteSurveyView>.Ok(await ViewAsync(report, cancellationToken));
    }

    /// <summary>Creates or updates the draft (DRAFT or IN_PROGRESS). Optimistic: ExpectedRevision must match an existing report.</summary>
    public async Task<OperationResult<OnSiteSurveyView>> SaveAsync(SaveOnSiteSurveyCommand command, CancellationToken cancellationToken)
    {
        var validation = Validate(command.Data, forCompletion: false);
        if (validation is not null) return OperationResult<OnSiteSurveyView>.Fail(CommonErrorCodes.ValidationFailed, "One or more fields are invalid.", validation);
        var (context, error) = await AuthorizeAsync(command.TechnicianUserId, command.AppointmentId, requireConfirmed: true, cancellationToken);
        if (error is not null) return error.As<OnSiteSurveyView>();

        return await MutateAsync(context!, command.ExpectedRevision, allowCreate: true, async (report, now, token) =>
        {
            if (report.Status == OnSiteSurveyStatuses.Completed)
                return OperationResult<bool>.Fail(OnSiteSurveyErrorCodes.AlreadyCompleted, "The survey is completed; submit an amendment instead.");
            ApplyData(report, command.Data);
            if (command.Data.Obstacles is { } obstacles) await unitOfWork.ReplaceObstaclesAsync(report.Id, Obstacles(report.Id, obstacles), token);
            // Like obstacles: an omitted bill of materials keeps the saved one.
            if (command.Data.BillOfMaterials is { } bom) report.BillOfMaterialsJson = SerializeBillOfMaterials(bom);
            return OperationResult<bool>.Ok(true);
        }, cancellationToken);
    }

    /// <summary>Records the actual start (DRAFT → IN_PROGRESS). Idempotent once started.</summary>
    public async Task<OperationResult<OnSiteSurveyView>> StartAsync(Guid technicianUserId, Guid appointmentId, CancellationToken cancellationToken)
    {
        var (context, error) = await AuthorizeAsync(technicianUserId, appointmentId, requireConfirmed: true, cancellationToken);
        if (error is not null) return error.As<OnSiteSurveyView>();
        // onsiteSurvey.earlyStartMinutes of the current settings (the same value is the technician's release cut-off).
        var earlyStart = Math.Max(0, (await policies.GetAsync(cancellationToken)).OnSiteSurveyEarlyStartMinutes);
        if (time.GetUtcNow() < context!.StartsAt.AddMinutes(-earlyStart))
            return OperationResult<OnSiteSurveyView>.Fail(OnSiteSurveyErrorCodes.TooEarly, $"The survey can start at most {earlyStart} minutes before the appointment.");

        return await MutateAsync(context, null, allowCreate: true, (report, now, _) =>
        {
            if (report.Status == OnSiteSurveyStatuses.Draft)
            {
                report.Status = OnSiteSurveyStatuses.InProgress;
                report.StartedAt = now;
            }
            return Task.FromResult(OperationResult<bool>.Ok(true));
        }, cancellationToken);
    }

    public async Task<OperationResult<OnSiteSurveyView>> AddAttachmentAsync(AddOnSiteSurveyAttachmentCommand command, CancellationToken cancellationToken)
    {
        var category = command.Category?.Trim().ToUpperInvariant() ?? "";
        if (!OnSiteSurveyVocabulary.AttachmentCategories.Contains(category))
            return OperationResult<OnSiteSurveyView>.Fail(CommonErrorCodes.ValidationFailed, $"Category must be one of {string.Join(", ", OnSiteSurveyVocabulary.AttachmentCategories)}.");
        var (context, error) = await AuthorizeAsync(command.TechnicianUserId, command.AppointmentId, requireConfirmed: true, cancellationToken);
        if (error is not null) return error.As<OnSiteSurveyView>();
        var verified = await files.RequireVerifiedAsync([command.FileId], FilePurposes.OnSiteSurveyEvidence, command.TechnicianUserId, cancellationToken);
        if (!verified.Succeeded) return verified.As<OnSiteSurveyView>();

        return await MutateAsync(context!, null, allowCreate: false, async (report, now, token) =>
        {
            if (report.Status == OnSiteSurveyStatuses.Completed)
                return OperationResult<bool>.Fail(OnSiteSurveyErrorCodes.AlreadyCompleted, "The survey is completed; add evidence through an amendment.");
            if ((await unitOfWork.GetAttachmentsAsync(report.Id, token)).Count >= options.MaxAttachments)
                return OperationResult<bool>.Fail(CommonErrorCodes.ValidationFailed, $"At most {options.MaxAttachments} attachments are allowed.");
            if (await unitOfWork.IsFileAttachedAsync(command.FileId, token))
                return OperationResult<bool>.Fail(FileErrorCodes.AlreadyLinked, "This file is already attached.");
            unitOfWork.AddAttachment(new OnSiteSurveyAttachment
            {
                Id = Guid.NewGuid(), ReportId = report.Id, FileId = command.FileId, Category = category,
                Caption = Trim(command.Caption, 300), AddedByUserId = command.TechnicianUserId, AddedAt = now,
            });
            return OperationResult<bool>.Ok(true);
        }, cancellationToken);
    }

    public async Task<OperationResult<OnSiteSurveyView>> RemoveAttachmentAsync(Guid technicianUserId, Guid appointmentId, Guid attachmentId, CancellationToken cancellationToken)
    {
        var (context, error) = await AuthorizeAsync(technicianUserId, appointmentId, requireConfirmed: true, cancellationToken);
        if (error is not null) return error.As<OnSiteSurveyView>();
        return await MutateAsync(context!, null, allowCreate: false, async (report, _, token) =>
        {
            if (report.Status == OnSiteSurveyStatuses.Completed)
                return OperationResult<bool>.Fail(OnSiteSurveyErrorCodes.AlreadyCompleted, "Completed survey evidence is kept as history.");
            return await unitOfWork.RemoveAttachmentAsync(report.Id, attachmentId, token)
                ? OperationResult<bool>.Ok(true)
                : OperationResult<bool>.Fail(OnSiteSurveyErrorCodes.ReportNotFound, "Attachment was not found.");
        }, cancellationToken);
    }

    /// <summary>
    /// IN_PROGRESS → COMPLETED. Requires the essential measurements and at least one ROOF photo. The
    /// survey request moves to Completed in the same transaction. Repeating it returns the completed report.
    /// </summary>
    public async Task<OperationResult<OnSiteSurveyView>> CompleteAsync(CompleteOnSiteSurveyCommand command, CancellationToken cancellationToken)
    {
        var (context, error) = await AuthorizeAsync(command.TechnicianUserId, command.AppointmentId, requireConfirmed: true, cancellationToken);
        if (error is not null) return error.As<OnSiteSurveyView>();

        var existing = await unitOfWork.FindReportBySurveyRequestAsync(context!.SurveyRequestId, false, cancellationToken);
        if (existing is { Status: OnSiteSurveyStatuses.Completed })
        {
            // Repeating the completion retries the automatic quotation (idempotent) without touching the survey.
            var again = await ViewAsync(existing, cancellationToken);
            return OperationResult<OnSiteSurveyView>.Ok(again with { AutomaticQuotationOutcome = await GenerateQuotationAsync(existing.SurveyRequestId, command.TechnicianUserId, cancellationToken) });
        }

        var completed = await MutateAsync(context, command.ExpectedRevision, allowCreate: false, async (report, now, token) =>
        {
            if (report.Status != OnSiteSurveyStatuses.InProgress)
                return OperationResult<bool>.Fail(OnSiteSurveyErrorCodes.NotStarted, "Start the survey on site before completing it.");
            var missing = Validate(DataOf(report, null), forCompletion: true);
            if (missing is not null) return OperationResult<bool>.Fail(OnSiteSurveyErrorCodes.Incomplete, "Required survey fields are missing or invalid.", missing);
            var attachments = await unitOfWork.GetAttachmentsAsync(report.Id, token);
            if (!attachments.Any(a => a.Category == "ROOF"))
                return OperationResult<bool>.Fail(OnSiteSurveyErrorCodes.EvidenceRequired, "At least one verified ROOF photo is required.");
            if (await CheckBillOfMaterialsAgainstCatalogAsync(ParseBillOfMaterials(report.BillOfMaterialsJson), token) is { } bomErrors)
                return OperationResult<bool>.Fail(OnSiteSurveyErrorCodes.BillOfMaterialsInvalid, "The bill of materials is not valid against the catalog.", bomErrors);

            report.Status = OnSiteSurveyStatuses.Completed;
            report.CompletedAt = now;
            report.Version = 1;
            // Conditional on the caller still being the confirmed technician: a reassignment that committed after the
            // authorization above makes this fail instead of completing someone else's appointment.
            if (!await unitOfWork.TryCompleteAppointmentAsync(report.AppointmentId, command.TechnicianUserId, now, token))
                return OperationResult<bool>.Fail(CommonErrorCodes.InvalidState, "The appointment is no longer accepted or assigned to you.");
            if (!await unitOfWork.TryCompleteSurveyRequestAsync(report.SurveyRequestId, token))
                return OperationResult<bool>.Fail(CommonErrorCodes.InvalidState, "The survey request is not in the Scheduled state.");
            return OperationResult<bool>.Ok(true);
        }, cancellationToken);
        if (!completed.Succeeded) return completed;

        // The survey is committed first; the quotation is generated in its own transaction (idempotent, retryable by
        // completing again or through the Sales "generate" endpoint), so a pricing problem never undoes the survey.
        return OperationResult<OnSiteSurveyView>.Ok(completed.Value! with
        {
            AutomaticQuotationOutcome = await GenerateQuotationAsync(completed.Value!.SurveyRequestId, command.TechnicianUserId, cancellationToken),
        });
    }

    /// <summary>Runs the automatic quotation when it is wired (production); returns its outcome, or null when not wired.</summary>
    private async Task<string?> GenerateQuotationAsync(Guid surveyRequestId, Guid actorUserId, CancellationToken cancellationToken)
    {
        if (quotations is null) return null;
        try
        {
            return (await quotations.GenerateStandardQuotationAsync(surveyRequestId, actorUserId, cancellationToken)).Outcome;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The survey stays completed; Sales can retry the generation. Nothing partial is stored (one transaction).
            logger?.LogError(ex, "Automatic quotation for survey request {SurveyRequestId} failed.", surveyRequestId);
            return Quotations.AutomaticQuotationOutcomes.Failed;
        }
    }

    /// <summary>Correction after completion: changed fields are applied, the previous values are recorded and Version is incremented.</summary>
    public async Task<OperationResult<OnSiteSurveyView>> AmendAsync(AmendOnSiteSurveyCommand command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Reason) || command.Reason.Length > 1000)
            return OperationResult<OnSiteSurveyView>.Fail(CommonErrorCodes.ValidationFailed, "A reason (at most 1000 characters) is required.");
        var validation = Validate(command.Changes, forCompletion: false);
        if (validation is not null) return OperationResult<OnSiteSurveyView>.Fail(CommonErrorCodes.ValidationFailed, "One or more fields are invalid.", validation);
        var (context, error) = await AuthorizeAsync(command.TechnicianUserId, command.AppointmentId, requireConfirmed: true, cancellationToken);
        if (error is not null) return error.As<OnSiteSurveyView>();
        var newFiles = command.AdditionalFileIds ?? [];
        if (newFiles.Count > 0)
        {
            var verified = await files.RequireVerifiedAsync(newFiles, FilePurposes.OnSiteSurveyEvidence, command.TechnicianUserId, cancellationToken);
            if (!verified.Succeeded) return verified.As<OnSiteSurveyView>();
        }

        var amended = await MutateAsync(context!, command.ExpectedRevision, allowCreate: false, async (report, now, token) =>
        {
            if (report.Status != OnSiteSurveyStatuses.Completed)
                return OperationResult<bool>.Fail(OnSiteSurveyErrorCodes.NotCompleted, "Only completed surveys are amended; edit the draft instead.");
            // Once a contract exists the survey is part of a commercial commitment: it is never changed underneath it. A post-contract change
            // needs the (not yet defined) commercial change process instead.
            if (context!.SurveyRequestStatus is nameof(PreSurvey.Enums.SurveyRequestStatus.Cancelled) or nameof(PreSurvey.Enums.SurveyRequestStatus.Closed))
                return OperationResult<bool>.Fail(OnSiteSurveyErrorCodes.RequestClosed, "The survey request was withdrawn or closed; its survey is history.");
            if (await unitOfWork.HasLiveContractAsync(context.SurveyRequestId, token))
                return OperationResult<bool>.Fail(OnSiteSurveyErrorCodes.CommerciallyLocked,
                    "A contract exists for this request; the survey can no longer be amended. A change after contracting needs a commercial change process.");
            var before = DataOf(report, null);
            var merged = Merge(before, command.Changes);
            var completeness = Validate(merged, forCompletion: true);
            if (completeness is not null) return OperationResult<bool>.Fail(CommonErrorCodes.ValidationFailed, "The amended survey would be incomplete.", completeness);

            var changes = Diff(before, merged);
            if (command.Changes.BillOfMaterials is { } bom)
            {
                if (await CheckBillOfMaterialsAgainstCatalogAsync(bom, token) is { } bomErrors)
                    return OperationResult<bool>.Fail(OnSiteSurveyErrorCodes.BillOfMaterialsInvalid, "The bill of materials is not valid against the catalog.", bomErrors);
                changes["billOfMaterials"] = new { old = ParseBillOfMaterials(report.BillOfMaterialsJson), @new = bom };
                report.BillOfMaterialsJson = SerializeBillOfMaterials(bom);
            }
            if (command.Changes.Obstacles is { } obstacles)
            {
                var old = (await unitOfWork.GetObstaclesAsync(report.Id, token)).Select(o => new ObstacleInput(o.Kind, o.Description, o.HeightM, o.AreaM2)).ToList();
                changes["obstacles"] = new { old, @new = obstacles };
                await unitOfWork.ReplaceObstaclesAsync(report.Id, Obstacles(report.Id, obstacles), token);
            }
            if (changes.Count == 0 && newFiles.Count == 0)
                return OperationResult<bool>.Fail(CommonErrorCodes.ValidationFailed, "The amendment changes nothing.");

            var amendment = new OnSiteSurveyAmendment
            {
                Id = Guid.NewGuid(), ReportId = report.Id, AmendmentNumber = report.Version, AuthorUserId = command.TechnicianUserId,
                Reason = command.Reason.Trim(), ChangesJson = JsonSerializer.Serialize(changes, Json), CreatedAt = now,
            };
            unitOfWork.AddAmendment(amendment);
            foreach (var fileId in newFiles.Distinct())
            {
                if (await unitOfWork.IsFileAttachedAsync(fileId, token))
                    return OperationResult<bool>.Fail(FileErrorCodes.AlreadyLinked, "A file is already attached.");
                unitOfWork.AddAttachment(new OnSiteSurveyAttachment
                {
                    Id = Guid.NewGuid(), ReportId = report.Id, FileId = fileId, Category = "DOCUMENT", AddedByUserId = command.TechnicianUserId,
                    AddedAt = now, AmendmentId = amendment.Id,
                });
            }
            ApplyData(report, merged);
            report.Version++;
            return OperationResult<bool>.Ok(true);
        }, cancellationToken);
        if (!amended.Succeeded) return amended;

        // Quotations based on the previous version stop being issuable/acceptable by themselves (version checks) and show up in the
        // Sales review queue; the SURVEY_AMENDED event is their audit record. It is idempotent and not critical: a failure is logged.
        if (quotations is not null)
        {
            try
            {
                await quotations.RecordSurveyAmendedAsync(amended.Value!.SurveyRequestId, amended.Value.Version, command.TechnicianUserId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogError(ex, "Recording the survey amendment on quotations of survey request {SurveyRequestId} failed.", amended.Value!.SurveyRequestId);
            }
        }
        return amended;
    }

    // ------------------------------------------------------------------ helpers

    private async Task<(SurveyAppointmentContext? Context, OperationResult<bool>? Error)> AuthorizeAsync(
        Guid technicianUserId, Guid appointmentId, bool requireConfirmed, CancellationToken cancellationToken)
    {
        var context = await unitOfWork.GetActiveAppointmentAsync(appointmentId, cancellationToken);
        if (context is null || context.TechnicianUserId != technicianUserId)
            return (null, OperationResult<bool>.Fail(OnSiteSurveyErrorCodes.AppointmentNotFound, "Appointment was not found."));
        // ACCEPTED / ASSIGNED: the caller is the confirmed technician. COMPLETED keeps amendments possible after completion.
        if (requireConfirmed && context.AppointmentStatus is not (AppointmentStatuses.Accepted or AppointmentStatuses.Assigned or AppointmentStatuses.Completed))
            return (null, OperationResult<bool>.Fail(OnSiteSurveyErrorCodes.NotConfirmed, "Accept the appointment (or be assigned to it) before working on the survey."));
        return (context, null);
    }

    private async Task<OperationResult<OnSiteSurveyView>> MutateAsync(
        SurveyAppointmentContext context, int? expectedRevision, bool allowCreate,
        Func<OnSiteSurveyReport, DateTimeOffset, CancellationToken, Task<OperationResult<bool>>> change, CancellationToken cancellationToken)
    {
        Guid reportId;
        try
        {
            var outcome = await unitOfWork.ExecuteInTransactionAsync<OperationResult<Guid>>(async token =>
            {
                var now = time.GetUtcNow();
                // Re-check under the technician lock: a staff reassignment committed after the authorization read wins.
                await unitOfWork.LockTechnicianAsync(context.TechnicianUserId, token);
                var current = await unitOfWork.GetActiveAppointmentAsync(context.AppointmentId, token);
                if (current is null || current.TechnicianUserId != context.TechnicianUserId
                    || current.AppointmentStatus is not (AppointmentStatuses.Accepted or AppointmentStatuses.Assigned or AppointmentStatuses.Completed))
                    return (OperationResult<Guid>.Fail(OnSiteSurveyErrorCodes.AppointmentNotFound, "Appointment was not found."), false);
                var report = await unitOfWork.FindReportBySurveyRequestAsync(context.SurveyRequestId, true, token);
                if (report is null)
                {
                    if (!allowCreate) return (OperationResult<Guid>.Fail(OnSiteSurveyErrorCodes.ReportNotFound, "Start or save the survey first."), false);
                    if (expectedRevision is not null) return (OperationResult<Guid>.Fail(OnSiteSurveyErrorCodes.StaleRevision, "The survey does not exist yet."), false);
                    report = new OnSiteSurveyReport
                    {
                        Id = Guid.NewGuid(), SurveyRequestId = context.SurveyRequestId, AppointmentId = context.AppointmentId,
                        TechnicianUserId = context.TechnicianUserId, Status = OnSiteSurveyStatuses.Draft, Version = 0, CreatedAt = now, UpdatedAt = now,
                    };
                    unitOfWork.AddReport(report);
                }
                else if (report.TechnicianUserId != context.TechnicianUserId || report.AppointmentId != context.AppointmentId)
                {
                    // Reassigned or rescheduled after a draft was started: the active assignment takes over the draft.
                    if (report.Status == OnSiteSurveyStatuses.Completed)
                        return (OperationResult<Guid>.Fail(OnSiteSurveyErrorCodes.AppointmentNotFound, "The survey was completed under another appointment."), false);
                    report.TechnicianUserId = context.TechnicianUserId;
                    report.AppointmentId = context.AppointmentId;
                }
                if (expectedRevision is { } revision && report.Revision != revision)
                    return (OperationResult<Guid>.Fail(OnSiteSurveyErrorCodes.StaleRevision, "The survey was changed meanwhile. Reload and try again.",
                        new { currentRevision = report.Revision }), false);

                var result = await change(report, now, token);
                if (!result.Succeeded) return (result.As<Guid>(), false);
                report.UpdatedAt = now;
                report.Revision++;
                await unitOfWork.SaveChangesAsync(token);
                return (OperationResult<Guid>.Ok(report.Id), true);
            }, cancellationToken);
            if (!outcome.Succeeded) return outcome.As<OnSiteSurveyView>();
            reportId = outcome.Value;
        }
        catch (PersistenceConflictException)
        {
            return OperationResult<OnSiteSurveyView>.Fail(OnSiteSurveyErrorCodes.StaleRevision, "The survey was changed meanwhile. Reload and try again.");
        }

        return OperationResult<OnSiteSurveyView>.Ok(await ViewAsync((await unitOfWork.FindReportAsync(reportId, false, cancellationToken))!, cancellationToken));
    }

    private async Task<OnSiteSurveyView> ViewAsync(OnSiteSurveyReport r, CancellationToken cancellationToken)
    {
        var obstacles = await unitOfWork.GetObstaclesAsync(r.Id, cancellationToken);
        var attachments = await unitOfWork.GetAttachmentsAsync(r.Id, cancellationToken);
        var amendments = await unitOfWork.GetAmendmentsAsync(r.Id, cancellationToken);
        return new OnSiteSurveyView(r.Id, r.SurveyRequestId, r.AppointmentId, r.TechnicianUserId, r.Status, r.Version, r.Revision, r.StartedAt, r.CompletedAt,
            DataOf(r, obstacles), obstacles.Select(o => new ObstacleView(o.Kind, o.Description, o.HeightM, o.AreaM2)).ToList(),
            attachments.Select(a => new AttachmentView(a.Id, a.FileId, a.Category, a.Caption, a.AddedAt, a.AmendmentId)).ToList(),
            amendments.Select(a => new AmendmentView(a.AmendmentNumber, a.AuthorUserId, a.Reason, JsonDocument.Parse(a.ChangesJson).RootElement.Clone(), a.CreatedAt)).ToList(),
            r.UpdatedAt);
    }

    private static OnSiteSurveyData DataOf(OnSiteSurveyReport r, IReadOnlyList<OnSiteSurveyObstacle>? obstacles) => new(
        r.RoofType, r.RoofMaterial, r.RoofLengthM, r.RoofWidthM, r.UsableAreaM2, r.TiltDegree, r.AzimuthDegree, r.RoofCondition,
        r.ElectricalCabinetNotes, r.ElectricalConnectionNotes, r.InstallationFeasibility, r.ProposedInverterLocation, r.ProposedPanelLayoutNotes,
        r.MeasuredLatitude, r.MeasuredLongitude, r.Notes,
        obstacles?.Select(o => new ObstacleInput(o.Kind, o.Description, o.HeightM, o.AreaM2)).ToList(),
        ParseBillOfMaterials(r.BillOfMaterialsJson));

    private static void ApplyData(OnSiteSurveyReport r, OnSiteSurveyData d)
    {
        r.RoofType = Trim(d.RoofType, 50); r.RoofMaterial = Trim(d.RoofMaterial, 50);
        r.RoofLengthM = d.RoofLengthM; r.RoofWidthM = d.RoofWidthM; r.UsableAreaM2 = d.UsableAreaM2;
        r.TiltDegree = d.TiltDegree; r.AzimuthDegree = d.AzimuthDegree;
        r.RoofCondition = d.RoofCondition?.Trim().ToUpperInvariant(); r.InstallationFeasibility = d.InstallationFeasibility?.Trim().ToUpperInvariant();
        r.ElectricalCabinetNotes = Trim(d.ElectricalCabinetNotes, 2000); r.ElectricalConnectionNotes = Trim(d.ElectricalConnectionNotes, 2000);
        r.ProposedInverterLocation = Trim(d.ProposedInverterLocation, 500); r.ProposedPanelLayoutNotes = Trim(d.ProposedPanelLayoutNotes, 2000);
        r.MeasuredLatitude = d.MeasuredLatitude; r.MeasuredLongitude = d.MeasuredLongitude; r.Notes = Trim(d.Notes, 4000);
    }

    /// <summary>Amendment fields: a non-null value replaces the current one; null keeps it.</summary>
    private static OnSiteSurveyData Merge(OnSiteSurveyData current, OnSiteSurveyData changes) => new(
        changes.RoofType ?? current.RoofType, changes.RoofMaterial ?? current.RoofMaterial, changes.RoofLengthM ?? current.RoofLengthM,
        changes.RoofWidthM ?? current.RoofWidthM, changes.UsableAreaM2 ?? current.UsableAreaM2, changes.TiltDegree ?? current.TiltDegree,
        changes.AzimuthDegree ?? current.AzimuthDegree, changes.RoofCondition ?? current.RoofCondition,
        changes.ElectricalCabinetNotes ?? current.ElectricalCabinetNotes, changes.ElectricalConnectionNotes ?? current.ElectricalConnectionNotes,
        changes.InstallationFeasibility ?? current.InstallationFeasibility, changes.ProposedInverterLocation ?? current.ProposedInverterLocation,
        changes.ProposedPanelLayoutNotes ?? current.ProposedPanelLayoutNotes, changes.MeasuredLatitude ?? current.MeasuredLatitude,
        changes.MeasuredLongitude ?? current.MeasuredLongitude, changes.Notes ?? current.Notes, null);

    private static Dictionary<string, object?> Diff(OnSiteSurveyData before, OnSiteSurveyData after)
    {
        var diff = new Dictionary<string, object?>();
        foreach (var property in typeof(OnSiteSurveyData).GetProperties()
                     .Where(p => p.Name is not (nameof(OnSiteSurveyData.Obstacles) or nameof(OnSiteSurveyData.BillOfMaterials))))
        {
            var (old, @new) = (property.GetValue(before), property.GetValue(after));
            if (!Equals(old, @new)) diff[JsonNamingPolicy.CamelCase.ConvertName(property.Name)] = new { old, @new };
        }
        return diff;
    }

    private List<OnSiteSurveyObstacle> Obstacles(Guid reportId, IReadOnlyList<ObstacleInput> inputs)
        => inputs.Take(options.MaxObstacles).Select((o, i) => new OnSiteSurveyObstacle
        {
            Id = Guid.NewGuid(), ReportId = reportId, SortOrder = i, Kind = o.Kind.Trim().ToUpperInvariant(),
            Description = Trim(o.Description, 500), HeightM = o.HeightM, AreaM2 = o.AreaM2,
        }).ToList();

    /// <summary>Range and vocabulary checks; with <paramref name="forCompletion"/> also the required fields.</summary>
    public static Dictionary<string, string[]>? Validate(OnSiteSurveyData d, bool forCompletion)
    {
        var errors = new Dictionary<string, string[]>();
        void Check(bool ok, string field, string message) { if (!ok) errors[field] = [message]; }

        Check(d.RoofLengthM is null || d.RoofLengthM is > 0 and <= 500, "roofLengthM", "Must be between 0 and 500 m.");
        Check(d.RoofWidthM is null || d.RoofWidthM is > 0 and <= 500, "roofWidthM", "Must be between 0 and 500 m.");
        Check(d.UsableAreaM2 is null || (d.UsableAreaM2 > 0 && (d.RoofLengthM is null || d.RoofWidthM is null || d.UsableAreaM2 <= d.RoofLengthM * d.RoofWidthM)),
            "usableAreaM2", "Must be positive and not exceed length × width.");
        Check(d.TiltDegree is null || d.TiltDegree is >= 0 and <= 90, "tiltDegree", "Must be between 0 and 90.");
        Check(d.AzimuthDegree is null || d.AzimuthDegree is >= 0 and < 360, "azimuthDegree", "Must be in [0, 360).");
        Check(d.MeasuredLatitude is null || d.MeasuredLatitude is >= -90 and <= 90, "measuredLatitude", "Invalid latitude.");
        Check(d.MeasuredLongitude is null || d.MeasuredLongitude is >= -180 and <= 180, "measuredLongitude", "Invalid longitude.");
        Check((d.MeasuredLatitude is null) == (d.MeasuredLongitude is null), "measuredLatitude", "Latitude and longitude go together.");
        Check(d.RoofCondition is null || OnSiteSurveyVocabulary.RoofConditions.Contains(d.RoofCondition.Trim().ToUpperInvariant()), "roofCondition",
            $"Must be one of {string.Join(", ", OnSiteSurveyVocabulary.RoofConditions)}.");
        Check(d.InstallationFeasibility is null || OnSiteSurveyVocabulary.Feasibility.Contains(d.InstallationFeasibility.Trim().ToUpperInvariant()), "installationFeasibility",
            $"Must be one of {string.Join(", ", OnSiteSurveyVocabulary.Feasibility)}.");
        if (d.Obstacles is { } obstacles)
        {
            Check(obstacles.Count <= 50, "obstacles", "At most 50 obstacles.");
            Check(obstacles.All(o => OnSiteSurveyVocabulary.ObstacleKinds.Contains(o.Kind?.Trim().ToUpperInvariant() ?? "")), "obstacles",
                $"Kinds must be one of {string.Join(", ", OnSiteSurveyVocabulary.ObstacleKinds)}.");
            Check(obstacles.All(o => o.HeightM is null or >= 0 and <= 100 && o.AreaM2 is null or >= 0 and <= 10000), "obstacles", "Invalid obstacle size.");
        }
        if (d.BillOfMaterials is { } bom)
        {
            var materials = bom.Materials ?? [];
            var services = bom.ServiceItems ?? [];
            var all = materials.Concat(services).ToList();
            Check(materials.Count + services.Count <= MaxBillOfMaterialsItems, "billOfMaterials", $"At most {MaxBillOfMaterialsItems} items.");
            Check(all.All(i => i.ProductId != Guid.Empty), "billOfMaterials", "Every item needs a catalog productId.");
            Check(all.All(i => i.Quantity is > 0 and <= MaxItemQuantity), "billOfMaterials", $"Every quantity must be greater than 0 and at most {MaxItemQuantity}.");
            Check(all.Select(i => i.ProductId).Distinct().Count() == all.Count, "billOfMaterials", "A product may appear only once; add the quantities together.");
            Check(bom.InstallationNotes is null || bom.InstallationNotes.Length <= 2000, "billOfMaterials", "installationNotes must be at most 2000 characters.");
        }

        if (forCompletion)
        {
            Check(!string.IsNullOrWhiteSpace(d.RoofType), "roofType", "Required.");
            Check(!string.IsNullOrWhiteSpace(d.RoofMaterial), "roofMaterial", "Required.");
            Check(d.RoofLengthM is not null && !errors.ContainsKey("roofLengthM"), "roofLengthM", "Required.");
            Check(d.RoofWidthM is not null && !errors.ContainsKey("roofWidthM"), "roofWidthM", "Required.");
            Check(d.UsableAreaM2 is not null && !errors.ContainsKey("usableAreaM2"), "usableAreaM2", "Required.");
            Check(d.TiltDegree is not null && !errors.ContainsKey("tiltDegree"), "tiltDegree", "Required.");
            Check(d.AzimuthDegree is not null && !errors.ContainsKey("azimuthDegree"), "azimuthDegree", "Required.");
            Check(d.RoofCondition is not null && !errors.ContainsKey("roofCondition"), "roofCondition", "Required.");
            Check(d.InstallationFeasibility is not null && !errors.ContainsKey("installationFeasibility"), "installationFeasibility", "Required.");
            Check(!string.IsNullOrWhiteSpace(d.ElectricalCabinetNotes), "electricalCabinetNotes", "Required.");
        }

        return errors.Count == 0 ? null : errors;
    }

    public const int MaxBillOfMaterialsItems = 100;
    public const decimal MaxItemQuantity = 100_000m;

    /// <summary>Units counted in whole pieces: their quantity must be a whole number (no 1.5 panels).</summary>
    private static readonly string[] CountedUnits = ["PCS", "PC", "SET", "UNIT", "EA"];

    private static string SerializeBillOfMaterials(BillOfMaterialsInput bom)
        => JsonSerializer.Serialize(new BillOfMaterialsInput(bom.Materials ?? [], bom.ServiceItems ?? [], Trim(bom.InstallationNotes, 2000)), Json);

    public static BillOfMaterialsInput? ParseBillOfMaterials(string? json)
        => string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<BillOfMaterialsInput>(json, Json);

    /// <summary>
    /// Every item must be an existing, active catalog product of the right kind: materials are equipment (never a service type),
    /// service items are INSTALLATION_LABOR / TRANSPORT / OTHER_SERVICE; counted units need whole quantities. Prices are not checked
    /// here: a missing price is a quotation exception for Sales, not a technician error.
    /// </summary>
    private async Task<Dictionary<string, string[]>?> CheckBillOfMaterialsAgainstCatalogAsync(BillOfMaterialsInput? bom, CancellationToken token)
    {
        if (bom is null) return null;
        var materials = bom.Materials ?? [];
        var services = bom.ServiceItems ?? [];
        var ids = materials.Concat(services).Select(i => i.ProductId).Distinct().ToList();
        if (ids.Count == 0) return null;
        var products = (await unitOfWork.GetCatalogProductsAsync(ids, token)).ToDictionary(p => p.ProductId);
        var errors = new Dictionary<string, string[]>();
        void Check(IReadOnlyList<BillOfMaterialsItem> items, string field, bool mustBeService)
        {
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                string? problem = !products.TryGetValue(item.ProductId, out var product) ? "is not a catalog product"
                    : !product.IsActive ? "is not active in the catalog"
                    : Catalog.Constants.ProductTypes.IsService(product.ProductType) != mustBeService
                        ? (mustBeService ? "is not a service product (INSTALLATION_LABOR, TRANSPORT, OTHER_SERVICE)" : "is a service; list it under serviceItems")
                    : CountedUnits.Contains(product.Unit.Trim().ToUpperInvariant()) && decimal.Truncate(item.Quantity) != item.Quantity
                        ? $"is counted in {product.Unit}; the quantity must be a whole number"
                    : null;
                if (problem is not null) errors[$"billOfMaterials.{field}[{i}]"] = [$"Product {item.ProductId} {problem}."];
            }
        }
        Check(materials, "materials", mustBeService: false);
        Check(services, "serviceItems", mustBeService: true);
        return errors.Count == 0 ? null : errors;
    }

    private static string? Trim(string? value, int max)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim() is var t && t.Length > max ? t[..max] : value.Trim();
}
