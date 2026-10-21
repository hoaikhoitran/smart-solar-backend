using System.Text.Json;
using SmartSolar.Modules.Installations;

namespace SmartSolar.Modules.AfterSales;

public sealed record ServiceItemView(Guid ItemId, Guid? AssetId, string Description, string Diagnosis, string? RequiredParts, string Coverage, string? CoverageReason,
    string Status, bool IsSupplementary, Guid? QuotationId, string? WorkResult, string? PartsUsed);

public sealed record ServiceVisitView(Guid VisitId, string Purpose, string Status, Guid? TechnicianUserId, string? TechnicianName, DateTimeOffset StartsAt,
    DateTimeOffset EndsAt, string? AssignmentStatus, bool CustomerConfirmationRequired, DateTimeOffset? CustomerConfirmedAt, DateTimeOffset? StartedAt,
    string? Report, DateTimeOffset? ReportedAt, string? FindingNote, string? CompanyRescheduleReason);

public sealed record ServiceQuotationView(Guid QuotationId, int Version, bool IsSupplementary, string Status, JsonElement Lines, decimal PartsAmount, decimal LaborAmount,
    decimal TotalAmount, decimal DisasterSupportAmount, decimal CustomerAmount, int? RepairWarrantyMonths, string? Note, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt,
    DateTimeOffset? AcceptedAt, DateTimeOffset? PaidAt, Guid? CurrentPaymentAttemptId);

public sealed record DisasterClaimView(Guid ClaimId, string DisasterType, DateTimeOffset IncidentOccurredAt, string Status, string? DecisionReason,
    DateTimeOffset? ReconsiderationRequestedAt, bool CanRequestReconsideration, Guid? SubscriptionId, decimal CoveragePercent, decimal PerIncidentLimit,
    decimal SupportReserved, decimal SupportConsumed);

public sealed record ServiceEvidenceView(Guid EvidenceId, Guid? VisitId, Guid FileId, string? Caption, DateTimeOffset AddedAt);

public sealed record AfterSalesEventView(string EventType, Guid? ActorUserId, string? Detail, DateTimeOffset CreatedAt);

/// <summary>Another open request of the same installation (a non-blocking duplicate warning).</summary>
public sealed record PossibleDuplicateView(Guid RequestId, string RequestNumber, string Kind, string Status, string Description, DateTimeOffset CreatedAt);

public sealed record ServiceRequestView(
    Guid RequestId, string RequestNumber, string Kind, string Status, Guid ProjectId, string Description, Guid? SuspectedAssetId, DateTimeOffset? PreferredStartsAt,
    DateTimeOffset? InstallationWarrantyEndsAt, bool WithinInstallationWarranty, bool WithinRepairWarranty, Guid? WarrantySourceRequestId, Guid? OriginRequestId,
    Guid? DuplicateOfRequestId, Guid? MaintenanceEntitlementId, string? Outcome, string? CustomerDecisionReason, DateTimeOffset? AcceptanceRequestedAt,
    DateTimeOffset? CompletedAt, int? RepairWarrantyMonths, DateTimeOffset? RepairWarrantyStartsAt, DateTimeOffset? RepairWarrantyEndsAt,
    DateTimeOffset? CancellationRequestedAt, string? CancellationReason, DateTimeOffset? CancelledAt, DateTimeOffset CreatedAt, int Revision,
    IReadOnlyList<ServiceItemView> Items, IReadOnlyList<ServiceVisitView> Visits, IReadOnlyList<ServiceQuotationView> Quotations, DisasterClaimView? Claim,
    IReadOnlyList<ServiceEvidenceView> Evidence, IReadOnlyList<PossibleDuplicateView> PossibleDuplicates, IReadOnlyList<AfterSalesEventView>? History);

public sealed record ServiceRequestSummary(Guid RequestId, string RequestNumber, string Kind, string Status, Guid ProjectId, string Description,
    DateTimeOffset? PreferredStartsAt, DateTimeOffset CreatedAt);

public sealed record AssetView(Guid AssetId, string AssetType, string Manufacturer, string Model, string SerialNumber, string? PositionCode, string Status,
    Guid? ReplacesAssetId, Guid? ServiceRequestId, string? Specifications);

