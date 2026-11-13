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
    public const string QuotationIssued = "INSTALLATION_QUOTATION_ISSUED";
    public const string ContractAwaitingSignature = "CONTRACT_AWAITING_SIGNATURE";
    public const string ContractSigned = "CONTRACT_SIGNED";
    public const string ContractSignatureRejected = "CONTRACT_SIGNATURE_REJECTED";
    public const string InstallationScheduled = "INSTALLATION_SCHEDULED";
    public const string InstallationStarted = "INSTALLATION_STARTED";
    public const string InstallationAcceptanceRequired = "INSTALLATION_ACCEPTANCE_REQUIRED";
    public const string InstallationReworkRequired = "INSTALLATION_REWORK_REQUIRED";
    public const string InstallationCancelled = "INSTALLATION_CANCELLED";
    public const string EquipmentSubstitutionApprovalRequired = "EQUIPMENT_SUBSTITUTION_APPROVAL_REQUIRED";
}
