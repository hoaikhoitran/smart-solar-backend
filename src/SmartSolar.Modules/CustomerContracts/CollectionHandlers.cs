using System.Text.Json;
using SmartSolar.Modules.Common.Paging;
using SmartSolar.Modules.Common.Persistence;
using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.Identity.Constants;

namespace SmartSolar.Modules.CustomerContracts;

/// <param name="Kind">CONTACT, NEGOTIATION, AGREEMENT or NOTE.</param>
/// <param name="Channel">How the customer was reached (PHONE, VISIT, ZALO, EMAIL, OTHER), optional.</param>
/// <param name="PromisedPaymentDate">AGREEMENT only (required there): the date the customer promised to pay. Information only: the contractual due date
/// does not change.</param>
public sealed record CollectionActivityInput(string? Kind, string? Channel, string? Summary, DateOnly? PromisedPaymentDate, IReadOnlyList<Guid>? EvidenceFileIds,
    int? ExpectedRevision);

public sealed record CollectionActivityView(Guid ActivityId, string Kind, string? FromStatus, string? ToStatus, Guid? ActorUserId, string? ActorRole, string? Channel,
    string? Summary, DateOnly? PromisedPaymentDate, IReadOnlyList<Guid> EvidenceFileIds, DateTimeOffset OccurredAt);

/// <param name="DaysOverdue">Whole days since the due time (0 within the first day).</param>
public sealed record CollectionCaseView(Guid CaseId, Guid ContractId, string ContractNumber, Guid InstallmentId, int InstallmentSequence, Guid CustomerUserId,
    string CustomerName, string? CustomerPhone, string Status, DateTimeOffset OpenedAt, DateTimeOffset DueAt, int DaysOverdue, decimal AmountDue,
    string InstallmentStatus, DateOnly? PromisedPaymentDate, DateTimeOffset? ResolvedAt, Guid? ResolvedByUserId, string? Resolution, int Revision,
    IReadOnlyList<CollectionActivityView>? Activities);

/// <summary>
/// Collection follow-up of overdue installments (2026-11-19). People handle it; the system only records:
/// <list type="bullet">
/// <item>Sales / Manager / Admin record contacts, negotiations, agreements (promised date) and notes, with actor, role, time and optional verified
/// COLLECTION_EVIDENCE. An agreement never extends the contractual due date and never changes money.</item>
/// <item>Sales / Manager / Admin escalate to a Manager; Manager / Admin request legal review or resolve the case with a written resolution.</item>
/// <item>Nothing automatic happens to money or assets: no repossession, no forfeiture of the deposit, no refund button. Paying the installment closes
/// the case automatically (PAID).</item>
/// </list>
/// </summary>
public sealed class CollectionHandlers(IContractUnitOfWork contracts, FileService files, TimeProvider time)
{
    public static readonly IReadOnlyList<string> Channels = ["PHONE", "VISIT", "ZALO", "EMAIL", "SMS", "OTHER"];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<OperationResult<PagedResult<CollectionCaseView>>> PageAsync(CollectionCaseFilter filter, PageRequest page, CancellationToken cancellationToken)
    {
        if (filter.Status is { } status && !CollectionCaseStatuses.All.Contains(status))
            return OperationResult<PagedResult<CollectionCaseView>>.Fail(CommonErrorCodes.ValidationFailed, "One or more fields are invalid.",
                new Dictionary<string, string[]> { ["status"] = [$"One of {string.Join(", ", CollectionCaseStatuses.All)}."] });
        var (rows, total) = await contracts.PageCollectionCasesAsync(filter, page.Skip, page.PageSize, cancellationToken);
        var views = new List<CollectionCaseView>();
        foreach (var row in rows) views.Add(await ViewAsync(row, false, cancellationToken));
        return OperationResult<PagedResult<CollectionCaseView>>.Ok(page.Result(views, total));
    }

    public async Task<OperationResult<CollectionCaseView>> GetAsync(Guid caseId, CancellationToken cancellationToken)
        => await contracts.FindCollectionCaseAsync(caseId, false, cancellationToken) is { } found
            ? OperationResult<CollectionCaseView>.Ok(await ViewAsync(found, true, cancellationToken))
            : NotFound();

