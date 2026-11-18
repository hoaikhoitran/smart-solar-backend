using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SmartSolar.Api.Contracts.InstallationWorkflow;
using SmartSolar.Api.Extensions;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.Quotations;
using SmartSolar.Modules.Common.Features;

namespace SmartSolar.Api.Controllers;

/// <summary>
/// Technician: the quotation of a survey the caller completed, built like a cart of catalog items (2026-11-17 simplified flow). Prices,
/// tax and totals always come from the backend; there is no price, discount or tax input. Requests of other technicians answer 404.
/// </summary>
[ApiController]
[Route("api/technician")]
[Authorize(Roles = RoleCodes.Technician)]
[RequireFeature(FeatureOptions.InstallationWorkflow)]
public sealed class TechnicianQuotationsController(QuotationHandlers handlers) : OperationResultControllerBase
{
    /// <summary>Creates the DRAFT cart for the completed survey (or returns the technician's existing draft).</summary>
    [HttpPost("survey-requests/{surveyRequestId:guid}/quotation")]
    [ProducesResponseType(typeof(SmartSolar.Api.Contracts.ApiResponse<QuotationView>), StatusCodes.Status201Created)]
    [EnableRateLimiting(RateLimitingExtensions.QuotationWritePolicy)]
    public async Task<IActionResult> Create(Guid surveyRequestId, [FromBody] TechnicianCartCreateRequest? request, CancellationToken cancellationToken)
        => CurrentUserId() is { } user
            ? Respond(await handlers.CreateCartAsync(user, surveyRequestId, request?.Notes, cancellationToken), StatusCodes.Status201Created)
            : UnauthorizedFailure();

    [HttpGet("survey-requests/{surveyRequestId:guid}/quotations")]
    [ProducesResponseType(typeof(SmartSolar.Api.Contracts.ApiResponse<IReadOnlyList<QuotationView>>), StatusCodes.Status200OK)]
    [EnableRateLimiting(RateLimitingExtensions.QuotationReadPolicy)]
    public async Task<IActionResult> List(Guid surveyRequestId, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await handlers.ListForTechnicianAsync(user, surveyRequestId, cancellationToken)) : UnauthorizedFailure();

    [HttpGet("quotations/{quotationId:guid}")]
    [ProducesResponseType(typeof(SmartSolar.Api.Contracts.ApiResponse<QuotationView>), StatusCodes.Status200OK)]
    [EnableRateLimiting(RateLimitingExtensions.QuotationReadPolicy)]
    public async Task<IActionResult> Get(Guid quotationId, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await handlers.GetForTechnicianAsync(user, quotationId, cancellationToken)) : UnauthorizedFailure();

    /// <summary>Adds a catalog product (its catalog price is used); the same product again increases its quantity.</summary>
    [HttpPost("quotations/{quotationId:guid}/lines")]
    [ProducesResponseType(typeof(SmartSolar.Api.Contracts.ApiResponse<QuotationView>), StatusCodes.Status200OK)]
    [EnableRateLimiting(RateLimitingExtensions.QuotationWritePolicy)]
    public async Task<IActionResult> AddLine(Guid quotationId, [FromBody] CartLineRequest request, CancellationToken cancellationToken)
    {
        if (CurrentUserId() is not { } user) return UnauthorizedFailure();
        if (request.ProductId is not { } productId || productId == Guid.Empty) return Invalid("productId", "'productId' is required.");
        if (request.Revision is not { } revision) return Invalid("revision", "'revision' is required.");
        return Respond(await handlers.AddCartLineAsync(user, quotationId, revision, productId, request.Quantity, cancellationToken));
    }

    [HttpPut("quotations/{quotationId:guid}/lines/{lineId:guid}")]
    [ProducesResponseType(typeof(SmartSolar.Api.Contracts.ApiResponse<QuotationView>), StatusCodes.Status200OK)]
    [EnableRateLimiting(RateLimitingExtensions.QuotationWritePolicy)]
    public async Task<IActionResult> UpdateLine(Guid quotationId, Guid lineId, [FromBody] CartLineQuantityRequest request, CancellationToken cancellationToken)
    {
        if (CurrentUserId() is not { } user) return UnauthorizedFailure();
        if (request.Revision is not { } revision) return Invalid("revision", "'revision' is required.");
        return Respond(await handlers.UpdateCartLineAsync(user, quotationId, lineId, revision, request.Quantity, cancellationToken));
    }