/// <summary>Customer view of one installation's after-sales coverage. <see cref="WarrantyStatus"/> is one of <see cref="InstallationWarrantyStatuses"/>.</summary>
public sealed record WarrantyView(Guid ProjectId, DateTimeOffset? HandoverAt, int? WarrantyMonths, DateTimeOffset? WarrantyEndsAt, bool UnderWarranty,
    IReadOnlyList<AssetView> Assets, IReadOnlyList<SubscriptionView> Subscriptions, string WarrantyStatus);

/// <summary>
/// Installation warranty state. NOT_RECORDED: the contract was signed before warranty durations existed and states no months; the
/// system never invents a duration (a Manager decides such cases from the signed document).
/// </summary>
public static class InstallationWarrantyStatuses
{
    public const string NotHandedOver = "NOT_HANDED_OVER";
    public const string NotRecorded = "NOT_RECORDED";
    public const string Active = "ACTIVE";
    public const string Expired = "EXPIRED";
}

public sealed record EntitlementView(Guid EntitlementId, int PlanMonth, DateTimeOffset PeriodStartsAt, DateTimeOffset PeriodEndsAt, string Status,
    Guid? CurrentRequestId, DateTimeOffset? ExtendedUntil, DateTimeOffset? ConsumedAt);

public sealed record SubscriptionView(Guid SubscriptionId, Guid ContractId, Guid? ProjectId, string Kind, string Status, string PackageName, decimal Price,
    int DurationMonths, IReadOnlyList<int> VisitMonths, IReadOnlyList<string> CoveredDisasterTypes, decimal? CoveragePercent, decimal? PerIncidentLimit,
    decimal? TermLimit, DateTimeOffset? StartsAt, DateTimeOffset? EndsAt, DateTimeOffset? CoverageStartsAt, decimal SupportReserved, decimal SupportConsumed,
    decimal? SupportAvailable, int? RemainingVisits, DateTimeOffset? CheckoutExpiresAt, Guid? CurrentPaymentAttemptId, DateTimeOffset? PaidAt,
    DateTimeOffset? CancellationRequestedAt, IReadOnlyList<EntitlementView> Entitlements);

public sealed record PackageView(Guid PackageId, string Kind, string Name, string? Description, decimal Price, int DurationMonths, IReadOnlyList<int> VisitMonths,
    IReadOnlyList<string> CoveredDisasterTypes, decimal? CoveragePercent, decimal? PerIncidentLimit, decimal? TermLimit, bool IsActive, int Revision);

public sealed record WarrantyOptionView(Guid OptionId, string Kind, int Months, bool IsDefault, bool IsActive);

public sealed record SalesTaskView(Guid TaskId, string TaskType, string Status, Guid? RequestId, Guid? EntitlementId, Guid CustomerUserId, Guid? AssignedSalesUserId,
    DateTimeOffset? ClaimedAt, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, string? Outcome, string? Note, DateTimeOffset CreatedAt, int Revision);

public sealed record RefundView(Guid RefundId, Guid CustomerUserId, Guid? PaymentAttemptId, Guid? SubscriptionId, Guid? QuotationId, Guid? RequestId,
    decimal OriginalAmount, decimal? ApprovedAmount, string Reason, string Status, Guid? ApprovedByUserId, DateTimeOffset? ApprovedAt, string? DecisionNote,
    Guid? RecordedByUserId, DateTimeOffset? RefundedAt, string? RefundReference, DateTimeOffset CreatedAt, int Revision);

public sealed record NotificationView(Guid NotificationId, string Type, string Message, Guid? RequestId, Guid? SubscriptionId, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt);

/// <summary>Maps entities to views; every timestamp is shown in the business time zone.</summary>
public sealed class AfterSalesViews(IAfterSalesUnitOfWork data, Scheduling.Domain.WorkingCalendar calendar)
{
    private DateTimeOffset L(DateTimeOffset value) => TimeZoneInfo.ConvertTime(value, calendar.TimeZone);
    private DateTimeOffset? L(DateTimeOffset? value) => value is { } v ? L(v) : null;

