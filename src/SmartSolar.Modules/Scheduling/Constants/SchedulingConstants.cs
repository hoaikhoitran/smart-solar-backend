namespace SmartSolar.Modules.Scheduling.Constants;

/// <summary>Appointment kinds handled by the shared scheduling core. Only PRE_SURVEY exists in Flow 1.</summary>
public static class AppointmentTypes
{
    public const string PreSurvey = "PRE_SURVEY";
}

/// <summary>
/// Flow 1 self-acceptance lifecycle. Every status except CANCELLED occupies the capacity of
/// <c>reserved_technician_user_id</c> (when set), so the exclusion constraint keeps working.
/// <list type="bullet">
/// <item>PENDING_ACCEPTANCE: booked; the capacity of an internal holder is reserved; nobody accepted yet.</item>
/// <item>ESCALATED: the acceptance deadline passed (or no holder could be kept); staff must act; still acceptable.</item>
/// <item>ACCEPTED: an eligible technician self-accepted.</item>
/// <item>ASSIGNED: Sales or a Manager assigned a technician (with a reason).</item>
/// <item>COMPLETED: the on-site survey was completed.</item>
/// <item>CANCELLED: cancelled or replaced by a reschedule.</item>
/// </list>
/// </summary>
public static class AppointmentStatuses
{
    public const string PendingAcceptance = "PENDING_ACCEPTANCE";
    public const string Escalated = "ESCALATED";
    public const string Accepted = "ACCEPTED";
    public const string Assigned = "ASSIGNED";
    public const string Completed = "COMPLETED";
    public const string Cancelled = "CANCELLED";

    public static readonly IReadOnlyList<string> All = [PendingAcceptance, Escalated, Accepted, Assigned, Completed, Cancelled];

    /// <summary>Not cancelled (the appointment still exists for its survey request).</summary>
    public static bool IsActive(string status) => status != Cancelled;

    /// <summary>Waiting for a technician: acceptable by eligible technicians.</summary>
    public static bool IsOpen(string status) => status is PendingAcceptance or Escalated;

    /// <summary>A technician is confirmed (self-accepted or staff-assigned) and the survey has not happened yet.</summary>
    public static bool IsConfirmed(string status) => status is Accepted or Assigned;

    /// <summary>Open or confirmed: the appointment can still be changed (before its start).</summary>
    public static bool IsChangeable(string status) => IsOpen(status) || IsConfirmed(status);
}

/// <summary>
/// Status label of an installation commitment in technician conflict lists (admin guards, account deactivation): a current
/// (PENDING or ACCEPTED) installation_assignment of the technician. The id reported with it is the assignment id.
/// </summary>
public static class InstallationCommitments
{
    public const string Status = "INSTALLATION_ASSIGNMENT";
}

/// <summary>How the confirmed technician got the appointment.</summary>
public static class AssignmentSources
{
    public const string SelfAccepted = "SELF_ACCEPTED";
    public const string SalesAssigned = "SALES_ASSIGNED";
    public const string ManagerOverride = "MANAGER_OVERRIDE";

    /// <summary>Booked directly as ASSIGNED from an uncovered-zone dispatch request (Manager/Admin assignment or an accepted proposal).</summary>
    public const string ManagerDispatch = "MANAGER_DISPATCH";

    public static readonly IReadOnlyList<string> All = [SelfAccepted, SalesAssigned, ManagerOverride, ManagerDispatch];
}

/// <summary>Customer-facing acceptance state (the internal capacity holder is never exposed).</summary>
public static class AcceptanceStatuses
{
    public const string WaitingForTechnician = "WAITING_FOR_TECHNICIAN";
    public const string Confirmed = "CONFIRMED";
    public const string NeedsStaff = "NEEDS_STAFF";
    public const string Cancelled = "CANCELLED";
    public const string Completed = "COMPLETED";

