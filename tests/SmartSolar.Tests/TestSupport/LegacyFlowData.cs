using Microsoft.EntityFrameworkCore;
using SmartSolar.Infrastructure.Persistence;
using SmartSolar.Infrastructure.Persistence.InstallationWorkflow;
using SmartSolar.Modules.CustomerContracts;
using SmartSolar.Modules.Quotations;

namespace SmartSolar.Tests.TestSupport;

/// <summary>
/// Data of the PREVIOUS commercial flow (before 2026-11-18), which production still holds: an installation quotation the customer accepted in the
/// app, with the contract DRAFT that acceptance created (to be signed, then paid by the customer). The handlers that produced it were retired, so tests
/// of the legacy contract / payment paths build exactly those rows here — with the production snapshot factory
/// (<see cref="ContractHandlers.BuildFromQuotation"/>) — instead of calling a removed customer endpoint.
/// </summary>
public static class LegacyFlowData
{
    /// <summary>ISSUED quotation → ACCEPTED by the customer + contract DRAFT (default warranty option), as the retired acceptance did.</summary>
    public static async Task<Guid> CustomerAcceptedWithDraftAsync(AppDbContext db, Guid quotationId, DateTimeOffset now)
    {
        var quotation = await db.Quotations.SingleAsync(q => q.Id == quotationId);
        if (quotation.Status != QuotationStatuses.Issued) throw new InvalidOperationException($"Legacy acceptance needs an ISSUED quotation, not {quotation.Status}.");
        quotation.Status = QuotationStatuses.Accepted;
        quotation.AcceptedAt = now;
        quotation.UpdatedAt = now;
        quotation.Revision++;
        db.QuotationEvents.Add(new QuotationEvent { Id = Guid.NewGuid(), QuotationId = quotationId, EventType = QuotationEventTypes.Accepted,
            ActorUserId = quotation.CustomerUserId, Detail = "legacy: accepted by the customer in the app", CreatedAt = now });
        await db.SaveChangesAsync();

        var uow = new ContractUnitOfWork(db);
        var source = (await uow.GetQuotationAsync(quotationId, CancellationToken.None))!;
        var (_, defaultMonths) = await uow.GetInstallationWarrantyOptionsAsync(CancellationToken.None);
        var contract = ContractHandlers.BuildFromQuotation(source, null, defaultMonths, now);
        db.CustomerContracts.Add(contract);
        db.ContractEvents.Add(new ContractEvent { Id = Guid.NewGuid(), ContractId = contract.Id, EventType = "CREATED", ToStatus = ContractStatuses.Draft,
            ActorUserId = quotation.CustomerUserId, Detail = "legacy: drafted on customer acceptance", CreatedAt = now });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return contract.Id;
    }

    /// <summary>ISSUED quotation → ACCEPTED by the customer in the app, WITHOUT a contract (legacy rows created before the automatic draft existed).</summary>
    public static async Task AcceptedWithoutContractAsync(AppDbContext db, Guid quotationId, DateTimeOffset now)
    {
        var quotation = await db.Quotations.SingleAsync(q => q.Id == quotationId);
        quotation.Status = QuotationStatuses.Accepted;
        quotation.AcceptedAt = now;
        quotation.UpdatedAt = now;
        quotation.Revision++;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }
}