    /// <summary>Full request view. <paramref name="forStaff"/> adds the audit history; customers never see internal events.</summary>
    public async Task<ServiceRequestView> RequestAsync(ServiceRequest r, bool forStaff, CancellationToken token)
    {
        var items = await data.ListItemsAsync(r.Id, false, token);
        var visits = await data.ListVisitsAsync(r.Id, false, token);
        var quotations = await data.ListQuotationsAsync(r.Id, false, token);
        var claim = await data.FindClaimForRequestAsync(r.Id, false, token);
        var evidence = await data.ListEvidenceAsync(r.Id, token);
        var names = await data.GetUserNamesAsync(visits.Where(v => v.TechnicianUserId is not null).Select(v => v.TechnicianUserId!.Value).ToList(), token);
        var visitViews = new List<ServiceVisitView>();
        foreach (var v in visits)
        {
            var assignment = await data.FindCurrentAssignmentAsync(v.Id, false, token);
            visitViews.Add(new ServiceVisitView(v.Id, v.Purpose, v.Status, v.TechnicianUserId, v.TechnicianUserId is { } t ? names.GetValueOrDefault(t) : null,
                L(v.StartsAt), L(v.EndsAt), assignment?.Status, v.CustomerConfirmationRequired, L(v.CustomerConfirmedAt), L(v.StartedAt), v.Report, L(v.ReportedAt),
                v.FindingNote, v.CompanyRescheduleReason));
        }
        var duplicates = ServiceRequestStatuses.IsFinal(r.Status)
            ? []
            : (await data.ListRequestsAsync(new RequestFilter(ProjectId: r.ProjectId), token))
                .Where(o => o.Id != r.Id && !ServiceRequestStatuses.IsFinal(o.Status) && o.Kind == ServiceRequestKinds.Fault && r.Kind == ServiceRequestKinds.Fault)
                .Select(o => new PossibleDuplicateView(o.Id, o.RequestNumber, o.Kind, o.Status, o.Description, L(o.CreatedAt))).ToList();
        var history = forStaff
            ? (await data.ListEventsAsync(EventSubjects.Request, r.Id, token)).Select(e => new AfterSalesEventView(e.EventType, e.ActorUserId, e.Detail, L(e.CreatedAt))).ToList()
            : null;
        return new ServiceRequestView(r.Id, r.RequestNumber, r.Kind, r.Status, r.ProjectId, r.Description, r.SuspectedAssetId, L(r.PreferredStartsAt),
            L(r.InstallationWarrantyEndsAt), r.WithinInstallationWarranty, r.WithinRepairWarranty, r.WarrantySourceRequestId, r.OriginRequestId, r.DuplicateOfRequestId,
            r.MaintenanceEntitlementId, r.Outcome, r.CustomerDecisionReason, L(r.AcceptanceRequestedAt), L(r.CompletedAt), r.RepairWarrantyMonths,
            L(r.RepairWarrantyStartsAt), L(r.RepairWarrantyEndsAt), L(r.CancellationRequestedAt), r.CancellationReason, L(r.CancelledAt), L(r.CreatedAt), r.Revision,
            items.Select(Item).ToList(), visitViews, quotations.Select(Quotation).ToList(), claim is null ? null : Claim(claim),
            evidence.Select(e => new ServiceEvidenceView(e.Id, e.VisitId, e.FileId, e.Caption, L(e.AddedAt))).ToList(), duplicates, history);
    }

    public ServiceRequestSummary Summary(ServiceRequest r)
        => new(r.Id, r.RequestNumber, r.Kind, r.Status, r.ProjectId, r.Description, L(r.PreferredStartsAt), L(r.CreatedAt));

    public static ServiceItemView Item(ServiceItem i)
        => new(i.Id, i.AssetId, i.Description, i.Diagnosis, i.RequiredParts, i.Coverage, i.CoverageReason, i.Status, i.IsSupplementary, i.QuotationId, i.WorkResult, i.PartsUsed);

