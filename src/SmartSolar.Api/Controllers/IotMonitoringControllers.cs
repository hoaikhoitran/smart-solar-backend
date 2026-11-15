using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SmartSolar.Api.Contracts;
using SmartSolar.Api.Contracts.IotMonitoring;
using SmartSolar.Api.Extensions;
using SmartSolar.Modules.Common.Features;
using SmartSolar.Modules.Common.Paging;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.IotMonitoring;

namespace SmartSolar.Api.Controllers;

/// <summary>Admin device registry. Credential responses are sent with <c>Cache-Control: no-store</c>; the secret appears only there.</summary>
[ApiController]
[Route("api/admin/iot/devices")]
[Authorize(Roles = RoleCodes.Admin)]
[RequireFeature(FeatureOptions.InstallationWorkflow)]
public sealed class AdminIotDevicesController(IotDeviceHandlers devices) : OperationResultControllerBase
{
    [HttpPost]
    [ProducesResponseType(typeof(ApiResponse<IotDeviceCredentialView>), StatusCodes.Status201Created)]
    [EnableRateLimiting(RateLimitingExtensions.IotWritePolicy)]
    public async Task<IActionResult> Register([FromBody] RegisterIotDeviceRequest request, CancellationToken cancellationToken)
    {
        NoStore();
        if (CurrentUserId() is not { } admin) return UnauthorizedFailure();
        return Respond(await devices.RegisterAsync(admin, new DeviceRegistrationInput(request.DeviceId, request.DisplayName, request.SensorModel, request.MaxCurrentAmps), cancellationToken),
            StatusCodes.Status201Created);
    }