    [HttpDelete("quotations/{quotationId:guid}/lines/{lineId:guid}")]
    [ProducesResponseType(typeof(SmartSolar.Api.Contracts.ApiResponse<QuotationView>), StatusCodes.Status200OK)]
    [EnableRateLimiting(RateLimitingExtensions.QuotationWritePolicy)]
    public async Task<IActionResult> RemoveLine(Guid quotationId, Guid lineId, [FromQuery] int? revision, CancellationToken cancellationToken)
    {
        if (CurrentUserId() is not { } user) return UnauthorizedFailure();
        if (revision is not { } expected) return Invalid("revision", "'revision' is required.");
        return Respond(await handlers.RemoveCartLineAsync(user, quotationId, lineId, expected, cancellationToken));
    }

    /// <summary>
    /// Finalizes the cart: re-prices every line from the catalog (snapshot), computes the totals and freezes them for presentation on the
    /// technician's device. Nothing is sent to the customer's app.
    /// </summary>
    [HttpPost("quotations/{quotationId:guid}/issue")]
    [ProducesResponseType(typeof(SmartSolar.Api.Contracts.ApiResponse<QuotationView>), StatusCodes.Status200OK)]
    [EnableRateLimiting(RateLimitingExtensions.QuotationWritePolicy)]
    public async Task<IActionResult> Issue(Guid quotationId, [FromBody] RevisionRequest request, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await handlers.IssueCartAsync(user, quotationId, request.Revision, cancellationToken)) : UnauthorizedFailure();

    /// <summary>New draft version after the customer asked for changes on site (or declined) an issued quotation.</summary>
    [HttpPost("quotations/{quotationId:guid}/revisions")]
    [ProducesResponseType(typeof(SmartSolar.Api.Contracts.ApiResponse<QuotationView>), StatusCodes.Status201Created)]
    [EnableRateLimiting(RateLimitingExtensions.QuotationWritePolicy)]
    public async Task<IActionResult> CreateRevision(Guid quotationId, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await handlers.CreateCartRevisionAsync(user, quotationId, cancellationToken), StatusCodes.Status201Created) : UnauthorizedFailure();

    /// <summary>
    /// Records the customer's decision taken on site in front of the technician: AGREED (prices frozen; the payment can be opened), DECLINED or
    /// REVISION_NEEDED (note required). The actor recorded is the technician; the customer never approves in the app.
    /// </summary>
    [HttpPost("quotations/{quotationId:guid}/on-site-decision")]
    [ProducesResponseType(typeof(SmartSolar.Api.Contracts.ApiResponse<QuotationView>), StatusCodes.Status200OK)]
    [EnableRateLimiting(RateLimitingExtensions.QuotationWritePolicy)]
    public async Task<IActionResult> OnSiteDecision(Guid quotationId, [FromBody] OnSiteDecisionRequest request, CancellationToken cancellationToken)
    {
        if (CurrentUserId() is not { } user) return UnauthorizedFailure();
        if (request.Revision is not { } revision) return Invalid("revision", "'revision' is required.");
        return Respond(await handlers.RecordOnSiteDecisionAsync(user, quotationId, new OnSiteDecisionInput(request.Decision, revision, request.Note), cancellationToken));
    }

    /// <summary>
    /// Opens the payment of the AGREED quotation: ONLINE returns the PayOS <c>qrCode</c> / <c>checkoutUrl</c> to show on the technician's device;
    /// CASH records a pending cash choice that a Manager/Admin confirms. Repeating the same method returns the open payment. The contract is
    /// created automatically once the payment is verified (<c>contractId</c>).
    /// </summary>
    [HttpPost("quotations/{quotationId:guid}/payment")]
    [ProducesResponseType(typeof(SmartSolar.Api.Contracts.ApiResponse<QuotationPaymentView>), StatusCodes.Status200OK)]
    [EnableRateLimiting(RateLimitingExtensions.PaymentCreatePolicy)]
    public async Task<IActionResult> StartPayment(Guid quotationId, [FromBody] QuotationPaymentRequest request, [FromServices] OnSiteQuotationPaymentHandlers payments,
        CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await payments.StartAsync(user, quotationId, request.Method, cancellationToken)) : UnauthorizedFailure();

    /// <summary>Payment state of the quotation (an open online link is checked server-to-server with PayOS; a browser return URL never marks it paid).</summary>
    [HttpGet("quotations/{quotationId:guid}/payment")]
    [ProducesResponseType(typeof(SmartSolar.Api.Contracts.ApiResponse<QuotationPaymentView>), StatusCodes.Status200OK)]
    [EnableRateLimiting(RateLimitingExtensions.PaymentStatusPolicy)]
    public async Task<IActionResult> GetPayment(Guid quotationId, [FromServices] OnSiteQuotationPaymentHandlers payments, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await payments.GetAsync(user, quotationId, cancellationToken)) : UnauthorizedFailure();