    public ServiceQuotationView Quotation(ServiceQuotation q)
        => new(q.Id, q.Version, q.IsSupplementary, q.Status, JsonDocument.Parse(q.LinesJson).RootElement.Clone(), q.PartsAmount, q.LaborAmount, q.TotalAmount,
            q.DisasterSupportAmount, q.CustomerAmount, q.RepairWarrantyMonths, q.Note, L(q.IssuedAt), L(q.ExpiresAt), L(q.AcceptedAt), L(q.PaidAt), q.CurrentPaymentAttemptId);

    public DisasterClaimView Claim(DisasterClaim c)
        => new(c.Id, c.DisasterType, L(c.IncidentOccurredAt), c.Status, c.DecisionReason, L(c.ReconsiderationRequestedAt),
            c.Status == ClaimStatuses.Rejected && c.ReconsiderationRequestedAt is null, c.SubscriptionId, c.CoveragePercent, c.PerIncidentLimit, c.SupportReserved,
            c.SupportConsumed);

    public static AssetView Asset(InstalledAsset a)
        => new(a.Id, a.AssetType, a.Manufacturer, a.Model, a.SerialNumber, a.PositionCode, a.Status, a.ReplacesAssetId, a.ServiceRequestId, a.Specifications);

    public async Task<SubscriptionView> SubscriptionAsync(ServiceSubscription s, CancellationToken token)
    {
        var entitlements = s.Kind == ServicePackageKinds.Maintenance ? await data.ListEntitlementsAsync(s.Id, false, token) : [];
        var remaining = s.Kind == ServicePackageKinds.Maintenance
            ? entitlements.Count(e => e.Status is EntitlementStatuses.Available or EntitlementStatuses.Booked or EntitlementStatuses.NotCompleted)
            : (int?)null;
        return new SubscriptionView(s.Id, s.ContractId, s.ProjectId, s.Kind, s.Status, s.PackageName, s.Price, s.DurationMonths, AfterSalesRules.ParseMonths(s.VisitMonths),
            AfterSalesRules.ParseList(s.CoveredDisasterTypes), s.CoveragePercent, s.PerIncidentLimit, s.TermLimit, L(s.StartsAt), L(s.EndsAt), L(s.CoverageStartsAt),
            s.SupportReserved, s.SupportConsumed, s.TermLimit is null ? null : s.SupportAvailable, remaining, L(s.CheckoutExpiresAt), s.CurrentPaymentAttemptId, L(s.PaidAt),
            L(s.CancellationRequestedAt),
            entitlements.Select(e => new EntitlementView(e.Id, e.PlanMonth, L(e.PeriodStartsAt), L(e.PeriodEndsAt), e.Status, e.CurrentRequestId, L(e.ExtendedUntil),
                L(e.ConsumedAt))).ToList());
    }

    public static PackageView Package(ServicePackage p)
        => new(p.Id, p.Kind, p.Name, p.Description, p.Price, p.DurationMonths, AfterSalesRules.ParseMonths(p.VisitMonths), AfterSalesRules.ParseList(p.CoveredDisasterTypes),
            p.CoveragePercent, p.PerIncidentLimit, p.TermLimit, p.IsActive, p.Revision);

    public static WarrantyOptionView Option(WarrantyOption o) => new(o.Id, o.Kind, o.Months, o.IsDefault, o.IsActive);

    public SalesTaskView Task(SalesTask t)
        => new(t.Id, t.TaskType, t.Status, t.RequestId, t.EntitlementId, t.CustomerUserId, t.AssignedSalesUserId, L(t.ClaimedAt), L(t.StartedAt), L(t.CompletedAt),
            t.Outcome, t.Note, L(t.CreatedAt), t.Revision);

    public RefundView Refund(RefundRecord r)
        => new(r.Id, r.CustomerUserId, r.PaymentAttemptId, r.SubscriptionId, r.QuotationId, r.RequestId, r.OriginalAmount, r.ApprovedAmount, r.Reason, r.Status,
            r.ApprovedByUserId, L(r.ApprovedAt), r.DecisionNote, r.RecordedByUserId, L(r.RefundedAt), r.RefundReference, L(r.CreatedAt), r.Revision);

    public NotificationView Notification(AfterSalesNotification n) => new(n.Id, n.Type, n.Message, n.RequestId, n.SubscriptionId, L(n.CreatedAt), L(n.ReadAt));
}