    /// <summary>CONTACT → CONTACTED, NEGOTIATION → NEGOTIATION_IN_PROGRESS, AGREEMENT → AGREEMENT_RECORDED, NOTE → unchanged. While escalated or in legal
    /// review the status stays (the Manager decides), the activity is still recorded.</summary>
    public async Task<OperationResult<CollectionCaseView>> RecordActivityAsync(Guid actorUserId, string actorRole, Guid caseId, CollectionActivityInput input,
        CancellationToken cancellationToken)
    {
        var kind = input.Kind?.Trim().ToUpperInvariant();
        var channel = input.Channel?.Trim().ToUpperInvariant();
        var summary = Trim(input.Summary, 2000);
        var errors = new Dictionary<string, string[]>();
        if (kind is null || !CollectionActivityKinds.Recordable.Contains(kind)) errors["kind"] = [$"One of {string.Join(", ", CollectionActivityKinds.Recordable)}."];
        if (channel is not null && !Channels.Contains(channel)) errors["channel"] = [$"One of {string.Join(", ", Channels)}."];
        if (summary is null) errors["summary"] = ["Describe what happened (at most 2000 characters)."];
        if (kind == CollectionActivityKinds.Agreement && input.PromisedPaymentDate is null) errors["promisedPaymentDate"] = ["Required for an agreement."];
        if (input.EvidenceFileIds is { Count: > 10 }) errors["evidenceFileIds"] = ["At most 10 files."];
        if (errors.Count > 0) return Invalid(errors);
        var evidence = input.EvidenceFileIds?.Distinct().ToList() ?? [];
        if (evidence.Count > 0)
        {
            var verified = await files.RequireVerifiedAsync(evidence, FilePurposes.CollectionEvidence, actorUserId, cancellationToken);
            if (!verified.Succeeded) return verified.As<CollectionCaseView>();
        }
        return await RunAsync(caseId, input.ExpectedRevision, (c, now) =>
        {
            var to = c.Status is CollectionCaseStatuses.EscalatedToManager or CollectionCaseStatuses.LegalReviewRequired ? c.Status : kind switch
            {
                CollectionActivityKinds.Contact => CollectionCaseStatuses.Contacted,
                CollectionActivityKinds.Negotiation => CollectionCaseStatuses.NegotiationInProgress,
                CollectionActivityKinds.Agreement => CollectionCaseStatuses.AgreementRecorded,
                _ => c.Status,
            };
            if (kind == CollectionActivityKinds.Agreement) c.PromisedPaymentDate = input.PromisedPaymentDate;
            Activity(c, kind!, to, actorUserId, actorRole, channel, summary, input.PromisedPaymentDate, evidence, now);
            return null;
        }, cancellationToken);
    }

    /// <summary>Sales / Manager / Admin hand the case to a Manager (reason required).</summary>
    public Task<OperationResult<CollectionCaseView>> EscalateAsync(Guid actorUserId, string actorRole, Guid caseId, string? reason, int? expectedRevision,
        CancellationToken cancellationToken)
        => TransitionAsync(actorUserId, actorRole, caseId, reason, expectedRevision, CollectionActivityKinds.Escalate, CollectionCaseStatuses.EscalatedToManager,
            [CollectionCaseStatuses.EscalatedToManager, CollectionCaseStatuses.LegalReviewRequired], cancellationToken);

    /// <summary>Manager / Admin: the case needs a legal review (the system records it; it never decides anything legal).</summary>
    public Task<OperationResult<CollectionCaseView>> RequestLegalReviewAsync(Guid actorUserId, string actorRole, Guid caseId, string? reason, int? expectedRevision,
        CancellationToken cancellationToken)
        => RequireManager(actorRole) ?? TransitionAsync(actorUserId, actorRole, caseId, reason, expectedRevision, CollectionActivityKinds.LegalReview,
            CollectionCaseStatuses.LegalReviewRequired, [CollectionCaseStatuses.LegalReviewRequired], cancellationToken);

    /// <summary>Manager / Admin close the case with a written resolution. Money and the installment are untouched (an unpaid installment stays DUE).</summary>
    public Task<OperationResult<CollectionCaseView>> ResolveAsync(Guid actorUserId, string actorRole, Guid caseId, string? resolution, int? expectedRevision,
        CancellationToken cancellationToken)
        => RequireManager(actorRole) ?? TransitionAsync(actorUserId, actorRole, caseId, resolution, expectedRevision, CollectionActivityKinds.Resolve,
            CollectionCaseStatuses.Resolved, [], cancellationToken);

    private Task<OperationResult<CollectionCaseView>> TransitionAsync(Guid actorUserId, string actorRole, Guid caseId, string? reason, int? expectedRevision,
        string kind, string to, IReadOnlyList<string> refusedFrom, CancellationToken cancellationToken)
    {
        var why = Trim(reason, 1000);
        if (why is null) return Task.FromResult(Invalid(new Dictionary<string, string[]> { ["reason"] = ["A reason of at most 1000 characters is required."] }));
        return RunAsync(caseId, expectedRevision, (c, now) =>
        {
            if (refusedFrom.Contains(c.Status))
                return OperationResult<bool>.Fail(PaymentPlanErrorCodes.CollectionInvalidTransition, $"The case is already {c.Status}.");
            if (to == CollectionCaseStatuses.Resolved)
            {
                c.ResolvedAt = now;
                c.ResolvedByUserId = actorUserId;
                c.Resolution = why;
            }
            Activity(c, kind, to, actorUserId, actorRole, null, why, null, [], now);
            return null;
        }, cancellationToken);
    }

