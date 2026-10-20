namespace SmartSolar.Modules.PreSurvey.Entities;

/// <summary>Append-only audit of commercial-ownership changes on a survey request (Manager/Admin reassignment of the Sales owner).</summary>
public sealed class SurveyRequestEvent
{
    public Guid Id { get; set; }
    public Guid SurveyRequestId { get; set; }
    public string EventType { get; set; } = null!;
    public Guid? ActorUserId { get; set; }
    public Guid? PreviousSaleUserId { get; set; }
    public Guid? NewSaleUserId { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public static class SurveyRequestEventTypes
{
    public const string SalesOwnerChanged = "SALES_OWNER_CHANGED";
}
