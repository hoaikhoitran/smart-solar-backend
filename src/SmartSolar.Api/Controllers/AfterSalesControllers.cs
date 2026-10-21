using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SmartSolar.Api.Contracts.AfterSales;
using SmartSolar.Api.Extensions;
using SmartSolar.Modules.AfterSales;
using SmartSolar.Modules.Common.Features;
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
    PaymentService payments) : OperationResultControllerBase
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
            ? Respond(await requests.RequestOneTimeMaintenanceAsync(user, body.ProjectId, body.PreferredStartsAt, body.Note, cancellationToken), StatusCodes.Status201Created)
            : UnauthorizedFailure();

    [HttpGet("requests")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationReadPolicy)]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Ok(Contracts.ApiResponse<object>.Success(await requests.ListAsync(user, cancellationToken), HttpContext.GetTraceId())) : UnauthorizedFailure();

    [HttpGet("requests/{requestId:guid}")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationReadPolicy)]
    public async Task<IActionResult> Get(Guid requestId, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await requests.GetAsync(user, requestId, cancellationToken)) : UnauthorizedFailure();

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
            ? Respond(await packages.PurchaseAsync(user, new PurchaseInput(body.ContractId, body.MaintenancePackageId, body.DisasterPackageId, body.IncludeContract),
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
    public async Task<IActionResult> Notifications(CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Ok(Contracts.ApiResponse<object>.Success(await requests.NotificationsAsync(user, cancellationToken), HttpContext.GetTraceId()))
            : UnauthorizedFailure();
}

/// <summary>Technician: assigned after-sales visits only.</summary>
[ApiController]
[Route("api/technicians/me/after-sales/visits")]
[Authorize(Roles = RoleCodes.Technician)]
[RequireFeature(FeatureOptions.InstallationWorkflow)]
public sealed class TechnicianAfterSalesController(TechnicianServiceHandlers visits) : OperationResultControllerBase
{
    [HttpGet]
    [EnableRateLimiting(RateLimitingExtensions.InstallationReadPolicy)]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Ok(Contracts.ApiResponse<object>.Success(await visits.ListAsync(user, cancellationToken), HttpContext.GetTraceId())) : UnauthorizedFailure();

    [HttpGet("{visitId:guid}")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationReadPolicy)]
    public async Task<IActionResult> Get(Guid visitId, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await visits.GetAsync(user, visitId, cancellationToken)) : UnauthorizedFailure();

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
    public async Task<IActionResult> Open(CancellationToken cancellationToken)
        => Ok(Contracts.ApiResponse<object>.Success(await tasks.ListOpenAsync(cancellationToken), HttpContext.GetTraceId()));

    [HttpGet("tasks/mine")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationReadPolicy)]
    public async Task<IActionResult> Mine(CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Ok(Contracts.ApiResponse<object>.Success(await tasks.ListMineAsync(user, cancellationToken), HttpContext.GetTraceId())) : UnauthorizedFailure();

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

/// <summary>Manager (and Admin): decisions, scheduling, reassignment, cancellations, refund approvals, equipment corrections.</summary>
[ApiController]
[Route("api/manager/after-sales")]
[Authorize(Roles = $"{RoleCodes.Manager},{RoleCodes.Admin}")]
[RequireFeature(FeatureOptions.InstallationWorkflow)]
public sealed class ManagerAfterSalesController(ServiceManagerHandlers requests, ServicePackageHandlers packages, SalesTaskHandlers tasks, RefundHandlers refunds)
    : OperationResultControllerBase
{
    [HttpGet("requests")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationReadPolicy)]
    public async Task<IActionResult> List([FromQuery] string? status, [FromQuery] string? kind, CancellationToken cancellationToken)
        => Ok(Contracts.ApiResponse<object>.Success(await requests.ListAsync(status, kind, cancellationToken), HttpContext.GetTraceId()));

    [HttpGet("requests/{requestId:guid}")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationReadPolicy)]
    public async Task<IActionResult> Get(Guid requestId, CancellationToken cancellationToken) => Respond(await requests.GetAsync(requestId, cancellationToken));

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

    [HttpPost("requests/{requestId:guid}/cancellation-decision")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> CancellationDecision(Guid requestId, [FromBody] CancellationDecisionRequest body, CancellationToken cancellationToken)
        => CurrentUserId() is { } user
            ? Respond(await requests.DecideCancellationAsync(user, requestId, body.Approve, body.NonRecoverableAmount, body.Note, cancellationToken))
            : UnauthorizedFailure();

    [HttpPost("requests/{requestId:guid}/stop")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> Stop(Guid requestId, [FromBody] StopWorkRequest body, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await requests.StopWorkAsync(user, requestId, body.Reason, body.RefundAmount, cancellationToken)) : UnauthorizedFailure();

    [HttpPost("requests/{requestId:guid}/not-fulfilled")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> NotFulfilled(Guid requestId, [FromBody] ReasonRequest body, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await requests.ConfirmCannotFulfillAsync(user, requestId, body.Reason, cancellationToken)) : UnauthorizedFailure();

    [HttpPut("assets/{assetId:guid}")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> CorrectAsset(Guid assetId, [FromBody] AssetCorrectionRequest body, CancellationToken cancellationToken)
        => CurrentUserId() is { } user
            ? Respond(await requests.CorrectAssetAsync(user, assetId, new AssetCorrection(body.Manufacturer, body.Model, body.SerialNumber, body.PositionCode, body.Specifications,
                body.Reason ?? ""), cancellationToken))
            : UnauthorizedFailure();

    [HttpPost("subscriptions/{subscriptionId:guid}/cancellation-decision")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> PackageCancellation(Guid subscriptionId, [FromBody] PackageCancellationDecisionRequest body, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await packages.DecideCancellationAsync(user, subscriptionId, body.Approve, body.RefundAmount, body.Note, cancellationToken))
            : UnauthorizedFailure();

    [HttpGet("tasks")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationReadPolicy)]
    public async Task<IActionResult> Tasks([FromQuery] string? status, CancellationToken cancellationToken)
        => Ok(Contracts.ApiResponse<object>.Success(await tasks.ListAllAsync(status, cancellationToken), HttpContext.GetTraceId()));

    [HttpPost("tasks/{taskId:guid}/assign")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> AssignTask(Guid taskId, [FromBody] AssignTaskRequest body, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await tasks.AssignAsync(user, taskId, body.SalesUserId, body.Reason, cancellationToken)) : UnauthorizedFailure();

    [HttpGet("refunds")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationReadPolicy)]
    public async Task<IActionResult> Refunds([FromQuery] string? status, CancellationToken cancellationToken)
        => Ok(Contracts.ApiResponse<object>.Success(await refunds.ListAsync(status, cancellationToken), HttpContext.GetTraceId()));

    [HttpPost("refunds/{refundId:guid}/decision")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> RefundDecision(Guid refundId, [FromBody] RefundDecisionRequest body, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await refunds.DecideAsync(user, refundId, body.Approve, body.ApprovedAmount, body.Note, cancellationToken)) : UnauthorizedFailure();
}

/// <summary>Admin only: catalog, warranty durations, manual refund recording and the dashboard (timings: the system-settings API).</summary>
[ApiController]
[Route("api/admin/after-sales")]
[Authorize(Roles = RoleCodes.Admin)]
[RequireFeature(FeatureOptions.InstallationWorkflow)]
public sealed class AdminAfterSalesController(ServicePackageHandlers packages, RefundHandlers refunds, AfterSalesDashboard dashboard) : OperationResultControllerBase
{
    [HttpGet("packages")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationReadPolicy)]
    public async Task<IActionResult> Packages([FromQuery] string? kind, CancellationToken cancellationToken)
        => Ok(Contracts.ApiResponse<object>.Success(await packages.ListPackagesAsync(kind, activeOnly: false, cancellationToken), HttpContext.GetTraceId()));

    [HttpPost("packages")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> CreatePackage([FromBody] PackageRequest body, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await packages.CreatePackageAsync(user, ToInput(body), cancellationToken), StatusCodes.Status201Created) : UnauthorizedFailure();

    [HttpPut("packages/{packageId:guid}")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> UpdatePackage(Guid packageId, [FromBody] PackageRequest body, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await packages.UpdatePackageAsync(user, packageId, ToInput(body), body.Revision, cancellationToken)) : UnauthorizedFailure();

    [HttpGet("warranty-options")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationReadPolicy)]
    public async Task<IActionResult> WarrantyOptions([FromQuery] string? kind, CancellationToken cancellationToken)
        => Ok(Contracts.ApiResponse<object>.Success(await packages.ListWarrantyOptionsAsync(kind, cancellationToken), HttpContext.GetTraceId()));

    [HttpPut("warranty-options")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> SaveWarrantyOption([FromBody] WarrantyOptionRequest body, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await packages.SaveWarrantyOptionAsync(user, body.Kind, body.Months, body.IsActive, body.IsDefault, cancellationToken))
            : UnauthorizedFailure();

    /// <summary>Records that an approved refund was actually returned (executed outside the system; PayOS refunds are never called).</summary>
    [HttpPost("refunds/{refundId:guid}/record")]
    [EnableRateLimiting(RateLimitingExtensions.InstallationWritePolicy)]
    public async Task<IActionResult> RecordRefund(Guid refundId, [FromBody] RecordRefundRequest body, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await refunds.RecordAsync(user, refundId, body.RefundedAt, body.Reference, cancellationToken)) : UnauthorizedFailure();

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

    private static PackageInput ToInput(PackageRequest b)
        => new(b.Kind, b.Name, b.Description, b.Price, b.DurationMonths, b.VisitMonths, b.CoveredDisasterTypes, b.CoveragePercent, b.PerIncidentLimit, b.TermLimit, b.IsActive);
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
