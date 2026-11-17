using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.PreSurvey.Entities;
using SmartSolar.Modules.PreSurvey.Enums;
using SmartSolar.Modules.Scheduling.Constants;
using SmartSolar.Modules.Scheduling.PreSurveyAppointments;
using SmartSolar.Modules.Scheduling.Zones;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.SchedulingTestContext;

namespace SmartSolar.Tests.Unit.InstallationWorkflow;

/// <summary>
/// Option B (2026-11-17): a customer books an on-site survey directly for an own property site — no pre-survey, surface assessment or
/// simulation — through the same scheduling rules as Option A, and the request converges on the same on-site survey and technician cart.
/// </summary>
public sealed class DirectSurveyBookingTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>A customer (with a phone unless <paramref name="phone"/> is empty) and a site at <paramref name="location"/>; NO pre-survey.</summary>
    internal static async Task<(Modules.Identity.Entities.UserAccount Customer, PropertySite Site)> CustomerWithSiteAsync(InstallationWorkflowTestContext ctx,
        string name = "Direct customer", GeoPoint? location = null, bool withCodes = true, string? phone = "0901234567")
    {
        var point = location ?? BinhTan;
        var user = await ctx.Scheduling.Inner.SeedUserAsync(name, phone: phone);
        var customer = await ctx.Scheduling.Inner.SeedCustomerAsync(user.Id);
        var site = PreSurveyTestContext.NewPropertySite(customer.Id);
        site.Latitude = point.Latitude;
        site.Longitude = point.Longitude;
        if (withCodes)
        {
            await ctx.Scheduling.EnsureCatalogAsync();
            site.ProvinceCode = Province;
            site.CommuneCode = CommuneCodeOf(point);
        }
        ctx.Db.Add(site);
        await ctx.Db.SaveChangesAsync();
        ctx.Db.ChangeTracker.Clear();
        return (user, site);
    }

    /// <summary>Direct booking at 10:00 on <see cref="Day"/>, accepted by a Binh Tan technician: the same ConfirmedSurvey shape as Option A.</summary>
    internal static async Task<ConfirmedSurvey> ConfirmedDirectSurveyAsync(InstallationWorkflowTestContext ctx, string name = "Direct customer")
    {
        var technician = await ctx.Scheduling.SeedTechnicianAsync($"Tech for {name}", BinhTan);
        var (customer, site) = await CustomerWithSiteAsync(ctx, name);
        var booked = await ctx.DirectBooking().HandleAsync(new DirectSurveyBookingCommand(customer.Id, site.Id, At(10), null, null, "Gate code 1234"), None);
        Assert.True(booked.Succeeded, booked.Message);
        var accepted = await ctx.Scheduling.AcceptAsync(technician.Id, booked.Value!.Booking.AppointmentId);
        Assert.True(accepted.Succeeded, accepted.Message);
        var request = await ctx.Db.SurveyRequests.AsNoTracking().SingleAsync(r => r.Id == booked.Value.SurveyRequestId);
        return new ConfirmedSurvey(new SeededSurveyRequest(customer, site, null!, request), technician, null!, booked.Value.Booking.AppointmentId);
    }

    [Fact]
    public async Task A_direct_booking_needs_no_pre_survey_and_converges_on_the_on_site_survey_and_the_technician_cart()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var technician = await ctx.Scheduling.SeedTechnicianAsync("Binh Tan tech", BinhTan);
        var (customer, site) = await CustomerWithSiteAsync(ctx);
        var command = new DirectSurveyBookingCommand(customer.Id, site.Id, At(10), "Chị Lan (con gái)", "+84 912 345 678", "Gọi trước 30 phút");

        var booked = await ctx.DirectBooking().HandleAsync(command, None);
        Assert.True(booked.Succeeded, booked.Message);
        Assert.True(booked.Value!.RequestCreated);
        Assert.True(booked.Value.Booking.Created);
        Assert.Equal(SurveyRequestSources.DirectBooking, booked.Value.Source);
        Assert.Equal(AppointmentStatuses.PendingAcceptance, booked.Value.Booking.AppointmentStatus);
        var request = await ctx.Db.SurveyRequests.AsNoTracking().SingleAsync(r => r.Id == booked.Value.SurveyRequestId);
        Assert.Null(request.PreSurveyId);
        Assert.Equal(site.Id, request.PropertySiteId);
        Assert.Equal((SurveyRequestStatus.Pending, "Chị Lan (con gái)", "+84 912 345 678", (Guid?)null), (request.Status, request.ContactName, request.ContactPhone, request.AssignedSaleId));
        Assert.Equal(0, await ctx.Db.PreSurveys.CountAsync());   // no placeholder pre-survey

        // A retry (double tap / network) reuses the request and the booking.
        var retry = await ctx.DirectBooking().HandleAsync(command, None);
        Assert.True(retry.Succeeded, retry.Message);
        Assert.Equal((booked.Value.SurveyRequestId, false, false), (retry.Value!.SurveyRequestId, retry.Value.RequestCreated, retry.Value.Booking.Created));
        Assert.Equal(1, await ctx.Db.SurveyRequests.CountAsync());

        // The customer's list shows the source; the technician sees the on-site contact once confirmed.
        var mine = (await new CustomerSurveyRequestQueries(ctx.Scheduling.UnitOfWork, ctx.Scheduling.Calendar, ctx.Scheduling.Policies, ctx.Clock)
            .ListAsync(customer.Id, None)).Single();
        Assert.Equal((SurveyRequestSources.DirectBooking, (Guid?)null), (mine.Source, mine.PreSurveyId));
        Assert.True((await ctx.Scheduling.AcceptAsync(technician.Id, booked.Value.Booking.AppointmentId)).Succeeded);
        var job = await ctx.Scheduling.Jobs().GetAsync(technician.Id, booked.Value.Booking.AppointmentId, None);
        Assert.True(job.Succeeded, job.Message);
        Assert.Equal(SurveyRequestSources.DirectBooking, job.Value!.Source);
        Assert.Equal(("Chị Lan (con gái)", "+84 912 345 678"), (job.Value.OnSiteContact!.Name, job.Value.OnSiteContact.Phone));
        Assert.Null(job.Value.PreSurvey.PreSurveyId);
        Assert.Null(job.Value.SimulationDetailPath);

        // Same on-site survey and technician cart as Option A.
        var survey = new ConfirmedSurvey(new SeededSurveyRequest(customer, site, null!, request), technician, null!, booked.Value.Booking.AppointmentId);
        await OnSiteSurveyTests.CompleteAsync(ctx, survey);
        Assert.Equal(SurveyRequestStatus.Completed, (await ctx.Db.SurveyRequests.AsNoTracking().SingleAsync(r => r.Id == request.Id)).Status);
        await AutomaticQuotationFixture.SeedCatalogAsync(ctx);
        var cart = await ctx.Quotations().CreateCartAsync(technician.Id, request.Id, "Direct booking cart", None);
        Assert.True(cart.Succeeded, cart.Message);
        Assert.Equal(Modules.Quotations.QuotationOrigins.Technician, cart.Value!.Origin);
    }

    [Fact]
    public async Task Direct_booking_checks_ownership_location_contact_and_an_already_open_pre_survey_request()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        await ctx.Scheduling.SeedTechnicianAsync("Binh Tan tech", BinhTan);
        var (customer, site) = await CustomerWithSiteAsync(ctx);
        var (stranger, _) = await CustomerWithSiteAsync(ctx, "Stranger");
        var handler = ctx.DirectBooking();

        // Another customer's site, or an unknown one: 404, nothing created.
        Assert.Equal(SchedulingErrorCodes.PropertySiteNotFound,
            (await handler.HandleAsync(new DirectSurveyBookingCommand(stranger.Id, site.Id, At(10), null, null, null), None)).ErrorCode);
        Assert.Equal(SchedulingErrorCodes.PropertySiteNotFound,
            (await handler.HandleAsync(new DirectSurveyBookingCommand(customer.Id, Guid.NewGuid(), At(10), null, null, null), None)).ErrorCode);

        // A site without an official commune can never be scheduled: refused, no request opened.
        var (noCodes, unplaced) = await CustomerWithSiteAsync(ctx, "No commune", withCodes: false);
        var location = await handler.HandleAsync(new DirectSurveyBookingCommand(noCodes.Id, unplaced.Id, At(10), null, null, null), None);
        Assert.Equal(CoverageStatuses.SiteLocationRequired, location.ErrorCode);
        Assert.Equal(422, SmartSolar.Api.Controllers.SchedulingControllerBase.StatusFor(location.ErrorCode!));

        // No phone on the account and none given: the technician could not reach anyone.
        var (noPhone, noPhoneSite) = await CustomerWithSiteAsync(ctx, "No phone", phone: null);
        var phone = await handler.HandleAsync(new DirectSurveyBookingCommand(noPhone.Id, noPhoneSite.Id, At(10), null, null, null), None);
        Assert.Equal(DirectSurveyBookingHandler.ContactPhoneRequired, phone.ErrorCode);
        Assert.Equal(400, SmartSolar.Api.Controllers.SchedulingControllerBase.StatusFor(phone.ErrorCode!));
        Assert.Equal(0, await ctx.Db.SurveyRequests.CountAsync());

        // Validation.
        var validator = new DirectSurveyBookingCommandValidator();
        Assert.False((await validator.ValidateAsync(new DirectSurveyBookingCommand(customer.Id, Guid.Empty, default, new string('x', 201), "12ab", new string('n', 1001)))).IsValid);
        Assert.False((await validator.ValidateAsync(new DirectSurveyBookingCommand(customer.Id, site.Id, At(10), null, "123", null))).IsValid);
        Assert.True((await validator.ValidateAsync(new DirectSurveyBookingCommand(customer.Id, site.Id, At(10), null, "0901 234 567", null))).IsValid);

        // The site already has an open request from a pre-survey (Option A): book that one, never a second survey job.
        var seeded = await ctx.Scheduling.SeedSurveyRequestAsync(BinhTan, "Pre-survey customer");
        var open = await handler.HandleAsync(new DirectSurveyBookingCommand(seeded.Customer.Id, seeded.Site.Id, At(10), null, "0901234567", null), None);
        Assert.Equal(DirectSurveyBookingHandler.SurveyRequestAlreadyOpen, open.ErrorCode);
        Assert.Equal(409, SmartSolar.Api.Controllers.SchedulingControllerBase.StatusFor(open.ErrorCode!));
        Assert.Contains(seeded.Request.Id.ToString(), System.Text.Json.JsonSerializer.Serialize(open.Details));
        Assert.Equal(SurveyRequestSources.PreSurvey, (await ctx.Db.SurveyRequests.AsNoTracking().SingleAsync(r => r.Id == seeded.Request.Id)).Source);
    }

    [Fact]
    public async Task Without_capacity_nothing_is_confirmed_the_request_stays_pending_and_can_be_booked_or_dispatched_later()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        await ctx.Scheduling.EnsureZoneAsync(BinhTan);   // a zone with no technician yet
        var (customer, site) = await CustomerWithSiteAsync(ctx);

        var failed = await ctx.DirectBooking().HandleAsync(new DirectSurveyBookingCommand(customer.Id, site.Id, At(10), null, null, null), None);
        Assert.False(failed.Succeeded);
        Assert.Equal(CoverageStatuses.NoZoneTechnician, failed.ErrorCode);
        var details = System.Text.Json.JsonSerializer.SerializeToElement(failed.Details);
        var requestId = details.GetProperty("surveyRequestId").GetGuid();
        Assert.True(details.GetProperty("dispatchAvailable").GetBoolean());
        var request = await ctx.Db.SurveyRequests.AsNoTracking().SingleAsync(r => r.Id == requestId);
        Assert.Equal((SurveyRequestStatus.Pending, SurveyRequestSources.DirectBooking), (request.Status, request.Source));
        Assert.Equal(0, await ctx.Db.TechnicianAppointments.CountAsync());

        // A technician joins the zone: the same request is booked through the ordinary booking endpoint (or a direct retry reuses it).
        await ctx.Scheduling.SeedTechnicianAsync("Late joiner", BinhTan);
        var retried = await ctx.DirectBooking().HandleAsync(new DirectSurveyBookingCommand(customer.Id, site.Id, At(10), null, null, null), None);
        Assert.True(retried.Succeeded, retried.Message);
        Assert.Equal((requestId, false, true), (retried.Value!.SurveyRequestId, retried.Value.RequestCreated, retried.Value.Booking.Created));
        Assert.Equal(1, await ctx.Db.SurveyRequests.CountAsync());
    }

    [Fact]
    public async Task Option_A_still_records_its_pre_survey_and_site_and_both_sources_share_one_request_table()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var preSurvey = await ctx.QuotableSurveyAsync("Pre-survey customer");
        ctx.Clock.Now = DefaultNow.ToUniversalTime();
        var direct = await ConfirmedDirectSurveyAsync(ctx);
        var rows = await ctx.Db.SurveyRequests.AsNoTracking().OrderBy(r => r.Source).ToListAsync();
        Assert.Equal(2, rows.Count);
        var a = rows.Single(r => r.Source == SurveyRequestSources.PreSurvey);
        Assert.Equal((preSurvey.Request.PreSurvey.Id, preSurvey.Request.Site.Id), (a.PreSurveyId!.Value, a.PropertySiteId));
        var b = rows.Single(r => r.Source == SurveyRequestSources.DirectBooking);
        Assert.Equal(((Guid?)null, direct.Request.Site.Id), (b.PreSurveyId, b.PropertySiteId));

        // The submit handler copies the pre-survey's property onto the request.
        var user = await ctx.Scheduling.Inner.SeedUserAsync("Submitter");
        var customer = await ctx.Scheduling.Inner.SeedCustomerAsync(user.Id);
        var site = await ctx.Scheduling.Inner.SeedPropertySiteAsync(customer.Id);
        var draft = await ctx.Scheduling.Inner.SeedPreSurveyAsync(site.Id, PreSurveyStatus.Draft);
        var submitted = await new Modules.PreSurvey.SubmitPreSurvey.SubmitPreSurveyHandler(ctx.Scheduling.Inner.UnitOfWork)
            .HandleAsync(new Modules.PreSurvey.SubmitPreSurvey.SubmitPreSurveyCommand(user.Id, draft.Id), None);
        var created = await ctx.Db.SurveyRequests.AsNoTracking().SingleAsync(r => r.Id == submitted.SurveyRequestId);
        Assert.Equal((site.Id, SurveyRequestSources.PreSurvey, (Guid?)draft.Id), (created.PropertySiteId, created.Source, created.PreSurveyId));
    }
}
