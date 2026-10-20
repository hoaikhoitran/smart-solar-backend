using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SmartSolar.Api.Contracts.SystemSettings;
using SmartSolar.Api.Extensions;
using SmartSolar.Modules.Common.Paging;
using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.SystemSettings;

namespace SmartSolar.Api.Controllers;

/// <summary>
/// Admin timing policy (scheduling, dispatch, on-site survey, installation durations). Managers may read; only Admins may change
/// values. Settings apply to new timing events only; existing deadlines are snapshots.
/// </summary>
[ApiController]
[Route("api/admin/system-settings")]
[Authorize(Roles = $"{RoleCodes.Admin},{RoleCodes.Manager}")]
public sealed class AdminSystemSettingsController(SystemSettingsService service) : OperationResultControllerBase
{
    [HttpGet("scheduling")]
    [EnableRateLimiting(RateLimitingExtensions.SystemSettingsReadPolicy)]
    public async Task<IActionResult> GetScheduling(CancellationToken cancellationToken)
        => Respond(OperationResult<SchedulingSettingsView>.Ok(await service.GetSchedulingAsync(cancellationToken)));

    [HttpPut("scheduling")]
    [Authorize(Roles = RoleCodes.Admin)]
    [EnableRateLimiting(RateLimitingExtensions.SystemSettingsWritePolicy)]
    public async Task<IActionResult> UpdateScheduling([FromBody] UpdateSchedulingSettingsRequest request, CancellationToken cancellationToken)
    {
        if (CurrentUserId() is not { } admin) return UnauthorizedFailure();
        var changes = request.Settings?.Select(s => new SystemSettingChangeInput(s.Key, AsInt(s.Value), s.Unit)).ToList();
        return Respond(await service.UpdateSchedulingAsync(admin, new SchedulingSettingsUpdateInput(changes, request.ExpectedVersion, request.Reason), cancellationToken));
    }

    [HttpGet("scheduling/history")]
    [EnableRateLimiting(RateLimitingExtensions.SystemSettingsReadPolicy)]
    public async Task<IActionResult> History([FromQuery] int page = 1, [FromQuery] int pageSize = 20, [FromQuery] string? key = null,
        CancellationToken cancellationToken = default)
        => Respond(OperationResult<PagedResult<SystemSettingRevisionView>>.Ok(await service.HistoryAsync(page, pageSize, key, cancellationToken)));

    /// <summary>A JSON integer within Int32; anything else (string, decimal, bool, null, missing) is reported as "must be an integer".</summary>
    private static int? AsInt(JsonElement value)
        => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
}
