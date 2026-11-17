using SmartSolar.Modules.Identity.Entities;
using SmartSolar.Modules.PreSurvey.Enums;

namespace SmartSolar.Modules.PreSurvey.Entities;

public sealed class SurveyRequest
{
    public Guid Id { get; set; }

    /// <summary>The submitted pre-survey (Option A, <see cref="SurveyRequestSources.PreSurvey"/>); null for a direct booking (Option B).</summary>
    public Guid? PreSurveyId { get; set; }

    /// <summary>The surveyed site. For a pre-survey request it is the pre-survey's property (copied at submission).</summary>
    public Guid PropertySiteId { get; set; }

    /// <summary>How the request started: one of <see cref="SurveyRequestSources"/>.</summary>
    public string Source { get; set; } = SurveyRequestSources.PreSurvey;

    /// <summary>On-site contact for the visit when it differs from the account holder (direct booking); null = the account's name / phone.</summary>
    public string? ContactName { get; set; }

    public string? ContactPhone { get; set; }

    public Guid? AssignedSaleId { get; set; }

    public SurveyRequestStatus Status { get; set; }

    public DateTimeOffset SubmittedAt { get; set; }

    public DateTimeOffset? AssignedAt { get; set; }

    public DateTimeOffset? ScheduledAt { get; set; }

    public string? SalesNote { get; set; }

    // ---- terminal outcome (Cancelled by the customer or Closed by Sales); null while the request is open

    public DateTimeOffset? ClosedAt { get; set; }

    public Guid? ClosedByUserId { get; set; }

    /// <summary>One of <see cref="SurveyRequestClosureReasons"/>.</summary>
    public string? ClosureReasonCode { get; set; }

    public string? ClosureNote { get; set; }

    public PreSurvey? PreSurvey { get; set; }

    public PropertySite PropertySite { get; set; } = null!;

    public UserAccount? AssignedSale { get; set; }
}

/// <summary>How a survey request started. Both converge on the same scheduling, on-site survey and quotation workflow.</summary>
public static class SurveyRequestSources
{
    /// <summary>Option A: the customer's submitted pre-survey (surface data, optional simulation).</summary>
    public const string PreSurvey = "PRE_SURVEY";

    /// <summary>Option B: booked directly for a property site, without a pre-survey, surface assessment or simulation.</summary>
    public const string DirectBooking = "DIRECT_BOOKING";

    public static readonly IReadOnlyList<string> All = [PreSurvey, DirectBooking];
}

/// <summary>Why a survey request ended without an installation.</summary>
public static class SurveyRequestClosureReasons
{
    /// <summary>The customer withdrew the request (status Cancelled).</summary>
    public const string CustomerWithdrew = "CUSTOMER_WITHDREW";
    /// <summary>The on-site survey concluded the installation is NOT_FEASIBLE.</summary>
    public const string NotFeasible = "NOT_FEASIBLE";
    /// <summary>The customer declined the offer.</summary>
    public const string CustomerDeclined = "CUSTOMER_DECLINED";
    /// <summary>The customer stopped responding.</summary>
    public const string NoResponse = "NO_RESPONSE";
    /// <summary>Any other reason; a note is required.</summary>
    public const string Other = "OTHER";

    /// <summary>Reasons a Sales owner may close a request with.</summary>
    public static readonly IReadOnlyList<string> SalesClosure = [NotFeasible, CustomerDeclined, NoResponse, Other];

    public static readonly IReadOnlyList<string> All = [CustomerWithdrew, NotFeasible, CustomerDeclined, NoResponse, Other];
}