    /// <summary>GET /api/admin/iot/devices?status=ACTIVE|DISABLED&amp;search=&amp;page=1&amp;pageSize=20</summary>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<PagedResult<IotDeviceView>>), StatusCodes.Status200OK)]
    [EnableRateLimiting(RateLimitingExtensions.IotReadPolicy)]
    public async Task<IActionResult> List([FromQuery] string? status, [FromQuery] string? search, [FromQuery] int page = 1, [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
        => Respond(await devices.ListAsync(status, search, page, pageSize, cancellationToken));

    [HttpGet("{deviceId:guid}")]
    [ProducesResponseType(typeof(ApiResponse<IotDeviceView>), StatusCodes.Status200OK)]
    [EnableRateLimiting(RateLimitingExtensions.IotReadPolicy)]
    public async Task<IActionResult> Get(Guid deviceId, CancellationToken cancellationToken) => Respond(await devices.GetAsync(deviceId, cancellationToken));

    [HttpPost("{deviceId:guid}/disable")]
    [ProducesResponseType(typeof(ApiResponse<IotDeviceView>), StatusCodes.Status200OK)]
    [EnableRateLimiting(RateLimitingExtensions.IotWritePolicy)]
    public async Task<IActionResult> Disable(Guid deviceId, [FromBody] DisableIotDeviceRequest request, CancellationToken cancellationToken)
        => CurrentUserId() is { } admin ? Respond(await devices.DisableAsync(admin, deviceId, request.Reason, request.ExpectedRevision, cancellationToken)) : UnauthorizedFailure();

    [HttpPost("{deviceId:guid}/enable")]
    [ProducesResponseType(typeof(ApiResponse<IotDeviceView>), StatusCodes.Status200OK)]
    [EnableRateLimiting(RateLimitingExtensions.IotWritePolicy)]
    public async Task<IActionResult> Enable(Guid deviceId, [FromQuery] int? expectedRevision, CancellationToken cancellationToken)
        => CurrentUserId() is { } admin ? Respond(await devices.EnableAsync(admin, deviceId, expectedRevision, cancellationToken)) : UnauthorizedFailure();

    /// <summary>Issues a new secret at once (the old one stops working) and returns it once.</summary>
    [HttpPost("{deviceId:guid}/rotate-credential")]
    [ProducesResponseType(typeof(ApiResponse<IotDeviceCredentialView>), StatusCodes.Status200OK)]
    [EnableRateLimiting(RateLimitingExtensions.IotWritePolicy)]
    public async Task<IActionResult> Rotate(Guid deviceId, [FromQuery] int? expectedRevision, CancellationToken cancellationToken)
    {
        NoStore();
        return CurrentUserId() is { } admin ? Respond(await devices.RotateCredentialAsync(admin, deviceId, expectedRevision, cancellationToken)) : UnauthorizedFailure();
    }
}

/// <summary>Manager/Admin: bindings of devices to installation projects and the measured data of any project.</summary>
[ApiController]
[Route("api")]
[Authorize(Roles = $"{RoleCodes.Manager},{RoleCodes.Admin}")]
[RequireFeature(FeatureOptions.InstallationWorkflow)]
public sealed class StaffIotMonitoringController(IotBindingHandlers bindings, IotTelemetryHandlers telemetry) : OperationResultControllerBase
{
    /// <summary>Binds the device (by its firmware id) to the project's customer; 409 when the device or the customer already has an ACTIVE binding.</summary>
    [HttpPost("installation-projects/{projectId:guid}/iot-device")]
    [ProducesResponseType(typeof(ApiResponse<IotBindingView>), StatusCodes.Status201Created)]
    [EnableRateLimiting(RateLimitingExtensions.IotWritePolicy)]
    public async Task<IActionResult> Bind(Guid projectId, [FromBody] BindIotDeviceRequest request, CancellationToken cancellationToken)
        => CurrentUserId() is { } actor
            ? Respond(await bindings.BindAsync(actor, projectId, request.DeviceId, request.Note, request.ReplaceCurrent ?? false, cancellationToken), StatusCodes.Status201Created)
            : UnauthorizedFailure();

    /// <summary>GET /api/iot/bindings?projectId=&amp;deviceId=&amp;status=ACTIVE|RELEASED — the binding history (newest first).</summary>
    [HttpGet("iot/bindings")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<IotBindingView>>), StatusCodes.Status200OK)]
    [EnableRateLimiting(RateLimitingExtensions.IotReadPolicy)]
    public async Task<IActionResult> Bindings([FromQuery] Guid? projectId, [FromQuery] string? deviceId, [FromQuery] string? status, CancellationToken cancellationToken)
        => Respond(await bindings.ListAsync(projectId, deviceId, status, cancellationToken));

    [HttpPost("iot/bindings/{bindingId:guid}/release")]
    [ProducesResponseType(typeof(ApiResponse<IotBindingView>), StatusCodes.Status200OK)]
    [EnableRateLimiting(RateLimitingExtensions.IotWritePolicy)]
    public async Task<IActionResult> Release(Guid bindingId, [FromBody] ReleaseIotBindingRequest request, CancellationToken cancellationToken)
        => CurrentUserId() is { } actor ? Respond(await bindings.ReleaseAsync(actor, bindingId, request.Reason, cancellationToken)) : UnauthorizedFailure();

    [HttpGet("installation-projects/{projectId:guid}/energy-monitoring/device")]
    [ProducesResponseType(typeof(ApiResponse<IotProjectDeviceView>), StatusCodes.Status200OK)]
    [EnableRateLimiting(RateLimitingExtensions.IotReadPolicy)]
    public async Task<IActionResult> Device(Guid projectId, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await telemetry.GetProjectDeviceAsync(user, true, projectId, cancellationToken)) : UnauthorizedFailure();

    [HttpGet("installation-projects/{projectId:guid}/energy-monitoring/readings/latest")]
    [ProducesResponseType(typeof(ApiResponse<IotLatestReadingView>), StatusCodes.Status200OK)]
    [EnableRateLimiting(RateLimitingExtensions.IotReadPolicy)]
    public async Task<IActionResult> Latest(Guid projectId, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await telemetry.GetLatestAsync(user, true, projectId, cancellationToken)) : UnauthorizedFailure();

