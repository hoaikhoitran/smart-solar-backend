namespace SmartSolar.Modules.Scheduling.Constants;

/// <summary>Appointment kinds handled by the shared scheduling core. Only PRE_SURVEY exists in Flow 1.</summary>
public static class AppointmentTypes
{
    public const string PreSurvey = "PRE_SURVEY";
}

/// <summary>Only non-cancelled appointments consume technician capacity.</summary>
public static class AppointmentStatuses
{
    public const string Booked = "BOOKED";
    public const string Assigned = "ASSIGNED";
    public const string Acknowledged = "ACKNOWLEDGED";
    public const string Cancelled = "CANCELLED";

    public static bool IsActive(string status) => status != Cancelled;
}

public static class TravelOriginKinds
{
    /// <summary>Technician's configured base location (first stop of the day).</summary>
    public const string Base = "BASE";

    /// <summary>The previous appointment's property.</summary>
    public const string Appointment = "APPOINTMENT";
}

public static class SchedulingErrorCodes
{
    public const string ValidationFailed = "SCHEDULING_VALIDATION_FAILED";
    public const string SlotUnavailable = "SURVEY_SLOT_UNAVAILABLE";
    public const string BookingAlreadyExists = "SURVEY_BOOKING_ALREADY_EXISTS";
    public const string BookingNotFound = "SURVEY_BOOKING_NOT_FOUND";
    public const string InvalidBookingTime = "INVALID_BOOKING_TIME";
    public const string InvalidBookingState = "INVALID_BOOKING_STATE";
    public const string TechnicianNotAvailable = "TECHNICIAN_NOT_AVAILABLE";
    public const string TechnicianNotFound = "TECHNICIAN_NOT_FOUND";
    public const string TechnicianNotAssigned = "TECHNICIAN_NOT_ASSIGNED";
    public const string TravelRouteUnavailable = "TRAVEL_ROUTE_UNAVAILABLE";
    public const string TravelProviderUnavailable = "TRAVEL_PROVIDER_UNAVAILABLE";
    public const string TravelProviderQuotaExceeded = "TRAVEL_PROVIDER_QUOTA_EXCEEDED";
    public const string ConcurrencyConflict = "APPOINTMENT_CONCURRENCY_CONFLICT";
    public const string PropertySiteLocationRequired = "PROPERTY_SITE_LOCATION_REQUIRED";
    public const string PropertySiteNotFound = "PROPERTY_SITE_NOT_FOUND";
    public const string SurveyRequestNotFound = "SURVEY_REQUEST_NOT_FOUND";
    public const string AppointmentNotFound = "APPOINTMENT_NOT_FOUND";
    public const string TechnicianHasConflictingAppointments = "TECHNICIAN_HAS_CONFLICTING_APPOINTMENTS";
    public const string TechnicianProfileRequired = "TECHNICIAN_PROFILE_REQUIRED";
}