    /// <summary>Cancels the open payment (switch method, or the customer stopped). A payment PayOS already reports paid is confirmed instead.</summary>
    [HttpPost("quotations/{quotationId:guid}/payment/cancel")]
    [ProducesResponseType(typeof(SmartSolar.Api.Contracts.ApiResponse<QuotationPaymentView>), StatusCodes.Status200OK)]
    [EnableRateLimiting(RateLimitingExtensions.PaymentCreatePolicy)]
    public async Task<IActionResult> CancelPayment(Guid quotationId, [FromServices] OnSiteQuotationPaymentHandlers payments, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await payments.CancelAsync(user, quotationId, cancellationToken)) : UnauthorizedFailure();
}

/// <summary>
/// Staff read access to quotations: Manager/Admin read every quotation; Sales (consult-only since 2026-11-17) reads the quotations of the
/// requests it owns. Every legacy Sales write endpoint (manual create/edit/issue, automatic generation, review queue, pricing approval) answers
/// 410 ENDPOINT_RETIRED: quotations are built by the surveying technician.
/// </summary>
[ApiController]
[Route("api")]
[Authorize(Roles = $"{RoleCodes.Sales},{RoleCodes.Manager},{RoleCodes.Admin}")]
[RequireFeature(FeatureOptions.InstallationWorkflow)]
public sealed class SalesQuotationsController(QuotationHandlers handlers) : OperationResultControllerBase
{
    private const string CartRoutes = "Technician cart: POST /api/technician/survey-requests/{surveyRequestId}/quotation, …/quotations/{id}/lines, …/issue, …/revisions";

    [HttpGet("survey-requests/{surveyRequestId:guid}/quotations")]
    [EnableRateLimiting(RateLimitingExtensions.QuotationReadPolicy)]
    public async Task<IActionResult> List(Guid surveyRequestId, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await handlers.ListForSurveyRequestAsync(user, IsManager(), surveyRequestId, cancellationToken)) : UnauthorizedFailure();

    [HttpGet("quotations/{quotationId:guid}")]
    [EnableRateLimiting(RateLimitingExtensions.QuotationReadPolicy)]
    public async Task<IActionResult> Get(Guid quotationId, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await handlers.GetForSalesAsync(user, IsManager(), quotationId, cancellationToken)) : UnauthorizedFailure();

    // ------------------------------------------------------------------ retired (410)

    [HttpPost("survey-requests/{surveyRequestId:guid}/quotations")]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    [EnableRateLimiting(RateLimitingExtensions.RetiredEndpointPolicy)]
    public IActionResult Create(Guid surveyRequestId) => Retired(CartRoutes);

    [HttpPost("survey-requests/{surveyRequestId:guid}/quotations/generate")]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    [EnableRateLimiting(RateLimitingExtensions.RetiredEndpointPolicy)]
    public IActionResult Generate(Guid surveyRequestId) => Retired(CartRoutes);

    [HttpGet("quotations/pending-review")]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    [EnableRateLimiting(RateLimitingExtensions.RetiredEndpointPolicy)]
    public IActionResult PendingReview() => Retired("GET /api/survey-requests/{surveyRequestId}/quotations (read) — no review queue exists any more");

    [HttpPut("quotations/{quotationId:guid}")]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    [EnableRateLimiting(RateLimitingExtensions.RetiredEndpointPolicy)]
    public IActionResult Update(Guid quotationId) => Retired(CartRoutes);

    [HttpPost("quotations/{quotationId:guid}/lines")]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    [EnableRateLimiting(RateLimitingExtensions.RetiredEndpointPolicy)]
    public IActionResult AddLine(Guid quotationId) => Retired(CartRoutes);

    [HttpPut("quotations/{quotationId:guid}/lines/{lineId:guid}")]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    [EnableRateLimiting(RateLimitingExtensions.RetiredEndpointPolicy)]
    public IActionResult UpdateLine(Guid quotationId, Guid lineId) => Retired(CartRoutes);

    [HttpDelete("quotations/{quotationId:guid}/lines/{lineId:guid}")]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    [EnableRateLimiting(RateLimitingExtensions.RetiredEndpointPolicy)]
    public IActionResult RemoveLine(Guid quotationId, Guid lineId) => Retired(CartRoutes);

