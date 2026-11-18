using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SmartSolar.Api.Contracts.AfterSales;
using SmartSolar.Api.Extensions;
using SmartSolar.Modules.AfterSales;
using SmartSolar.Modules.Common.Features;
using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.Payments;

namespace SmartSolar.Api.Controllers;

// Flow 3 — After-Sales Services. One controller per actor; every use case returns OperationResult (error code → HTTP status in
// OperationResultControllerBase). Ownership checks live in the handlers; role checks are the [Authorize] attributes below.

/// <summary>Customer: warranty, fault requests, quotations and payments, packages, maintenance bookings, acceptance.</summary>
[ApiController]
[Route("api/customers/me/after-sales")]
[Authorize(Roles = RoleCodes.Customer)]
[RequireFeature(FeatureOptions.InstallationWorkflow)]
public sealed class CustomerAfterSalesController(
    ServiceCustomerHandlers requests,
    ServiceQuotationHandlers quotations,
    ServicePackageHandlers packages,
    PaymentService payments,
    AfterSalesEvidenceUrlHandlers evidenceUrls) : OperationResultControllerBase
{
    [HttpGet("installations/{projectId:guid}/warranty")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationReadPolicy)]
    public async Task<IActionResult> Warranty(Guid projectId, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await requests.GetWarrantyAsync(user, projectId, cancellationToken)) : UnauthorizedFailure();

    /// <summary>Reports a fault. The response lists other open requests of the installation as a non-blocking duplicate warning.</summary>
    [HttpPost("fault-requests")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> CreateFault([FromBody] CreateFaultRequest body, CancellationToken cancellationToken)
        => CurrentUserId() is { } user
            ? Respond(await requests.CreateFaultAsync(user, new CreateFaultInput(body.ProjectId, body.Description, body.SuspectedAssetId, body.EvidenceFileIds,
                body.WarrantySourceRequestId, body.DisasterType, body.IncidentOccurredAt), cancellationToken), StatusCodes.Status201Created)
            : UnauthorizedFailure();

    [HttpPost("one-time-maintenance")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> OneTimeMaintenance([FromBody] OneTimeMaintenanceRequest body, CancellationToken cancellationToken)
        => CurrentUserId() is { } user
            ? Respond(await requests.RequestOneTimeMaintenanceAsync(user, body.ProjectId, body.PreferredStartsAt, body.Note, cancellationToken,
                body.InspectionRequired ?? true), StatusCodes.Status201Created)
            : UnauthorizedFailure();

    [HttpGet("requests")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationReadPolicy)]
    public async Task<IActionResult> List([FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken cancellationToken)
    {
        if (CurrentUserId() is not { } user) return UnauthorizedFailure();
        var paging = SmartSolar.Modules.Common.Paging.PageRequest.Parse(page, pageSize);
        if (!paging.Succeeded) return Respond(paging);
        return paging.Value is { } p
            ? Ok(Contracts.ApiResponse<object>.Success(await requests.PageAsync(user, p, cancellationToken), HttpContext.GetTraceId()))
            : Ok(Contracts.ApiResponse<object>.Success(await requests.ListAsync(user, cancellationToken), HttpContext.GetTraceId()));
    }

    [ProducesResponseType(typeof(SmartSolar.Api.Contracts.ApiResponse<SmartSolar.Modules.AfterSales.ServiceRequestView>), StatusCodes.Status200OK)]
    [HttpGet("requests/{requestId:guid}")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationReadPolicy)]
    public async Task<IActionResult> Get(Guid requestId, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await requests.GetAsync(user, requestId, cancellationToken)) : UnauthorizedFailure();

    /// <summary>A short-lived presigned GET URL for one evidence photo of the customer's own request (ids come from the request's <c>evidence</c>).</summary>
    [ProducesResponseType(typeof(SmartSolar.Api.Contracts.ApiResponse<SmartSolar.Modules.Files.AttachedFileUrlView>), StatusCodes.Status200OK)]
    [HttpGet("requests/{requestId:guid}/evidence/{evidenceId:guid}/url")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationReadPolicy)]
    public async Task<IActionResult> EvidenceUrl(Guid requestId, Guid evidenceId, [FromQuery] string? disposition, CancellationToken cancellationToken)
    {
        NoStore();
        if (CurrentUserId() is not { } user) return UnauthorizedFailure();
        if (ParseDisposition(disposition) is not { } parsed) return Invalid("disposition", "'disposition' must be inline or attachment.");
        return Respond(await evidenceUrls.ForCustomerAsync(user, requestId, evidenceId, parsed, cancellationToken));
    }

    [HttpPost("requests/{requestId:guid}/cancel")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> Cancel(Guid requestId, [FromBody] ReasonRequest body, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await requests.CancelAsync(user, requestId, body.Reason, cancellationToken)) : UnauthorizedFailure();

    /// <summary>Only the customer accepts completed work; a rejection needs a reason (and may carry photos).</summary>
    [HttpPost("requests/{requestId:guid}/completion")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> Completion(Guid requestId, [FromBody] CompletionDecisionRequest body, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await requests.DecideCompletionAsync(user, requestId, body.Accept, body.Reason, body.EvidenceFileIds, cancellationToken))
            : UnauthorizedFailure();

    [HttpPost("requests/{requestId:guid}/reconsideration")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> Reconsideration(Guid requestId, [FromBody] ReconsiderationRequest body, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await requests.RequestReconsiderationAsync(user, requestId, body.Note, body.EvidenceFileIds, cancellationToken))
            : UnauthorizedFailure();

    [HttpPost("requests/{requestId:guid}/confirm-appointment")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> ConfirmAppointment(Guid requestId, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await requests.ConfirmAppointmentAsync(user, requestId, cancellationToken)) : UnauthorizedFailure();

    [HttpPost("requests/{requestId:guid}/reschedule")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> Reschedule(Guid requestId, [FromBody] PreferredTimeRequest body, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await requests.RescheduleMaintenanceAsync(user, requestId, body.PreferredStartsAt, cancellationToken)) : UnauthorizedFailure();

    /// <summary>Opens a separate fault request for a problem the technician noticed during maintenance.</summary>
    [HttpPost("requests/{requestId:guid}/report-fault")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> ReportFault(Guid requestId, [FromBody] FaultFromMaintenanceRequest body, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await requests.ReportFaultFromMaintenanceAsync(user, requestId, body.Description, cancellationToken), StatusCodes.Status201Created)
            : UnauthorizedFailure();

    [HttpPost("quotations/{quotationId:guid}/accept")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> AcceptQuotation(Guid quotationId, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await quotations.AcceptAsync(user, quotationId, cancellationToken)) : UnauthorizedFailure();

    [HttpPost("quotations/{quotationId:guid}/reject")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> RejectQuotation(Guid quotationId, [FromBody] ReasonRequest body, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await quotations.RejectAsync(user, quotationId, body.Reason, cancellationToken)) : UnauthorizedFailure();

    /// <summary>PayOS checkout of an accepted quotation (idempotent). The return URL never marks it paid: poll the payment.</summary>
    /// <summary>Chooses to pay an ACCEPTED quotation in cash (pending until a Manager/Admin confirms the receipt, before the quotation expires).</summary>
    [HttpPost("quotations/{quotationId:guid}/cash")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> ChooseCash(Guid quotationId, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await quotations.SelectCashAsync(user, quotationId, cancellationToken)) : UnauthorizedFailure();

    /// <summary>Withdraws the pending cash choice of a quotation (to pay online instead).</summary>
    [HttpPost("quotations/{quotationId:guid}/cash/cancel")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> CancelCash(Guid quotationId, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await quotations.CancelCashAsync(user, quotationId, cancellationToken)) : UnauthorizedFailure();

    [HttpPost("quotations/{quotationId:guid}/checkout")]
    [EnableRateLimiting(RateLimitingExtensions.PaymentCreatePolicy)]
    public async Task<IActionResult> CheckoutQuotation(Guid quotationId, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await quotations.CheckoutAsync(user, quotationId, cancellationToken)) : UnauthorizedFailure();

    /// <summary>Current state of one of the customer's after-sales checkouts (reconciled with PayOS server-to-server when still open).</summary>
    [HttpGet("payments/{attemptId:guid}")]
    [EnableRateLimiting(RateLimitingExtensions.PaymentStatusPolicy)]
    public async Task<IActionResult> Payment(Guid attemptId, CancellationToken cancellationToken)
    {
        if (CurrentUserId() is not { } user) return UnauthorizedFailure();
        var view = await payments.GetAttemptForCustomerAsync(user, attemptId, reconcile: true, cancellationToken);
        return view is null
            ? Respond(Modules.Common.Results.OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.AttemptNotFound, "Payment was not found."))
            : Respond(Modules.Common.Results.OperationResult<PaymentAttemptView>.Ok(view));
    }

    [HttpGet("packages")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationReadPolicy)]
    public async Task<IActionResult> Packages([FromQuery] string? kind, CancellationToken cancellationToken)
        => Ok(Contracts.ApiResponse<object>.Success(await packages.ListPackagesAsync(kind, activeOnly: true, cancellationToken), HttpContext.GetTraceId()));

    /// <summary>Buys packages (optionally together with the signed contract payment) and returns the PayOS checkout.</summary>
    [HttpPost("purchases")]
    [EnableRateLimiting(RateLimitingExtensions.PaymentCreatePolicy)]
    public async Task<IActionResult> Purchase([FromBody] PurchaseRequest body, CancellationToken cancellationToken)
        => CurrentUserId() is { } user
            ? Respond(await packages.PurchaseAsync(user, new PurchaseInput(body.ContractId, body.MaintenancePackageId, body.DisasterPackageId, body.IncludeContract, body.PaymentMethod),
                cancellationToken), StatusCodes.Status201Created)
            : UnauthorizedFailure();

    [HttpGet("subscriptions")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationReadPolicy)]
    public async Task<IActionResult> Subscriptions(CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Ok(Contracts.ApiResponse<object>.Success(await packages.ListForCustomerAsync(user, cancellationToken), HttpContext.GetTraceId()))
            : UnauthorizedFailure();

    [HttpPost("subscriptions/{subscriptionId:guid}/cancel")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> CancelSubscription(Guid subscriptionId, [FromBody] ReasonRequest body, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await packages.RequestCancellationAsync(user, subscriptionId, body.Reason, cancellationToken)) : UnauthorizedFailure();

    /// <summary>Books an included maintenance visit for a preferred time (a request; a Manager plans the technician).</summary>
    [HttpPost("maintenance/{entitlementId:guid}/book")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> Book(Guid entitlementId, [FromBody] PreferredTimeRequest body, CancellationToken cancellationToken)
        => CurrentUserId() is { } user
            ? Respond(await requests.BookMaintenanceAsync(user, entitlementId, body.PreferredStartsAt, body.Note, cancellationToken), StatusCodes.Status201Created)
            : UnauthorizedFailure();

    [HttpGet("notifications")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationReadPolicy)]
    public async Task<IActionResult> Notifications([FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken cancellationToken)
    {
        if (CurrentUserId() is not { } user) return UnauthorizedFailure();
        var paging = SmartSolar.Modules.Common.Paging.PageRequest.Parse(page, pageSize);
        if (!paging.Succeeded) return Respond(paging);
        return paging.Value is { } p
            ? Ok(Contracts.ApiResponse<object>.Success(await requests.PageNotificationsAsync(user, p, cancellationToken), HttpContext.GetTraceId()))
            : Ok(Contracts.ApiResponse<object>.Success(await requests.NotificationsAsync(user, cancellationToken), HttpContext.GetTraceId()));
    }

    /// <summary>Marks one of the caller's notifications as read (idempotent; <c>readAt</c> is persisted). Another user's notification is 404.</summary>
    [ProducesResponseType(typeof(SmartSolar.Api.Contracts.ApiResponse<SmartSolar.Modules.AfterSales.NotificationView>), StatusCodes.Status200OK)]
    [HttpPost("notifications/{notificationId:guid}/read")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> MarkNotificationRead(Guid notificationId, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await requests.MarkNotificationReadAsync(user, notificationId, cancellationToken)) : UnauthorizedFailure();
}

/// <summary>
/// Technician-priced repair quotations (2026-11-18): the technician of a visit of a FAULT request prices its chargeable / disaster-protected items with
/// catalog products; the backend computes every amount. 404 for a technician without a visit on the request.
/// </summary>
[ApiController]
[Route("api/technicians/me/after-sales/requests")]
[Authorize(Roles = RoleCodes.Technician)]
[RequireFeature(FeatureOptions.InstallationWorkflow)]
public sealed class TechnicianAfterSalesQuotationsController(ServiceQuotationHandlers quotations) : OperationResultControllerBase
{
    [HttpPost("{requestId:guid}/quotations")]
    [EnableRateLimiting(RateLimitingExtensions.QuotationWritePolicy)]
    public async Task<IActionResult> Issue(Guid requestId, [FromBody] TechnicianQuotationRequest body, CancellationToken cancellationToken)
    {
        if (CurrentUserId() is not { } user) return UnauthorizedFailure();
        if (body.Lines is not { Count: > 0 } lines || lines.Any(l => l.ItemId is null || l.Components is not { Count: > 0 }
                                                                      || l.Components.Any(c => c.ProductId is null || c.Quantity is null)))
            return Invalid("lines", "Give lines with 'itemId' and 'components' [{ productId, quantity }].");
        var input = new TechnicianQuotationInput(lines.Select(l => new TechnicianQuotationLineInput(l.ItemId!.Value,
            l.Components!.Select(c => new QuotationComponentInput(c.ProductId!.Value, c.Quantity!.Value)).ToList(), l.Description)).ToList(), body.RepairWarrantyMonths, body.Note);
        return Respond(await quotations.IssueByTechnicianAsync(user, requestId, input, cancellationToken), StatusCodes.Status201Created);
    }
}

/// <summary>Technician: assigned after-sales visits only.</summary>
[ApiController]
[Route("api/technicians/me/after-sales/visits")]
[Authorize(Roles = RoleCodes.Technician)]
[RequireFeature(FeatureOptions.InstallationWorkflow)]
public sealed class TechnicianAfterSalesController(TechnicianServiceHandlers visits, AfterSalesEvidenceUrlHandlers evidenceUrls) : OperationResultControllerBase
{
    [HttpGet]
    [EnableRateLimiting(RateLimitingExtensions.InstallationReadPolicy)]
    public async Task<IActionResult> List([FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken cancellationToken)
    {
        if (CurrentUserId() is not { } user) return UnauthorizedFailure();
        var paging = SmartSolar.Modules.Common.Paging.PageRequest.Parse(page, pageSize);
        if (!paging.Succeeded) return Respond(paging);
        return Ok(Contracts.ApiResponse<object>.Success(paging.Value is { } p ? await visits.PageAsync(user, p, cancellationToken) : await visits.ListAsync(user, cancellationToken),
            HttpContext.GetTraceId()));
    }

    [ProducesResponseType(typeof(SmartSolar.Api.Contracts.ApiResponse<SmartSolar.Modules.AfterSales.TechnicianVisitView>), StatusCodes.Status200OK)]
    [HttpGet("{visitId:guid}")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationReadPolicy)]
    public async Task<IActionResult> Get(Guid visitId, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await visits.GetAsync(user, visitId, cancellationToken)) : UnauthorizedFailure();

    /// <summary>A short-lived presigned GET URL for one evidence photo of the visit's request (ids come from the visit's <c>evidence</c>).</summary>
    [ProducesResponseType(typeof(SmartSolar.Api.Contracts.ApiResponse<SmartSolar.Modules.Files.AttachedFileUrlView>), StatusCodes.Status200OK)]
    [HttpGet("{visitId:guid}/evidence/{evidenceId:guid}/url")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationReadPolicy)]
    public async Task<IActionResult> EvidenceUrl(Guid visitId, Guid evidenceId, [FromQuery] string? disposition, CancellationToken cancellationToken)
    {
        NoStore();
        if (CurrentUserId() is not { } user) return UnauthorizedFailure();
        if (ParseDisposition(disposition) is not { } parsed) return Invalid("disposition", "'disposition' must be inline or attachment.");
        return Respond(await evidenceUrls.ForTechnicianAsync(user, visitId, evidenceId, parsed, cancellationToken));
    }

    [HttpPost("{visitId:guid}/respond")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> Answer(Guid visitId, [FromBody] VisitResponseRequest body, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await visits.RespondAsync(user, visitId, body.Accept, body.Reason, cancellationToken)) : UnauthorizedFailure();

    [HttpPost("{visitId:guid}/start")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> Start(Guid visitId, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await visits.StartAsync(user, visitId, cancellationToken)) : UnauthorizedFailure();

    [HttpPost("{visitId:guid}/evidence")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> Evidence(Guid visitId, [FromBody] EvidenceRequest body, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await visits.AddEvidenceAsync(user, visitId, body.FileId, body.Caption, cancellationToken)) : UnauthorizedFailure();

    [HttpPost("{visitId:guid}/inspection")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> Inspection(Guid visitId, [FromBody] InspectionReportRequest body, CancellationToken cancellationToken)
        => CurrentUserId() is { } user
            ? Respond(await visits.ReportInspectionAsync(user, visitId, body.Report, body.NoFaultFound,
                (body.Items ?? []).Select(i => new DiagnosedItemInput(i.AssetId, i.Description, i.Diagnosis, i.RequiredParts)).ToList(), body.StayOnSite, cancellationToken))
            : UnauthorizedFailure();

    /// <summary>Same-visit repair: continue the inspection visit as the repair once the work is authorized and paid.</summary>
    [HttpPost("{visitId:guid}/continue-repair")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> ContinueRepair(Guid visitId, [FromBody] ContinueRepairRequest body, CancellationToken cancellationToken)
        => CurrentUserId() is { } user
            ? Respond(await visits.ContinueWithRepairAsync(user, visitId, body.EstimatedMinutes, body.PartsAvailable, cancellationToken))
            : UnauthorizedFailure();

    /// <summary>Ends an on-site wait without repairing; a Manager schedules a separate repair visit.</summary>
    [HttpPost("{visitId:guid}/finish")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> Finish(Guid visitId, [FromBody] FinishOnSiteRequest body, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await visits.FinishOnSiteWaitAsync(user, visitId, body.Note, cancellationToken)) : UnauthorizedFailure();

    [HttpPost("{visitId:guid}/supplementary-items")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> Supplementary(Guid visitId, [FromBody] DiagnosedItemRequest body, CancellationToken cancellationToken)
        => CurrentUserId() is { } user
            ? Respond(await visits.AddSupplementaryItemAsync(user, visitId, new DiagnosedItemInput(body.AssetId, body.Description, body.Diagnosis, body.RequiredParts), cancellationToken))
            : UnauthorizedFailure();

    [HttpPost("{visitId:guid}/work-report")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> WorkReport(Guid visitId, [FromBody] WorkReportRequest body, CancellationToken cancellationToken)
        => CurrentUserId() is { } user
            ? Respond(await visits.ReportWorkAsync(user, visitId, body.Report,
                (body.Results ?? []).Select(r => new ItemResultInput(r.ItemId, r.WorkResult, r.PartsUsed)).ToList(),
                (body.Replacements ?? []).Select(r => new ReplacementInput(r.ReplacedAssetId, r.Manufacturer, r.Model, r.SerialNumber, r.Specifications)).ToList(),
                body.FindingNote, cancellationToken))
            : UnauthorizedFailure();

    [HttpPost("{visitId:guid}/absence")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> Absence(Guid visitId, [FromBody] AbsenceRequest body, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await visits.ReportAbsenceAsync(user, visitId, body.Note, cancellationToken)) : UnauthorizedFailure();
}

/// <summary>Sales: the shared after-sales queue, quotations and customer follow-ups.</summary>
[ApiController]
[Route("api/sales/after-sales")]
[Authorize(Roles = RoleCodes.Sales)]
[RequireFeature(FeatureOptions.InstallationWorkflow)]
public sealed class SalesAfterSalesController(SalesTaskHandlers tasks, ServiceQuotationHandlers quotations, ServiceManagerHandlers requests,
    ServicePackageHandlers packages) : OperationResultControllerBase
{
    /// <summary>The active warranty durations Sales may choose (INSTALLATION for contracts, REPAIR for quotations).</summary>
    [HttpGet("warranty-options")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationReadPolicy)]
    public async Task<IActionResult> WarrantyOptions([FromQuery] string? kind, CancellationToken cancellationToken)
        => Ok(Contracts.ApiResponse<object>.Success((await packages.ListWarrantyOptionsAsync(kind, cancellationToken)).Where(o => o.IsActive).ToList(),
            HttpContext.GetTraceId()));

    [HttpGet("tasks/open")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationReadPolicy)]
    public async Task<IActionResult> Open([FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken cancellationToken)
    {
        var paging = SmartSolar.Modules.Common.Paging.PageRequest.Parse(page, pageSize);
        if (!paging.Succeeded) return Respond(paging);
        return Ok(Contracts.ApiResponse<object>.Success(paging.Value is { } p ? await tasks.PageOpenAsync(p, cancellationToken) : await tasks.ListOpenAsync(cancellationToken),
            HttpContext.GetTraceId()));
    }

    [HttpGet("tasks/mine")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationReadPolicy)]
    public async Task<IActionResult> Mine([FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken cancellationToken)
    {
        if (CurrentUserId() is not { } user) return UnauthorizedFailure();
        var paging = SmartSolar.Modules.Common.Paging.PageRequest.Parse(page, pageSize);
        if (!paging.Succeeded) return Respond(paging);
        return Ok(Contracts.ApiResponse<object>.Success(paging.Value is { } p ? await tasks.PageMineAsync(user, p, cancellationToken) : await tasks.ListMineAsync(user, cancellationToken),
            HttpContext.GetTraceId()));
    }

    [HttpPost("tasks/{taskId:guid}/claim")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> Claim(Guid taskId, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await tasks.ClaimAsync(user, taskId, cancellationToken)) : UnauthorizedFailure();

    [HttpPost("tasks/{taskId:guid}/start")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> Start(Guid taskId, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await tasks.StartAsync(user, taskId, cancellationToken)) : UnauthorizedFailure();

    [HttpPost("tasks/{taskId:guid}/follow-up")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> FollowUp(Guid taskId, [FromBody] FollowUpRequest body, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await tasks.RecordFollowUpAsync(user, taskId, new FollowUpInput(body.ContactedAt, body.Result, body.Note, body.Outcome), cancellationToken))
            : UnauthorizedFailure();

    [ProducesResponseType(typeof(SmartSolar.Api.Contracts.ApiResponse<SmartSolar.Modules.AfterSales.ServiceRequestView>), StatusCodes.Status200OK)]
    [HttpGet("requests/{requestId:guid}")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationReadPolicy)]
    public async Task<IActionResult> GetRequest(Guid requestId, CancellationToken cancellationToken) => Respond(await requests.GetAsync(requestId, cancellationToken));

    /// <summary>Issues (or supersedes with a new version) the quotation of a request whose queue task the caller owns.</summary>
    [HttpPost("requests/{requestId:guid}/quotations")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> Quote(Guid requestId, [FromBody] IssueQuotationRequest body, CancellationToken cancellationToken)
        => CurrentUserId() is { } user
            ? Respond(await quotations.IssueAsync(user, requestId, new IssueQuotationInput(
                (body.Lines ?? []).Select(l => new QuotationLineInput(l.ItemId, l.Description, l.PartsAmount, l.LaborAmount)).ToList(), body.RepairWarrantyMonths, body.Note),
                cancellationToken), StatusCodes.Status201Created)
            : UnauthorizedFailure();
}

/// <summary>Manager (and Admin): decisions, scheduling, reassignment, cancellations of unpaid work, equipment corrections. Refunds are not handled in this system.</summary>
[ApiController]
[Route("api/manager/after-sales")]
[Authorize(Roles = $"{RoleCodes.Manager},{RoleCodes.Admin}")]
[RequireFeature(FeatureOptions.InstallationWorkflow)]
public sealed class ManagerAfterSalesController(ServiceManagerHandlers requests, ServicePackageHandlers packages, SalesTaskHandlers tasks,
    AfterSalesEvidenceUrlHandlers evidenceUrls) : OperationResultControllerBase
{
    [HttpGet("requests")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationReadPolicy)]
    public async Task<IActionResult> List([FromQuery] string? status, [FromQuery] string? kind, [FromQuery] int? page, [FromQuery] int? pageSize,
        CancellationToken cancellationToken)
    {
        var paging = SmartSolar.Modules.Common.Paging.PageRequest.Parse(page, pageSize);
        if (!paging.Succeeded) return Respond(paging);
        return paging.Value is { } p
            ? Ok(Contracts.ApiResponse<object>.Success(await requests.PageAsync(status, kind, p, cancellationToken), HttpContext.GetTraceId()))
            : Ok(Contracts.ApiResponse<object>.Success(await requests.ListAsync(status, kind, cancellationToken), HttpContext.GetTraceId()));
    }

    [ProducesResponseType(typeof(SmartSolar.Api.Contracts.ApiResponse<SmartSolar.Modules.AfterSales.ServiceRequestView>), StatusCodes.Status200OK)]
    [HttpGet("requests/{requestId:guid}")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationReadPolicy)]
    public async Task<IActionResult> Get(Guid requestId, CancellationToken cancellationToken) => Respond(await requests.GetAsync(requestId, cancellationToken));

    /// <summary>A short-lived presigned GET URL for one evidence photo of any request.</summary>
    [ProducesResponseType(typeof(SmartSolar.Api.Contracts.ApiResponse<SmartSolar.Modules.Files.AttachedFileUrlView>), StatusCodes.Status200OK)]
    [HttpGet("requests/{requestId:guid}/evidence/{evidenceId:guid}/url")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationReadPolicy)]
    public async Task<IActionResult> EvidenceUrl(Guid requestId, Guid evidenceId, [FromQuery] string? disposition, CancellationToken cancellationToken)
    {
        NoStore();
        if (ParseDisposition(disposition) is not { } parsed) return Invalid("disposition", "'disposition' must be inline or attachment.");
        return Respond(await evidenceUrls.ForStaffAsync(requestId, evidenceId, parsed, cancellationToken));
    }

    [HttpPost("requests/{requestId:guid}/visits")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationSchedulingPolicy)]
    public async Task<IActionResult> Schedule(Guid requestId, [FromBody] ScheduleVisitRequest body, CancellationToken cancellationToken)
        => CurrentUserId() is { } user
            ? Respond(await requests.ScheduleVisitAsync(user, requestId, new ScheduleVisitInput(body.TechnicianUserId, body.StartsAt, body.EndsAt, body.Reason,
                body.ExpectedRevision, body.CrossZoneAuthorized, body.CompanyCaused), cancellationToken))
            : UnauthorizedFailure();

    [HttpPost("requests/{requestId:guid}/coverage")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> Coverage(Guid requestId, [FromBody] CoverageRequest body, CancellationToken cancellationToken)
        => CurrentUserId() is { } user
            ? Respond(await requests.DecideCoverageAsync(user, requestId, (body.Decisions ?? []).Select(d => new CoverageDecision(d.ItemId, d.Coverage ?? "", d.Reason)).ToList(),
                cancellationToken))
            : UnauthorizedFailure();

    [HttpPost("requests/{requestId:guid}/claim-decision")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> ClaimDecision(Guid requestId, [FromBody] DecisionRequest body, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await requests.DecideClaimAsync(user, requestId, body.Approve, body.Reason, cancellationToken)) : UnauthorizedFailure();

    [HttpPost("requests/{requestId:guid}/duplicate")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> Duplicate(Guid requestId, [FromBody] DuplicateRequest body, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await requests.MarkDuplicateAsync(user, requestId, body.OriginalRequestId, body.Reason, cancellationToken)) : UnauthorizedFailure();

    [HttpPost("requests/{requestId:guid}/dispute-review")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> DisputeReview(Guid requestId, [FromBody] DisputeReviewRequest body, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await requests.ReviewDisputeAsync(user, requestId, body.RequireRework, body.Note, cancellationToken)) : UnauthorizedFailure();

    /// <summary>Approval cancels unpaid work; paid work answers 409 AFTER_SALES_PAID_CANCELLATION_UNSUPPORTED (no refund workflow in this system).</summary>
    [HttpPost("requests/{requestId:guid}/cancellation-decision")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> CancellationDecision(Guid requestId, [FromBody] CancellationDecisionRequest body, CancellationToken cancellationToken)
        => CurrentUserId() is { } user
            ? Respond(await requests.DecideCancellationAsync(user, requestId, body.Approve, body.Note, cancellationToken))
            : UnauthorizedFailure();

    [HttpPost("requests/{requestId:guid}/stop")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> Stop(Guid requestId, [FromBody] StopWorkRequest body, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await requests.StopWorkAsync(user, requestId, body.Reason, cancellationToken)) : UnauthorizedFailure();

    [HttpPost("requests/{requestId:guid}/not-fulfilled")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> NotFulfilled(Guid requestId, [FromBody] ReasonRequest body, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await requests.ConfirmCannotFulfillAsync(user, requestId, body.Reason, cancellationToken)) : UnauthorizedFailure();

    /// <summary>Records the verified manufacturer-warranty activation date of a catalog-warranty asset (once, with a verified document and a reason).</summary>
    [HttpPost("assets/{assetId:guid}/warranty-activation")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> RecordWarrantyActivation(Guid assetId, [FromBody] WarrantyActivationRequest body, CancellationToken cancellationToken)
    {
        if (CurrentUserId() is not { } user) return UnauthorizedFailure();
        DateTimeOffset? activatedAt = null;
        if (body.ActivatedAt is not null)
        {
            if (!SmartSolar.Api.Contracts.Scheduling.IsoTimestamp.TryParse(body.ActivatedAt, out var parsed))
                return Invalid("activatedAt", "'activatedAt' must be ISO 8601 with an offset.");
            activatedAt = parsed;
        }
        return Respond(await requests.RecordWarrantyActivationAsync(user, assetId, activatedAt, body.EvidenceFileId, body.Reason, cancellationToken));
    }

    [HttpPut("assets/{assetId:guid}")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> CorrectAsset(Guid assetId, [FromBody] AssetCorrectionRequest body, CancellationToken cancellationToken)
        => CurrentUserId() is { } user
            ? Respond(await requests.CorrectAssetAsync(user, assetId, new AssetCorrection(body.Manufacturer, body.Model, body.SerialNumber, body.PositionCode, body.Specifications,
                body.Reason ?? ""), cancellationToken))
            : UnauthorizedFailure();

    /// <summary>Decides a pre-existing cancellation request of a paid package: rejection only (approval is 409 AFTER_SALES_PAID_CANCELLATION_UNSUPPORTED).</summary>
    [HttpPost("subscriptions/{subscriptionId:guid}/cancellation-decision")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> PackageCancellation(Guid subscriptionId, [FromBody] PackageCancellationDecisionRequest body, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await packages.DecideCancellationAsync(user, subscriptionId, body.Approve, body.Note, cancellationToken))
            : UnauthorizedFailure();

    /// <summary>Read-only, paged ledger of unapplied money (status optional: PENDING_APPROVAL, APPROVED, REJECTED, REFUNDED). Nothing is resolved here.</summary>
    [HttpGet("unapplied-money")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationReadPolicy)]
    public async Task<IActionResult> UnappliedMoney([FromQuery] string? status, [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken cancellationToken)
    {
        var paging = SmartSolar.Modules.Common.Paging.PageRequest.Parse(page ?? 1, pageSize);
        if (!paging.Succeeded) return Respond(paging);
        var s = string.IsNullOrWhiteSpace(status) ? null : status.Trim().ToUpperInvariant();
        if (s is not null && !RefundStatuses.All.Contains(s)) return Invalid("status", $"'status' must be one of {string.Join(", ", RefundStatuses.All)}.");
        return Ok(Contracts.ApiResponse<object>.Success(await requests.PageUnappliedMoneyAsync(s, paging.Value!, cancellationToken), HttpContext.GetTraceId()));
    }

    [HttpGet("tasks")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationReadPolicy)]
    public async Task<IActionResult> Tasks([FromQuery] string? status, [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken cancellationToken)
    {
        var paging = SmartSolar.Modules.Common.Paging.PageRequest.Parse(page, pageSize);
        if (!paging.Succeeded) return Respond(paging);
        return Ok(Contracts.ApiResponse<object>.Success(paging.Value is { } p ? await tasks.PageAllAsync(status, p, cancellationToken) : await tasks.ListAllAsync(status, cancellationToken),
            HttpContext.GetTraceId()));
    }

    [HttpPost("tasks/{taskId:guid}/assign")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> AssignTask(Guid taskId, [FromBody] AssignTaskRequest body, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await tasks.AssignAsync(user, taskId, body.SalesUserId, body.Reason, cancellationToken)) : UnauthorizedFailure();
}

/// <summary>Admin only: catalog, warranty durations and the dashboard (timings: the system-settings API).</summary>
[ApiController]
[Route("api/admin/after-sales")]
[Authorize(Roles = RoleCodes.Admin)]
[RequireFeature(FeatureOptions.InstallationWorkflow)]
public sealed class AdminAfterSalesController(ServicePackageHandlers packages, AfterSalesDashboard dashboard, SmartSolar.Modules.Files.FileService files,
    SmartSolar.Modules.Files.PublicFileService publicFiles, AdminOverview overview) : OperationResultControllerBase
{
    /// <summary>
    /// Operational overview (2026-11-18): status distributions now and payment / quotation figures of the period [from, to] (local dates, default this
    /// month, at most 366 days). Computed by the backend; definitions in ADMIN_DASHBOARD_METRICS.md. Payment rows: GET /api/manager/payments (paged).
    /// </summary>
    [HttpGet("dashboard/overview")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationReadPolicy)]
    public async Task<IActionResult> Overview([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken cancellationToken)
        => Respond(await overview.GetAsync(from, to, cancellationToken));

    [HttpGet("packages")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationReadPolicy)]
    public async Task<IActionResult> Packages([FromQuery] string? kind, CancellationToken cancellationToken)
        => Ok(Contracts.ApiResponse<object>.Success(await packages.ListPackagesAsync(kind, activeOnly: false, cancellationToken), HttpContext.GetTraceId()));

    [HttpPost("packages")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> CreatePackage([FromBody] PackageRequest body, CancellationToken cancellationToken)
    {
        if (CurrentUserId() is not { } user) return UnauthorizedFailure();
        var input = await ToInputAsync(body, cancellationToken);
        return input.Succeeded ? Respond(await packages.CreatePackageAsync(user, input.Value!, cancellationToken), StatusCodes.Status201Created) : Respond(input);
    }

    [HttpPut("packages/{packageId:guid}")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> UpdatePackage(Guid packageId, [FromBody] PackageRequest body, CancellationToken cancellationToken)
    {
        if (CurrentUserId() is not { } user) return UnauthorizedFailure();
        var input = await ToInputAsync(body, cancellationToken);
        return input.Succeeded ? Respond(await packages.UpdatePackageAsync(user, packageId, input.Value!, body.Revision, cancellationToken)) : Respond(input);
    }

    [HttpGet("warranty-options")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationReadPolicy)]
    public async Task<IActionResult> WarrantyOptions([FromQuery] string? kind, CancellationToken cancellationToken)
        => Ok(Contracts.ApiResponse<object>.Success(await packages.ListWarrantyOptionsAsync(kind, cancellationToken), HttpContext.GetTraceId()));

    [HttpPut("warranty-options")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> SaveWarrantyOption([FromBody] WarrantyOptionRequest body, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await packages.SaveWarrantyOptionAsync(user, body.Kind, body.Months, body.IsActive, body.IsDefault, cancellationToken))
            : UnauthorizedFailure();

    [HttpGet("dashboard")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationReadPolicy)]
    public async Task<IActionResult> Dashboard([FromQuery] DashboardQueryRequest query, CancellationToken cancellationToken)
        => Respond(await dashboard.GetAsync(query.ToQuery(), cancellationToken));

    /// <summary>The dashboard metrics as CSV, with the same filters.</summary>
    [HttpGet("dashboard/export")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationReadPolicy)]
    public async Task<IActionResult> Export([FromQuery] DashboardQueryRequest query, CancellationToken cancellationToken)
    {
        var csv = await dashboard.ExportCsvAsync(query.ToQuery(), cancellationToken);
        return csv.Succeeded ? File(Encoding.UTF8.GetBytes(csv.Value!), "text/csv", "after-sales-dashboard.csv") : Respond(csv);
    }

    /// <summary>An uploaded PACKAGE_IMAGE file (VERIFIED) becomes its stable public address; otherwise the given https link (or none) is kept.</summary>
    private async Task<OperationResult<PackageInput>> ToInputAsync(PackageRequest b, CancellationToken cancellationToken)
    {
        var imageUrl = string.IsNullOrWhiteSpace(b.ImageUrl) ? null : b.ImageUrl.Trim();
        if (b.ImageFileId is { } fileId)
        {
            if (imageUrl is not null)
                return OperationResult<PackageInput>.Fail(AfterSalesErrorCodes.ValidationFailed, "Give either imageUrl or imageFileId, not both.");
            var verified = await files.RequireVerifiedAsync([fileId], SmartSolar.Modules.Files.FilePurposes.PackageImage, null, cancellationToken);
            if (!verified.Succeeded) return verified.As<PackageInput>();
            var publicUrl = publicFiles.PublicUrlFor(verified.Value![0]);
            if (!publicUrl.Succeeded) return publicUrl.As<PackageInput>();
            imageUrl = publicUrl.Value;
        }
        return OperationResult<PackageInput>.Ok(new PackageInput(b.Kind, b.Name, b.Description, b.Price, b.DurationMonths, b.VisitMonths, b.CoveredDisasterTypes,
            b.CoveragePercent, b.PerIncidentLimit, b.TermLimit, b.IsActive, imageUrl));
    }
}

public sealed class DashboardQueryRequest
{
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    public DateOnly? CompareFrom { get; set; }
    public DateOnly? CompareTo { get; set; }
    public Guid? ZoneId { get; set; }
    public string? ServiceType { get; set; }
    public string? Status { get; set; }

    public DashboardQuery ToQuery() => new(From, To, CompareFrom, CompareTo, ZoneId, ServiceType, Status);
}
