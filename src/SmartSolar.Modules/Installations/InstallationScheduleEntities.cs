namespace SmartSolar.Modules.Installations;

/// <summary>
/// The installation date the responsible technician agreed with the customer (by phone or on site) after the contract was SIGNED, before or
/// after payment. It is NOT an installation project, an assignment or a capacity hold: before PAID it blocks nobody's calendar (so it can never
/// hold a technician indefinitely), and it is validated against eligibility (role, zone, working hours, leave, existing commitments) when
/// it is recorded. When the contract is PAID the system creates (or reuses) the contract's single installation project, re-checks the
/// technician under the scheduling locks and turns the plan into the project's ACCEPTED assignment (CONFIRMED); if the technician is no
/// longer free or eligible the plan becomes RESCHEDULE_REQUIRED and nothing is confirmed. History is kept: a change cancels the live row
/// (reason recorded) and inserts a new one; at most one live row (PLANNED / CONFIRMED / RESCHEDULE_REQUIRED) per contract (partial unique index).
/// </summary>
public sealed class InstallationScheduleProposal
{
    public Guid Id { get; set; }
    public Guid ContractId { get; set; }
    public Guid SurveyRequestId { get; set; }
    public Guid CustomerUserId { get; set; }
    public Guid PropertySiteId { get; set; }
    public Guid TechnicianUserId { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }

    /// <summary>One of <see cref="InstallationScheduleStatuses"/>.</summary>
    public string Status { get; set; } = null!;

    /// <summary>How and when the customer agreed (e.g. "Khách đồng ý qua điện thoại 17/11 15:00"). Required: the customer is not asked to confirm again in the app.</summary>
    public string CustomerAgreementNote { get; set; } = null!;

    /// <summary>Why this plan replaced the previous one (required for a change).</summary>
    public string? ChangeReason { get; set; }
    public Guid? ReplacesProposalId { get; set; }

    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>The project whose accepted assignment carries the plan (set on CONFIRMED).</summary>
    public Guid? ProjectId { get; set; }
    public DateTimeOffset? ConfirmedAt { get; set; }

    /// <summary>Why the plan could not be confirmed (<see cref="InstallationScheduleRescheduleReasons"/>) and the precise message.</summary>
    public string? RescheduleReasonCode { get; set; }
    public string? RescheduleDetail { get; set; }
    public DateTimeOffset? RescheduleRequiredAt { get; set; }

    /// <summary>The customer asked for another date (the plan keeps its state until the technician records a new one).</summary>
    public DateTimeOffset? CustomerChangeRequestedAt { get; set; }
    public string? CustomerChangeRequestNote { get; set; }

    public DateTimeOffset? CancelledAt { get; set; }
    public Guid? CancelledByUserId { get; set; }
    public string? CancellationReason { get; set; }

    public int Revision { get; set; }
}

public static class InstallationScheduleStatuses
{
    /// <summary>Agreed and recorded by the technician; not confirmed (the contract may still be unpaid). Never shown as confirmed.</summary>
    public const string Planned = "PLANNED";
    /// <summary>The contract is PAID and the plan is the project's ACCEPTED assignment (capacity re-checked under the locks).</summary>
    public const string Confirmed = "CONFIRMED";
    /// <summary>The plan cannot be confirmed (technician unavailable, start passed, …): the technician records a new date.</summary>
    public const string RescheduleRequired = "RESCHEDULE_REQUIRED";
    /// <summary>Cancelled with a reason (a replacement, the technician, a Manager/Admin, or the installation cancelled).</summary>
    public const string Cancelled = "CANCELLED";

    public static readonly IReadOnlyList<string> All = [Planned, Confirmed, RescheduleRequired, Cancelled];
    public static readonly IReadOnlyList<string> Live = [Planned, Confirmed, RescheduleRequired];

    public static bool IsLive(string status) => Live.Contains(status);
}

public static class InstallationScheduleRescheduleReasons
{
    /// <summary>The technician is no longer eligible or free for the interval (leave, another job, zone, working hours…).</summary>
    public const string TechnicianUnavailable = "TECHNICIAN_UNAVAILABLE";
    /// <summary>The interval is no longer acceptable (start passed before payment, horizon, length).</summary>
    public const string InvalidSchedule = "INVALID_SCHEDULE";
    /// <summary>The site's zone or location no longer allows the assignment (or a cross-zone authorization by a Manager is needed).</summary>
    public const string CoverageChanged = "COVERAGE_CHANGED";
    /// <summary>The project is assigned to another technician or can no longer be (re)assigned: a Manager/Admin decides.</summary>
    public const string ProjectNotAssignable = "PROJECT_NOT_ASSIGNABLE";

    public static readonly IReadOnlyList<string> All = [TechnicianUnavailable, InvalidSchedule, CoverageChanged, ProjectNotAssignable];
}

public static class InstallationScheduleErrorCodes
{
    public const string NotFound = "INSTALLATION_SCHEDULE_NOT_FOUND";
    public const string ContractNotFound = "INSTALLATION_SCHEDULE_CONTRACT_NOT_FOUND";
    public const string ContractNotSigned = "INSTALLATION_SCHEDULE_CONTRACT_NOT_SIGNED";
    public const string AgreementRequired = "INSTALLATION_SCHEDULE_AGREEMENT_REQUIRED";
    public const string ReasonRequired = "INSTALLATION_SCHEDULE_REASON_REQUIRED";
    public const string InvalidSchedule = "INSTALLATION_SCHEDULE_INVALID";
    public const string TechnicianIneligible = "INSTALLATION_SCHEDULE_TECHNICIAN_INELIGIBLE";
    public const string InstallationStarted = "INSTALLATION_SCHEDULE_INSTALLATION_STARTED";
    public const string AssignedToOtherTechnician = "INSTALLATION_SCHEDULE_ASSIGNED_TO_OTHER_TECHNICIAN";
    public const string StaleRevision = "INSTALLATION_SCHEDULE_STALE_REVISION";
    public const string NotLive = "INSTALLATION_SCHEDULE_NOT_LIVE";
    public const string CoverageUnavailable = "INSTALLATION_SCHEDULE_COVERAGE_UNAVAILABLE";
}
