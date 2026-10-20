using System.Text.Json;

namespace SmartSolar.Api.Contracts.SystemSettings;

/// <summary>
/// <c>PUT /api/admin/system-settings/scheduling</c>. Only the listed keys change (all-or-nothing); <c>expectedVersion</c> is the
/// version returned by GET (0 while the bootstrap defaults apply).
/// </summary>
public sealed record UpdateSchedulingSettingsRequest(IReadOnlyList<SystemSettingValueRequest>? Settings, int? ExpectedVersion, string? Reason);

/// <summary><c>value</c> must be a JSON integer; <c>unit</c> is optional and, when sent, must equal the setting's unit.</summary>
public sealed record SystemSettingValueRequest(string? Key, JsonElement Value, string? Unit);