    /// <summary>
    /// A pending appointment past its deadline counts as escalated even before the worker ran; an open (pending or escalated)
    /// appointment whose start passed counts as cancelled (the worker cancels it with NOT_ACCEPTED_BEFORE_START).
    /// </summary>
    public static string Of(string status, DateTimeOffset startsAt, DateTimeOffset? acceptanceDeadlineAt, DateTimeOffset now) => status switch
    {
        AppointmentStatuses.Accepted or AppointmentStatuses.Assigned => Confirmed,
        AppointmentStatuses.Completed => Completed,
        AppointmentStatuses.Cancelled => Cancelled,
        _ when AppointmentStatuses.IsOpen(status) && startsAt <= now => Cancelled,
        AppointmentStatuses.Escalated => NeedsStaff,
        _ => acceptanceDeadlineAt is { } deadline && deadline <= now ? NeedsStaff : WaitingForTechnician,
    };
}

/// <summary>Cancellation reasons written by the system (customer reasons are free text).</summary>
public static class CancellationReasons
{
    public const string Rescheduled = "RESCHEDULED";
    public const string RescheduledBySales = "RESCHEDULED_BY_SALES";
    public const string CustomerCancelled = "CUSTOMER_CANCELLED";

    /// <summary>Still open (PENDING_ACCEPTANCE/ESCALATED) when its start passed: nobody accepted it in time (escalation worker, or a new booking).</summary>
    public const string NotAcceptedBeforeStart = "NOT_ACCEPTED_BEFORE_START";

    /// <summary>The technician released a dispatch-assigned appointment; the work went back to the dispatch queue (a new request).</summary>
    public const string ReleasedToDispatch = "RELEASED_TO_DISPATCH";
}

public static class SchedulingErrorCodes
{
    public const string ValidationFailed = "SCHEDULING_VALIDATION_FAILED";
    public const string SlotUnavailable = "SURVEY_SLOT_UNAVAILABLE";
    public const string BookingAlreadyExists = "SURVEY_BOOKING_ALREADY_EXISTS";
    public const string BookingNotFound = "SURVEY_BOOKING_NOT_FOUND";
    public const string InvalidBookingTime = "INVALID_BOOKING_TIME";
    public const string InvalidBookingState = "INVALID_BOOKING_STATE";
    public const string TechnicianNotFound = "TECHNICIAN_NOT_FOUND";
    public const string ConcurrencyConflict = "APPOINTMENT_CONCURRENCY_CONFLICT";
    public const string PropertySiteNotFound = "PROPERTY_SITE_NOT_FOUND";
    public const string SurveyRequestNotFound = "SURVEY_REQUEST_NOT_FOUND";
    public const string AppointmentNotFound = "APPOINTMENT_NOT_FOUND";
    public const string TechnicianHasConflictingAppointments = "TECHNICIAN_HAS_CONFLICTING_APPOINTMENTS";
    public const string TechnicianProfileRequired = "TECHNICIAN_PROFILE_REQUIRED";

    /// <summary>Another technician (or staff) took the appointment first.</summary>
    public const string AppointmentAlreadyTaken = "APPOINTMENT_ALREADY_TAKEN";

    /// <summary>The Sales caller is not the request's commercial owner (claim it first).</summary>
    public const string SurveyRequestNotOwned = "SURVEY_REQUEST_NOT_OWNED";

    public const string AssignmentReasonRequired = "ASSIGNMENT_REASON_REQUIRED";

    /// <summary>The confirmed technician already started the on-site survey: staff cannot hand the appointment to someone else.</summary>
    public const string OnSiteSurveyInProgress = "ONSITE_SURVEY_IN_PROGRESS";

    // Geographic coverage (site location → commune → operational zone)
    /// <summary>The site has no valid commune code (official, active, inside the site's province). Replaces SITE_AREA_REQUIRED.</summary>
    public const string SiteLocationRequired = Zones.CoverageStatuses.SiteLocationRequired;

    /// <summary>The site's commune is not mapped to an active operational zone. Replaces AREA_NOT_COVERED.</summary>
    public const string NoZoneCoverage = Zones.CoverageStatuses.NoZoneCoverage;

