namespace SmartSolar.Modules.Scheduling.Zones;

/// <summary>
/// An internal operational zone: a set of official communes served by the same technicians. Zones are data managed by
/// Admin/Manager (nothing in code lists them). <see cref="IsActive"/> = false retires the zone; <see cref="IsServiceable"/> = false
/// keeps it (and its communes) but stops new work there (coverage status ZONE_NOT_SERVICEABLE).
/// </summary>
public sealed class OperationalZone
{
    public Guid Id { get; set; }

    /// <summary>Internal code, upper-case ASCII ("KHU-THU-DUC"); immutable once created.</summary>
    public string Code { get; set; } = null!;

    public string Name { get; set; } = null!;
    public bool IsActive { get; set; }
    public bool IsServiceable { get; set; } = true;
    public Guid? ResponsibleManagerUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Who created the zone and who last changed its name, flags or responsible manager (audit; nullable for rows written outside the API).</summary>
    public Guid? CreatedByUserId { get; set; }
    public Guid? UpdatedByUserId { get; set; }

    /// <summary>Optimistic concurrency token; every update (including a commune-set change) increments it.</summary>
    public int Revision { get; set; }
}

/// <summary>
/// A commune assigned to a zone. A commune belongs to at most ONE zone at a time (partial unique index on commune_code where
/// removed_at is null); removal is soft so the history of which zone served a commune is kept.
/// </summary>
public sealed class OperationalZoneCommune
{
    public Guid Id { get; set; }
    public Guid ZoneId { get; set; }
    public string CommuneCode { get; set; } = null!;
    public Guid AssignedByUserId { get; set; }
    public DateTimeOffset AssignedAt { get; set; }
    public DateTimeOffset? RemovedAt { get; set; }
    public Guid? RemovedByUserId { get; set; }
    public string? RemovalReason { get; set; }
}

/// <summary>
/// A technician's membership in a zone (a technician may belong to several zones). Removal is soft (<see cref="RemovedAt"/>) so
/// the history of who served where is kept; one active membership per technician and zone.
/// </summary>
public sealed class TechnicianZoneMembership
{
    public Guid Id { get; set; }
    public Guid TechnicianUserId { get; set; }
    public Guid ZoneId { get; set; }
    public Guid? AssignedByUserId { get; set; }
    public DateTimeOffset AssignedAt { get; set; }
    public DateTimeOffset? RemovedAt { get; set; }
    public Guid? RemovedByUserId { get; set; }
}

/// <summary>
/// Stable coverage status codes of a site (see <see cref="ZoneCoverageService"/>). The first three are decided by the site
/// location and the zone data alone; the last three need the zone's technicians (and the booking horizon / requested interval).
/// </summary>
public static class CoverageStatuses
{
    public const string Covered = "COVERED";

    /// <summary>The site has no valid (active, official) commune code. Replaces SITE_AREA_REQUIRED.</summary>
    public const string SiteLocationRequired = "SITE_LOCATION_REQUIRED";

    /// <summary>The site's commune is not mapped to an ACTIVE zone. Replaces AREA_NOT_COVERED.</summary>
    public const string NoZoneCoverage = "NO_ZONE_COVERAGE";

    /// <summary>The zone exists and is active but is_serviceable = false.</summary>
    public const string ZoneNotServiceable = "ZONE_NOT_SERVICEABLE";

    /// <summary>The zone has no active membership of a schedulable technician (active account, TECHNICIAN role, scheduling on).</summary>
    public const string NoZoneTechnician = "NO_ZONE_TECHNICIAN";

    /// <summary>Schedulable members exist but none can take any bookable slot in the booking horizon.</summary>
    public const string NoAvailableTechnician = "NO_AVAILABLE_TECHNICIAN";

    /// <summary>Some member can work in the horizon, but none at the requested time.</summary>
    public const string ScheduleConflict = "SCHEDULE_CONFLICT";

    public static readonly IReadOnlyList<string> All =
        [Covered, SiteLocationRequired, NoZoneCoverage, ZoneNotServiceable, NoZoneTechnician, NoAvailableTechnician, ScheduleConflict];

    /// <summary>
    /// The coverage problems an uncovered-zone dispatch request can solve (Manager/Admin pick a technician by hand). SmartSolar accepts
    /// requests throughout Ho Chi Minh City: a valid in-scope site whose area is TEMPORARILY uncovered — no active zone
    /// (NO_ZONE_COVERAGE), a zone marked not serviceable (ZONE_NOT_SERVICEABLE), no technician (NO_ZONE_TECHNICIAN), no capacity
    /// (NO_AVAILABLE_TECHNICIAN) or a time conflict (SCHEDULE_CONFLICT) — gets an actionable dispatch request, never a refusal and never
    /// an automatic confirmation. Only an invalid / out-of-scope location (SITE_LOCATION_REQUIRED) is not dispatchable; COVERED needs none.
    /// </summary>
    public static readonly IReadOnlyList<string> Dispatchable = [NoZoneCoverage, ZoneNotServiceable, NoZoneTechnician, NoAvailableTechnician, ScheduleConflict];

    public static bool IsDispatchable(string status) => Dispatchable.Contains(status);
}
