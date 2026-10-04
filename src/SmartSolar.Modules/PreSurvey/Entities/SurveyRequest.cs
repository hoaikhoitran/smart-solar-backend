using SmartSolar.Modules.Identity.Entities;
using SmartSolar.Modules.PreSurvey.Enums;

namespace SmartSolar.Modules.PreSurvey.Entities;

public sealed class SurveyRequest
{
    public Guid Id { get; set; }

    public Guid PreSurveyId { get; set; }

    public Guid? AssignedSaleId { get; set; }

    public SurveyRequestStatus Status { get; set; }

    public DateTimeOffset SubmittedAt { get; set; }

    public DateTimeOffset? AssignedAt { get; set; }

    public DateTimeOffset? ScheduledAt { get; set; }

    public string? SalesNote { get; set; }

    public PreSurvey PreSurvey { get; set; } = null!;

    public UserAccount? AssignedSale { get; set; }
}