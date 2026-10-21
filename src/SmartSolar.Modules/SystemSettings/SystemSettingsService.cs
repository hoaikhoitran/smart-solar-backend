using Microsoft.Extensions.Logging;
using SmartSolar.Modules.Common.Paging;
using SmartSolar.Modules.Common.Persistence;
using SmartSolar.Modules.Common.Results;

namespace SmartSolar.Modules.SystemSettings;

public static class SystemSettingErrorCodes
{
    /// <summary>The settings changed since the caller read them (expectedVersion is stale, or a concurrent update won).</summary>
    public const string VersionConflict = "SETTINGS_VERSION_CONFLICT";
    /// <summary>Malformed request: missing expectedVersion, empty or duplicate settings, reason too long.</summary>
    public const string ValidationFailed = "SYSTEM_SETTING_VALIDATION_FAILED";
    public const string UnknownKey = "SYSTEM_SETTING_UNKNOWN_KEY";
    public const string NotEditable = "SYSTEM_SETTING_NOT_EDITABLE";
    public const string InvalidUnit = "SYSTEM_SETTING_INVALID_UNIT";
    /// <summary>Not an integer, or outside the setting's range.</summary>
    public const string InvalidValue = "SYSTEM_SETTING_INVALID_VALUE";
    /// <summary>A cross-field rule (for example minimum booking lead below the booking horizon) is violated.</summary>
    public const string RuleViolation = "SYSTEM_SETTING_RULE_VIOLATION";
}

public static class SystemSettingSources
{
    public const string Default = "DEFAULT";
    public const string Admin = "ADMIN";
}

public sealed record SystemSettingView(string Key, string Type, string Unit, int Value, int DefaultValue, int Min, int Max, string Source, bool AdminEditable,
    string Description, int Version, DateTimeOffset? UpdatedAt, Guid? UpdatedByUserId);

/// <param name="Version">Global settings version (0 = bootstrap defaults); send it back as expectedVersion.</param>
/// <param name="CacheTtlSeconds">Upper bound for other API instances to pick up a change.</param>
public sealed record SchedulingSettingsView(int Version, bool Bootstrap, DateTimeOffset? UpdatedAt, Guid? UpdatedByUserId, int CacheTtlSeconds,
    IReadOnlyList<SystemSettingView> Settings, IReadOnlyList<string> Rules);

public sealed record SystemSettingRevisionView(Guid Id, string Key, int Version, int OldValue, int NewValue, string Unit, Guid ChangedByUserId,
    DateTimeOffset ChangedAt, string? Reason);

/// <param name="Value">Null when the submitted value was not an integer.</param>
/// <param name="Unit">Optional; when present it must equal the setting's unit (no conversion).</param>
public sealed record SystemSettingChangeInput(string? Key, int? Value, string? Unit);

public sealed record SchedulingSettingsUpdateInput(IReadOnlyList<SystemSettingChangeInput>? Settings, int? ExpectedVersion, string? Reason);