    [HttpPost("quotations/{quotationId:guid}/issue")]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    [EnableRateLimiting(RateLimitingExtensions.RetiredEndpointPolicy)]
    public IActionResult Issue(Guid quotationId) => Retired(CartRoutes);

    [HttpPost("quotations/{quotationId:guid}/confirm-survey-version")]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    [EnableRateLimiting(RateLimitingExtensions.RetiredEndpointPolicy)]
    public IActionResult ConfirmSurveyVersion(Guid quotationId) => Retired("The technician's cart is re-based on the current survey when it is issued");

    [HttpPost("quotations/{quotationId:guid}/pricing-approval-request")]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    [EnableRateLimiting(RateLimitingExtensions.RetiredEndpointPolicy)]
    public IActionResult RequestPricingApproval(Guid quotationId) => Retired("None: quotations use catalog prices only (no discount or price exception)");

    [HttpPost("quotations/{quotationId:guid}/revisions")]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    [EnableRateLimiting(RateLimitingExtensions.RetiredEndpointPolicy)]
    public IActionResult CreateRevision(Guid quotationId) => Retired("POST /api/technician/quotations/{quotationId}/revisions");

    [HttpPut("quotations/{quotationId:guid}/document")]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    [EnableRateLimiting(RateLimitingExtensions.RetiredEndpointPolicy)]
    public IActionResult AttachDocument(Guid quotationId) => Retired("None: the quotation view carries every line and total for the FE to render");

    private bool IsManager() => IsInRole(RoleCodes.Manager) || IsInRole(RoleCodes.Admin);
}

/// <summary>Retired (2026-11-17): pricing exceptions no longer exist; quotations use catalog prices only. Both endpoints answer 410.</summary>
[ApiController]
[Route("api/manager/quotations")]
[Authorize(Roles = RoleCodes.Manager)]
[RequireFeature(FeatureOptions.InstallationWorkflow)]
public sealed class ManagerQuotationApprovalsController : OperationResultControllerBase
{
    [HttpGet("pricing-approvals")]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    [EnableRateLimiting(RateLimitingExtensions.RetiredEndpointPolicy)]
    public IActionResult Pending() => Retired("None: quotations use catalog prices only (no discount or price exception)");

    [HttpPost("{quotationId:guid}/pricing-approval")]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    [EnableRateLimiting(RateLimitingExtensions.RetiredEndpointPolicy)]
    public IActionResult Decide(Guid quotationId) => Retired("None: quotations use catalog prices only (no discount or price exception)");
}

/// <summary>
/// Retired (2026-11-18, technician-led on-site sales): the customer no longer receives, reads, accepts, rejects or sends back installation quotations
/// in the app. The technician presents the quotation on their device, records the outcome and opens the payment; the customer follows the paid
/// contract (<c>GET /api/customers/me/contracts</c>) and the installation. Every route answers 410 ENDPOINT_RETIRED.
/// </summary>
[ApiController]
[Route("api/customers/me/quotations")]
[Authorize(Roles = RoleCodes.Customer)]
[RequireFeature(FeatureOptions.InstallationWorkflow)]
public sealed class CustomerQuotationsController : OperationResultControllerBase
{
    private const string Replacement = "None for the customer: the technician records the on-site decision (POST /api/technician/quotations/{id}/on-site-decision); follow GET /api/customers/me/contracts";

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    [EnableRateLimiting(RateLimitingExtensions.RetiredEndpointPolicy)]
    public IActionResult List() => RetiredOnSite(Replacement);

    [HttpGet("{quotationId:guid}")]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    [EnableRateLimiting(RateLimitingExtensions.RetiredEndpointPolicy)]
    public IActionResult Get(Guid quotationId) => RetiredOnSite(Replacement);

    [HttpPost("{quotationId:guid}/accept")]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    [EnableRateLimiting(RateLimitingExtensions.RetiredEndpointPolicy)]
    public IActionResult Accept(Guid quotationId) => RetiredOnSite(Replacement);

    [HttpPost("{quotationId:guid}/reject")]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    [EnableRateLimiting(RateLimitingExtensions.RetiredEndpointPolicy)]
    public IActionResult Reject(Guid quotationId) => RetiredOnSite(Replacement);

    [HttpPost("{quotationId:guid}/revision-request")]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    [EnableRateLimiting(RateLimitingExtensions.RetiredEndpointPolicy)]
    public IActionResult RequestRevision(Guid quotationId) => RetiredOnSite(Replacement);
}