    private void Activity(ContractCollectionCase c, string kind, string to, Guid actorUserId, string actorRole, string? channel, string? summary, DateOnly? promised,
        IReadOnlyList<Guid> evidence, DateTimeOffset now)
    {
        contracts.AddCollectionActivity(new ContractCollectionActivity
        {
            Id = Guid.NewGuid(), CaseId = c.Id, Kind = kind, FromStatus = c.Status, ToStatus = to, ActorUserId = actorUserId, ActorRole = actorRole, Channel = channel,
            Summary = summary, PromisedPaymentDate = promised, EvidenceFileIds = evidence.Count == 0 ? null : JsonSerializer.Serialize(evidence, Json), OccurredAt = now,
        });
        c.Status = to;
    }

    private async Task<OperationResult<CollectionCaseView>> RunAsync(Guid caseId, int? expectedRevision,
        Func<ContractCollectionCase, DateTimeOffset, OperationResult<bool>?> change, CancellationToken cancellationToken)
    {
        var probe = await contracts.FindCollectionCaseAsync(caseId, false, cancellationToken);
        if (probe is null) return NotFound();
        try
        {
            var outcome = await contracts.ExecuteInTransactionAsync<OperationResult<bool>>(async token =>
            {
                await contracts.LockContractAsync(probe.ContractId, token);
                var c = await contracts.FindCollectionCaseAsync(caseId, true, token);
                if (c is null) return (OperationResult<bool>.Fail(PaymentPlanErrorCodes.CollectionNotFound, "Collection case was not found."), false);
                if (c.Status == CollectionCaseStatuses.Resolved)
                    return (OperationResult<bool>.Fail(PaymentPlanErrorCodes.CollectionClosed, "The collection case is resolved; nothing can be recorded on it."), false);
                if (expectedRevision is { } expected && c.Revision != expected)
                    return (OperationResult<bool>.Fail(PaymentPlanErrorCodes.StaleRevision, "The case changed meanwhile. Reload and try again.",
                        new { currentRevision = c.Revision }), false);
                var now = time.GetUtcNow();
                if (change(c, now) is { } refused) return (refused, false);
                c.UpdatedAt = now;
                c.Revision++;
                await contracts.SaveChangesAsync(token);
                return (OperationResult<bool>.Ok(true), true);
            }, cancellationToken);
            if (!outcome.Succeeded) return outcome.As<CollectionCaseView>();
        }
        catch (PersistenceConflictException)
        {
            return OperationResult<CollectionCaseView>.Fail(PaymentPlanErrorCodes.StaleRevision, "The case changed meanwhile. Reload and try again.");
        }
        return OperationResult<CollectionCaseView>.Ok(await ViewAsync((await contracts.FindCollectionCaseAsync(caseId, false, cancellationToken))!, true, cancellationToken));
    }

    private async Task<CollectionCaseView> ViewAsync(ContractCollectionCase c, bool withActivities, CancellationToken cancellationToken)
    {
        var row = await contracts.GetContractRowAsync(c.ContractId, cancellationToken);
        var installment = await contracts.FindInstallmentAsync(c.InstallmentId, false, cancellationToken);
        var now = time.GetUtcNow();
        var activities = withActivities
            ? (await contracts.GetCollectionActivitiesAsync(c.Id, cancellationToken)).Select(a => new CollectionActivityView(a.Id, a.Kind, a.FromStatus, a.ToStatus,
                a.ActorUserId, a.ActorRole, a.Channel, a.Summary, a.PromisedPaymentDate,
                a.EvidenceFileIds is null ? [] : JsonSerializer.Deserialize<List<Guid>>(a.EvidenceFileIds, Json) ?? [], a.OccurredAt)).ToList()
            : null;
        return new CollectionCaseView(c.Id, c.ContractId, row?.ContractNumber ?? "", c.InstallmentId, installment?.Sequence ?? 0, c.CustomerUserId, row?.CustomerName ?? "",
            row?.CustomerPhone, c.Status, c.OpenedAt, c.DueAt, now > c.DueAt ? (int)Math.Floor((now - c.DueAt).TotalDays) : 0, c.AmountDue, installment?.Status ?? "",
            c.PromisedPaymentDate, c.ResolvedAt, c.ResolvedByUserId, c.Resolution, c.Revision, activities);
    }

    private static Task<OperationResult<CollectionCaseView>>? RequireManager(string actorRole)
        => actorRole is RoleCodes.Manager or RoleCodes.Admin ? null
            : Task.FromResult(OperationResult<CollectionCaseView>.Fail(CommonErrorCodes.Forbidden, "Only a Manager or Admin can do this."));

    private static OperationResult<CollectionCaseView> NotFound() => OperationResult<CollectionCaseView>.Fail(PaymentPlanErrorCodes.CollectionNotFound, "Collection case was not found.");
    private static OperationResult<CollectionCaseView> Invalid(Dictionary<string, string[]> errors)
        => OperationResult<CollectionCaseView>.Fail(CommonErrorCodes.ValidationFailed, "One or more fields are invalid.", errors);
    private static string? Trim(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length > max ? value.Trim()[..max] : value.Trim();
}