/// <summary>
/// Admin reads and updates of the scheduling policy. An update is all-or-nothing: every change is validated (key, editability,
/// unit, integer range) and the merged values pass the cross-field rules; then, under the global version row lock and revision
/// check, the version is incremented, the changed keys are stamped with it and one audit row per changed key is written. The local
/// policy cache is invalidated after commit. Settings apply to new timing events only; existing deadlines are snapshots.
/// </summary>
public sealed class SystemSettingsService(ISystemSettingsUnitOfWork unitOfWork, SchedulingPolicyDefinitions definitions, ISchedulingPolicyProvider provider,
    SchedulingPolicyCache cache, TimeProvider time, ILogger<SystemSettingsService> logger)
{
    public const int MaxReasonLength = 500;

    public async Task<SchedulingSettingsView> GetSchedulingAsync(CancellationToken cancellationToken)
        => View(await unitOfWork.GetSettingsAsync(false, cancellationToken), await unitOfWork.FindVersionAsync(cancellationToken));

    public async Task<PagedResult<SystemSettingRevisionView>> HistoryAsync(int page, int pageSize, string? key, CancellationToken cancellationToken)
    {
        page = Math.Clamp(page, 1, 10_000); // bounded: (page - 1) * pageSize must not overflow into a negative OFFSET
        pageSize = Math.Clamp(pageSize, 1, 100);
        var (items, total) = await unitOfWork.ListRevisionsAsync(string.IsNullOrWhiteSpace(key) ? null : key.Trim(), (page - 1) * pageSize, pageSize, cancellationToken);
        return PagedResult<SystemSettingRevisionView>.Create(items.Select(r => new SystemSettingRevisionView(r.Id, r.Key, r.Version, r.OldValue, r.NewValue, r.Unit,
            r.ChangedByUserId, r.ChangedAt, r.Reason)).ToList(), page, pageSize, total);
    }

    public async Task<OperationResult<SchedulingSettingsView>> UpdateSchedulingAsync(Guid adminUserId, SchedulingSettingsUpdateInput input, CancellationToken cancellationToken)
    {
        var shape = ValidateShape(input);
        if (shape is not null) return shape;
        var changes = input.Settings!.ToDictionary(s => s.Key!.Trim(), s => s.Value!.Value, StringComparer.Ordinal);
        var reason = string.IsNullOrWhiteSpace(input.Reason) ? null : input.Reason.Trim();

        OperationResult<SchedulingSettingsView> result;
        var committed = false;
        try
        {
            result = await unitOfWork.ExecuteInTransactionAsync<OperationResult<SchedulingSettingsView>>(async token =>
            {
                var versionRow = await unitOfWork.LockVersionAsync(token);
                var current = versionRow?.Version ?? 0;
                if (input.ExpectedVersion != current) return (Conflict(current), false);

                var rows = await unitOfWork.GetSettingsAsync(true, token);
                var before = definitions.Effective(rows);
                var merged = new Dictionary<string, int>(before, StringComparer.Ordinal);
                foreach (var (key, value) in changes) merged[key] = value;
                var ruleErrors = SchedulingPolicyDefinitions.RuleErrors(merged);
                if (ruleErrors.Count > 0)
                    return (OperationResult<SchedulingSettingsView>.Fail(SystemSettingErrorCodes.RuleViolation, "The settings break a cross-field rule.", ruleErrors), false);

                // Only real changes are versioned and audited; a request that changes nothing is a no-op (same version).
                var changed = changes.Keys.Where(k => before[k] != changes[k]).Order(StringComparer.Ordinal).ToList();
                if (changed.Count == 0) return (OperationResult<SchedulingSettingsView>.Ok(View(rows, versionRow)), false);

                var now = time.GetUtcNow();
                var next = current + 1;
                if (versionRow is null)
                {
                    versionRow = new SystemSettingsVersion { Id = SystemSettingsVersion.SingletonId, Version = next, UpdatedAt = now, UpdatedByUserId = adminUserId, Revision = 1 };
                    unitOfWork.Add(versionRow);
                }
                else
                {
                    versionRow.Version = next;
                    versionRow.UpdatedAt = now;
                    versionRow.UpdatedByUserId = adminUserId;
                    versionRow.Revision++;
                }

                var all = rows.ToList();
                foreach (var key in changed)
                {
                    var definition = definitions.Find(key)!;
                    var row = all.FirstOrDefault(r => r.Key == key);
                    if (row is null)
                    {
                        row = new SystemSetting { Key = key, Revision = 0 };
                        unitOfWork.Add(row);
                        all.Add(row);
                    }
                    row.ValueInt = changes[key];
                    row.Unit = definition.Unit;
                    row.Version = next;
                    row.UpdatedAt = now;
                    row.UpdatedByUserId = adminUserId;
                    row.Revision++;
                    unitOfWork.Add(new SystemSettingRevision
                    {
                        Id = Guid.NewGuid(), Key = key, Version = next, OldValue = before[key], NewValue = changes[key], Unit = definition.Unit,
                        ChangedByUserId = adminUserId, ChangedAt = now, Reason = reason,
                    });
                }
                await unitOfWork.SaveChangesAsync(token);
                committed = true;
                return (OperationResult<SchedulingSettingsView>.Ok(View(all, versionRow)), true);
            }, cancellationToken);
        }
        catch (PersistenceConflictException)
        {
            // A concurrent update won: the version row (or a first insert of it) changed under us.
            return Conflict(null);
        }

        if (result.Succeeded && committed)
        {
            provider.Invalidate();
            logger.LogInformation("Scheduling settings version {Version} saved by {AdminId}: {Keys}.", result.Value!.Version, adminUserId,
                string.Join(", ", changes.Keys.Order(StringComparer.Ordinal)));
        }
        return result;
    }

    // ------------------------------------------------------------------ internals

    private OperationResult<SchedulingSettingsView>? ValidateShape(SchedulingSettingsUpdateInput input)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (input.ExpectedVersion is null or < 0) errors["expectedVersion"] = ["Required: the version returned by GET (0 for the bootstrap defaults)."];
        if (string.IsNullOrWhiteSpace(input.Reason)) errors["reason"] = ["Required: why the settings change (kept in the history)."];
        else if (input.Reason.Length > MaxReasonLength) errors["reason"] = [$"At most {MaxReasonLength} characters."];
        if (input.Settings is null || input.Settings.Count == 0) errors["settings"] = ["At least one setting is required."];
        else if (input.Settings.Count > definitions.All.Count) errors["settings"] = [$"At most {definitions.All.Count} settings."];
        else if (input.Settings.Any(s => string.IsNullOrWhiteSpace(s.Key))) errors["settings"] = ["Every setting needs a key."];
        else if (input.Settings.GroupBy(s => s.Key!.Trim(), StringComparer.Ordinal).Any(g => g.Count() > 1)) errors["settings"] = ["A key may appear only once."];
        if (errors.Count > 0) return OperationResult<SchedulingSettingsView>.Fail(SystemSettingErrorCodes.ValidationFailed, "The request is invalid.", errors);

        // Most fundamental problem first: unknown key, then not editable, then unit, then value.
        var unknown = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var locked = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var units = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var values = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var change in input.Settings!)
        {
            var key = change.Key!.Trim();
            var definition = definitions.Find(key);
            if (definition is null)
            {
                unknown[key] = ["Unknown setting key."];
                continue;
            }
            if (!definition.AdminEditable) locked[key] = ["This setting cannot be changed by an administrator."];
            if (change.Unit is not null && !string.Equals(change.Unit.Trim(), definition.Unit, StringComparison.OrdinalIgnoreCase))
                units[key] = [$"Unit must be {definition.Unit} (no conversion is applied)."];
            if (change.Value is not { } value) values[key] = ["Must be an integer."];
            else if (SchedulingPolicyDefinitions.RangeError(definition, value) is { } range) values[key] = [range];
        }
        if (unknown.Count > 0) return OperationResult<SchedulingSettingsView>.Fail(SystemSettingErrorCodes.UnknownKey, "One or more setting keys are unknown.", unknown);
        if (locked.Count > 0) return OperationResult<SchedulingSettingsView>.Fail(SystemSettingErrorCodes.NotEditable, "One or more settings are not editable.", locked);
        if (units.Count > 0) return OperationResult<SchedulingSettingsView>.Fail(SystemSettingErrorCodes.InvalidUnit, "One or more units do not match.", units);
        if (values.Count > 0) return OperationResult<SchedulingSettingsView>.Fail(SystemSettingErrorCodes.InvalidValue, "One or more values are invalid.", values);
        return null;
    }

    private static OperationResult<SchedulingSettingsView> Conflict(int? currentVersion)
        => OperationResult<SchedulingSettingsView>.Fail(SystemSettingErrorCodes.VersionConflict, "The settings changed meanwhile. Reload and try again.",
            currentVersion is null ? null : new { currentVersion });

    private SchedulingSettingsView View(IReadOnlyList<SystemSetting> rows, SystemSettingsVersion? version)
    {
        var settings = definitions.All.Select(d =>
        {
            var row = rows.FirstOrDefault(r => r.Key == d.Key);
            var usable = row is not null && SchedulingPolicyDefinitions.IsUsable(d, row);
            return new SystemSettingView(d.Key, d.Type, d.Unit, usable ? row!.ValueInt : d.Default, d.Default, d.Min, d.Max,
                usable ? SystemSettingSources.Admin : SystemSettingSources.Default, d.AdminEditable, d.Description, usable ? row!.Version : 0,
                usable ? row!.UpdatedAt : null, usable ? row!.UpdatedByUserId : null);
        }).ToList();
        var current = version?.Version ?? 0;
        return new SchedulingSettingsView(current, current == 0, version?.UpdatedAt, version?.UpdatedByUserId, (int)cache.Ttl.TotalSeconds, settings,
            SchedulingPolicyDefinitions.RuleDescriptions);
    }
}
