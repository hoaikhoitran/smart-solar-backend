namespace SmartSolar.Modules.OnSiteSurveys;

/// <summary>
/// The technician's on-site survey for one survey request (Installation Flow step 1). Kept apart from the
/// customer's PreSurvey, which is never overwritten. One report per survey request; after
/// completion the report is locked and every correction goes through an <see cref="OnSiteSurveyAmendment"/>
/// that records the previous values and increments <see cref="Version"/>.
/// </summary>
public sealed class OnSiteSurveyReport
{
    public Guid Id { get; set; }
    public Guid SurveyRequestId { get; set; }
    public Guid AppointmentId { get; set; }
    public Guid TechnicianUserId { get; set; }
    public string Status { get; set; } = null!;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public string? RoofType { get; set; }
    public string? RoofMaterial { get; set; }
    public decimal? RoofLengthM { get; set; }
    public decimal? RoofWidthM { get; set; }
    public decimal? UsableAreaM2 { get; set; }
    public decimal? TiltDegree { get; set; }
    public decimal? AzimuthDegree { get; set; }
    public string? RoofCondition { get; set; }
    public string? ElectricalCabinetNotes { get; set; }
    public string? ElectricalConnectionNotes { get; set; }
    public string? InstallationFeasibility { get; set; }
    public string? ProposedInverterLocation { get; set; }
    public string? ProposedPanelLayoutNotes { get; set; }
    public decimal? MeasuredLatitude { get; set; }
    public decimal? MeasuredLongitude { get; set; }
    public string? Notes { get; set; }

    /// <summary>1 at completion, +1 per amendment. Quotations record the version they were based on.</summary>
    public int Version { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int Revision { get; set; }
}

public sealed class OnSiteSurveyObstacle
{
    public Guid Id { get; set; }
    public Guid ReportId { get; set; }
    public int SortOrder { get; set; }
    public string Kind { get; set; } = null!;
    public string? Description { get; set; }
    public decimal? HeightM { get; set; }
    public decimal? AreaM2 { get; set; }
}

/// <summary>Links a VERIFIED file to the report. A file can be attached to one report only.</summary>
public sealed class OnSiteSurveyAttachment
{
    public Guid Id { get; set; }
    public Guid ReportId { get; set; }
    public Guid FileId { get; set; }
    public string Category { get; set; } = null!;
    public string? Caption { get; set; }
    public Guid AddedByUserId { get; set; }
    public DateTimeOffset AddedAt { get; set; }
    /// <summary>Set when the attachment was added through an amendment after completion.</summary>
    public Guid? AmendmentId { get; set; }
}

/// <summary>Correction to a completed report: what changed, why and by whom (previous values kept).</summary>
public sealed class OnSiteSurveyAmendment
{
    public Guid Id { get; set; }
    public Guid ReportId { get; set; }
    public int AmendmentNumber { get; set; }
    public Guid AuthorUserId { get; set; }
    public string Reason { get; set; } = null!;
    /// <summary>JSON object: field → { "old": …, "new": … }.</summary>
    public string ChangesJson { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
}

public static class OnSiteSurveyStatuses
{
    public const string Draft = "DRAFT";
    public const string InProgress = "IN_PROGRESS";
    public const string Completed = "COMPLETED";
}

public static class OnSiteSurveyVocabulary
{
    public static readonly IReadOnlyList<string> RoofConditions = ["GOOD", "FAIR", "POOR"];
    public static readonly IReadOnlyList<string> Feasibility = ["FEASIBLE", "FEASIBLE_WITH_CONDITIONS", "NOT_FEASIBLE"];
    public static readonly IReadOnlyList<string> AttachmentCategories = ["ROOF", "ELECTRICAL", "OBSTACLE", "SITE", "DOCUMENT"];
    public static readonly IReadOnlyList<string> ObstacleKinds = ["CHIMNEY", "WATER_TANK", "SKYLIGHT", "ANTENNA", "VENT", "TREE_SHADE", "BUILDING_SHADE", "OTHER"];
}

public static class OnSiteSurveyErrorCodes
{
    public const string AppointmentNotFound = "ONSITE_APPOINTMENT_NOT_FOUND";
    public const string NotConfirmed = "ONSITE_APPOINTMENT_NOT_CONFIRMED";
    public const string TooEarly = "ONSITE_SURVEY_TOO_EARLY";
    public const string ReportNotFound = "ONSITE_SURVEY_NOT_FOUND";
    public const string AlreadyCompleted = "ONSITE_SURVEY_ALREADY_COMPLETED";
    public const string NotStarted = "ONSITE_SURVEY_NOT_STARTED";
    public const string Incomplete = "ONSITE_SURVEY_INCOMPLETE";
    public const string NotCompleted = "ONSITE_SURVEY_NOT_COMPLETED";
    public const string StaleRevision = "ONSITE_SURVEY_STALE_REVISION";
    public const string EvidenceRequired = "ONSITE_SURVEY_EVIDENCE_REQUIRED";
    public const string AttachmentNotFound = "ONSITE_SURVEY_ATTACHMENT_NOT_FOUND";
}