    /// <summary>GET …/readings?from=&amp;to=&amp;before=&amp;limit= — newest first; <c>before</c> is the cursor returned as <c>nextBefore</c>.</summary>
    [HttpGet("installation-projects/{projectId:guid}/energy-monitoring/readings")]
    [ProducesResponseType(typeof(ApiResponse<IotReadingHistoryView>), StatusCodes.Status200OK)]
    [EnableRateLimiting(RateLimitingExtensions.IotReadPolicy)]
    public async Task<IActionResult> History(Guid projectId, [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to, [FromQuery] DateTimeOffset? before,
        [FromQuery] int? limit, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await telemetry.GetHistoryAsync(user, true, projectId, from, to, before, limit, cancellationToken)) : UnauthorizedFailure();
}

/// <summary>Customer: the device bound to their own project and its readings (other customers' projects answer 404).</summary>
[ApiController]
[Route("api/customers/me/installation-projects/{projectId:guid}/energy-monitoring")]
[Authorize(Roles = RoleCodes.Customer)]
[RequireFeature(FeatureOptions.InstallationWorkflow)]
public sealed class CustomerIotMonitoringController(IotTelemetryHandlers telemetry) : OperationResultControllerBase
{
    [HttpGet("device")]
    [ProducesResponseType(typeof(ApiResponse<IotProjectDeviceView>), StatusCodes.Status200OK)]
    [EnableRateLimiting(RateLimitingExtensions.IotReadPolicy)]
    public async Task<IActionResult> Device(Guid projectId, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await telemetry.GetProjectDeviceAsync(user, false, projectId, cancellationToken)) : UnauthorizedFailure();

    [HttpGet("readings/latest")]
    [ProducesResponseType(typeof(ApiResponse<IotLatestReadingView>), StatusCodes.Status200OK)]
    [EnableRateLimiting(RateLimitingExtensions.IotReadPolicy)]
    public async Task<IActionResult> Latest(Guid projectId, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await telemetry.GetLatestAsync(user, false, projectId, cancellationToken)) : UnauthorizedFailure();

    [HttpGet("readings")]
    [ProducesResponseType(typeof(ApiResponse<IotReadingHistoryView>), StatusCodes.Status200OK)]
    [EnableRateLimiting(RateLimitingExtensions.IotReadPolicy)]
    public async Task<IActionResult> History(Guid projectId, [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to, [FromQuery] DateTimeOffset? before,
        [FromQuery] int? limit, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await telemetry.GetHistoryAsync(user, false, projectId, from, to, before, limit, cancellationToken)) : UnauthorizedFailure();
}

/// <summary>
/// Telemetry ingestion for devices. No user JWT: the device authenticates with its id in the path and its secret in the
/// <c>X-Device-Key</c> header (HTTPS only in production). 201 with the stored reading; 401 unknown device / wrong key, 403 disabled,
/// 409 not bound, 400 invalid value, 429 faster than the agreed 10 s cycle allows, 503 database unavailable (retry next cycle).
/// </summary>
[ApiController]
[Route("api/iot/devices/{deviceId}/readings")]
[AllowAnonymous]
[RequireFeature(FeatureOptions.InstallationWorkflow)]
public sealed class IotTelemetryController(IotTelemetryHandlers telemetry) : OperationResultControllerBase
{
    public const string DeviceKeyHeader = "X-Device-Key";

    [HttpPost]
    [ProducesResponseType(typeof(ApiResponse<IotIngestView>), StatusCodes.Status201Created)]
    [EnableRateLimiting(RateLimitingExtensions.IotTelemetryIngestPolicy)]
    public async Task<IActionResult> Ingest(string deviceId, [FromBody] TelemetryReadingRequest request, CancellationToken cancellationToken)
    {
        NoStore();
        var key = Request.Headers.TryGetValue(DeviceKeyHeader, out var values) ? values.ToString() : null;
        return Respond(await telemetry.IngestAsync(deviceId, key, request.Current, cancellationToken), StatusCodes.Status201Created);
    }
}
