namespace SmartSolar.Modules.Common.Notifications;

/// <summary>
/// Writes an in-app notification in the caller's transaction. Every module writes to the one table the customer notification
/// endpoint reads (<c>after_sales_notification</c>, which predates the pre-installation events), so a notification exists only
/// together with the business change that caused it: a refused transition never notifies and a transition never notifies twice.
/// </summary>
public interface IInAppNotificationWriter
{
    void AddNotification(Guid recipientUserId, string type, string message, DateTimeOffset now);
}

/// <summary>Customer notification types of the pre-installation flows (the after-sales types live with their handlers). At most 50 characters.</summary>
public static class CustomerNotificationTypes
{
    public const string SurveyAppointmentBooked = "SURVEY_APPOINTMENT_BOOKED";
    /// <summary>A technician is confirmed for the appointment (self-acceptance, staff assignment, or a dispatch booking).</summary>
    public const string SurveyAppointmentConfirmed = "SURVEY_APPOINTMENT_CONFIRMED";
    public const string SurveyAppointmentRescheduled = "SURVEY_APPOINTMENT_RESCHEDULED";
    /// <summary>The confirmed technician released the appointment; it keeps its time and waits for another technician (or staff).</summary>
    public const string SurveyTechnicianReleased = "SURVEY_TECHNICIAN_RELEASED";
    public const string SurveyAppointmentCancelled = "SURVEY_APPOINTMENT_CANCELLED";
    /// <summary>The technician-agreed installation quotation was paid and its contract created (information only).</summary>
    public const string ContractCreatedAfterPayment = "CONTRACT_CREATED_AFTER_PAYMENT";
    public const string ContractAwaitingSignature = "CONTRACT_AWAITING_SIGNATURE";
    public const string ContractSigned = "CONTRACT_SIGNED";
    public const string ContractSignatureRejected = "CONTRACT_SIGNATURE_REJECTED";
    public const string InstallationScheduled = "INSTALLATION_SCHEDULED";
    /// <summary>The technician recorded the installation date agreed with the customer (PLANNED; confirmed once the contract is paid and signed).</summary>
    public const string InstallationSchedulePlanned = "INSTALLATION_SCHEDULE_PLANNED";
    public const string InstallationScheduleChanged = "INSTALLATION_SCHEDULE_CHANGED";
    public const string InstallationScheduleRescheduleRequired = "INSTALLATION_SCHEDULE_RESCHEDULE_REQUIRED";
    public const string InstallationScheduleCancelled = "INSTALLATION_SCHEDULE_CANCELLED";
    public const string InstallationStarted = "INSTALLATION_STARTED";
    /// <summary>The technician reported completion; SmartSolar accepts it internally (information only, no customer action).</summary>
    public const string InstallationCompletionReported = "INSTALLATION_COMPLETION_REPORTED";
    /// <summary>A Manager/Admin accepted the installation internally: handover done (information only).</summary>
    public const string InstallationCompleted = "INSTALLATION_COMPLETED";
    public const string InstallationReworkRequired = "INSTALLATION_REWORK_REQUIRED";
    public const string InstallationCancelled = "INSTALLATION_CANCELLED";
    /// <summary>A Manager approved installed equipment that differs from the contract (information only, with the reason).</summary>
    public const string EquipmentSubstitutionRecorded = "EQUIPMENT_SUBSTITUTION_RECORDED";
}
