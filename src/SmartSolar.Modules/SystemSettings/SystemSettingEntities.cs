namespace SmartSolar.Modules.SystemSettings;

/// <summary>
/// An admin-saved value of a typed setting (table <c>system_setting</c>). A key without a row uses its code/appsettings
/// default (bootstrap, version 0). <see cref="Version"/> is the global settings version that last changed this key, so the
/// highest version over all rows always equals <see cref="SystemSettingsVersion.Version"/>.
/// </summary>
public sealed class SystemSetting
{
    public string Key { get; set; } = null!;
    public int ValueInt { get; set; }
    public string Unit { get; set; } = null!;
    public int Version { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? UpdatedByUserId { get; set; }
    public int Revision { get; set; }
}

/// <summary>Append-only audit of every changed value (table <c>system_setting_revision</c>); one row per key and version.</summary>
public sealed class SystemSettingRevision
{
    public Guid Id { get; set; }
    public string Key { get; set; } = null!;
    public int Version { get; set; }
    /// <summary>The effective value before the change (the default when the key had never been saved).</summary>
    public int OldValue { get; set; }
    public int NewValue { get; set; }
    public string Unit { get; set; } = null!;
    public Guid ChangedByUserId { get; set; }
    public DateTimeOffset ChangedAt { get; set; }
    public string? Reason { get; set; }
}

/// <summary>
/// The single row (id 1) of <c>system_settings_version</c>: the global version every update increments under a row lock and a
/// revision check. Absent until the first admin save (bootstrap = version 0).
/// </summary>
public sealed class SystemSettingsVersion
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;
    public int Version { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? UpdatedByUserId { get; set; }
    public int Revision { get; set; }
}
