namespace SmartSolar.Modules.PreSurvey.Enums;

/// <summary>
/// Pending = no confirmed technician yet (the customer may or may not have booked);
/// Scheduled = the active appointment is ACCEPTED or ASSIGNED; Completed = on-site survey done.
/// Commercial ownership (assigned_sale_id) is independent of the status. The former values
/// Assigned (2) and Reviewing (3) are no longer used; stored rows are backfilled to Pending.
/// Numeric values are kept stable because the API exposes them.
/// </summary>
public enum SurveyRequestStatus
{
    Pending = 1,
    Scheduled = 4,
    Completed = 5,
    Cancelled = 6
}