    /// <summary>The site's zone is active but marked not serviceable.</summary>
    public const string ZoneNotServiceable = Zones.CoverageStatuses.ZoneNotServiceable;
    public const string NoZoneTechnician = Zones.CoverageStatuses.NoZoneTechnician;
    public const string NoAvailableTechnician = Zones.CoverageStatuses.NoAvailableTechnician;
    public const string ScheduleConflict = Zones.CoverageStatuses.ScheduleConflict;
    public const string SiteHasActiveBooking = "SITE_HAS_ACTIVE_BOOKING";

    /// <summary>The survey request has an OPEN/PROPOSAL_PENDING dispatch request: accept its proposal or cancel it before booking.</summary>
    public const string DispatchRequestOpen = "DISPATCH_REQUEST_OPEN";

    /// <summary>The site has an installation project that is not COMPLETED/CANCELLED (Installation Flow): its location is frozen.</summary>
    public const string SiteHasActiveInstallation = "SITE_HAS_ACTIVE_INSTALLATION";

    /// <summary>The province or commune code is not an official one, or the commune is not inside the province (422).</summary>
    public const string LocationUnknown = AdministrativeUnits.AdministrativeUnitErrorCodes.LocationUnknown;

    /// <summary>The code was closed by a newer catalog dataset: refused for new values (422).</summary>
    public const string LocationSuperseded = AdministrativeUnits.AdministrativeUnitErrorCodes.LocationSuperseded;

    /// <summary>422: the province is outside the service area (only Ho Chi Minh City, 79, is served).</summary>
    public const string LocationOutOfServiceArea = AdministrativeUnits.AdministrativeUnitErrorCodes.LocationOutOfServiceArea;
    public const string ZoneNotFound = "ZONE_NOT_FOUND";

    /// <summary>403: only an active Admin may create or change an operational zone or its commune mapping.</summary>
    public const string ZoneManagementForbidden = "ZONE_MANAGEMENT_FORBIDDEN";

    /// <summary>403: a Manager may add/remove technician memberships only in zones they are the responsible manager of.</summary>
    public const string ZoneScopeForbidden = "ZONE_SCOPE_FORBIDDEN";
    public const string ZoneCodeExists = "ZONE_CODE_EXISTS";
    public const string ZoneStale = "ZONE_STALE_REVISION";

    /// <summary>Deactivating (or marking not serviceable) a zone that unfinished work records. Same rule as the former SERVICE_AREA_IN_USE.</summary>
    public const string ZoneInUse = "ZONE_IN_USE";

    /// <summary>A commune of the requested set is actively mapped to another ACTIVE zone (a commune belongs to one zone at a time).</summary>
    public const string CommuneAlreadyMapped = "COMMUNE_ALREADY_MAPPED";

    /// <summary>Removing or moving a commune whose sites have unfinished survey appointments or installation projects.</summary>
    public const string CommuneInUse = "COMMUNE_IN_USE";
    public const string ResponsibleManagerInvalid = "RESPONSIBLE_MANAGER_INVALID";
    public const string SalesUserInvalid = "SALES_USER_INVALID";

    // Technician eligibility (also returned as reason codes on availability views)
    public const string TechnicianInactive = Domain.EligibilityReasons.TechnicianInactive;
    public const string TechnicianSchedulingDisabled = Domain.EligibilityReasons.SchedulingDisabled;
    public const string TechnicianOutsideZone = Domain.EligibilityReasons.OutsideZone;
    public const string OutsideWorkingHours = Domain.EligibilityReasons.OutsideWorkingHours;
    public const string TechnicianOnLeave = Domain.EligibilityReasons.OnLeave;
    public const string TechnicianScheduleConflict = Domain.EligibilityReasons.ScheduleConflict;
    public const string DailyLimitReached = Domain.EligibilityReasons.DailyLimitReached;

    public static bool IsEligibilityCode(string code) => Domain.EligibilityReasons.All.Contains(code);
}
