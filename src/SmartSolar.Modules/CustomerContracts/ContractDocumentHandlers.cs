using Microsoft.Extensions.Logging;
using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.Files;

namespace SmartSolar.Modules.CustomerContracts;

/// <summary>Who asks for a contract document: decides the visibility rule (the same as the contract views).</summary>
public enum ContractViewer
{
    /// <summary>The contract's customer; only customer-visible versions (legacy: from AWAITING_SIGNATURE on, superseded included; 30/70: from creation).</summary>
    Customer = 1,
    /// <summary>Sales, Technician, Manager or Admin: every contract and version (read-all since 2026-11-19; reads are audited).</summary>
    Staff = 2,
}

/// <summary>
/// Short-lived read URLs for the official contract PDF and for the customer's signed scans, for every version the caller may see: an older
/// (SUPERSEDED) version stays downloadable as history, which changes nothing about signing or paying (payment stays SIGNED-only, through the
/// payment endpoints). The object location comes from the stored file reference, pinned to the verified version, never from the client.
/// Anyone outside the visibility rule gets 404 without disclosure.
/// </summary>
public sealed class ContractDocumentHandlers(IContractUnitOfWork contracts, IFileStore files, FileReadUrlService urls, ILogger<ContractDocumentHandlers> logger)
{
    /// <summary>The official PDF Sales confirmed for this version (409 CONTRACT_DOCUMENT_MISSING before one is attached).</summary>
    public async Task<OperationResult<AttachedFileUrlView>> OfficialDocumentUrlAsync(ContractViewer viewer, Guid actorUserId, bool isManager, Guid contractId,
        StorageUrlDisposition disposition, CancellationToken cancellationToken)
    {
        var contract = await VisibleAsync(viewer, actorUserId, isManager, contractId, cancellationToken);
        if (contract is null) return NotFound();
        if (contract.OfficialDocumentFileId is not { } fileId)
            return OperationResult<AttachedFileUrlView>.Fail(ContractErrorCodes.DocumentMissing, "No official document is attached to this contract version yet.");
        return await SignAsync(contract, fileId, FilePurposes.ContractDocument, disposition, cancellationToken);
    }

    /// <summary>One signed scan the customer submitted for this version (rejected scans included, as history).</summary>
    public async Task<OperationResult<AttachedFileUrlView>> SubmissionUrlAsync(ContractViewer viewer, Guid actorUserId, bool isManager, Guid contractId, Guid submissionId,
        StorageUrlDisposition disposition, CancellationToken cancellationToken)
    {
        var contract = await VisibleAsync(viewer, actorUserId, isManager, contractId, cancellationToken);
        if (contract is null) return NotFound();
        var submission = (await contracts.GetSubmissionsAsync(contractId, false, cancellationToken)).FirstOrDefault(s => s.Id == submissionId);
        if (submission is null) return OperationResult<AttachedFileUrlView>.Fail(ContractErrorCodes.SubmissionNotFound, "The signed document was not found.");
        return await SignAsync(contract, submission.FileId, FilePurposes.SignedContract, disposition, cancellationToken);
    }

    /// <summary>
    /// One photographed page of the signed paper contract (30/70 plan), of the current set or of an older (superseded / replaced) upload as history.
    /// Short-lived signed URL; every issued URL is recorded in the contract access audit.
    /// </summary>
    public async Task<OperationResult<AttachedFileUrlView>> SignedPageUrlAsync(ContractViewer viewer, Guid actorUserId, string actorRole, Guid contractId, Guid fileId,
        StorageUrlDisposition disposition, CancellationToken cancellationToken)
    {
        var contract = await VisibleAsync(viewer, actorUserId, true, contractId, cancellationToken);
        if (contract is null) return NotFound();
        var page = (await contracts.GetSignedPagesAsync(contractId, false, cancellationToken)).FirstOrDefault(p => p.FileId == fileId);
        if (page is null) return OperationResult<AttachedFileUrlView>.Fail(PaymentPlanErrorCodes.SignatureSetNotFound, "The signed page was not found.");
        var signed = await SignAsync(contract, fileId, FilePurposes.SignedContractPage, disposition, cancellationToken);
        if (signed.Succeeded) await AuditAsync(contractId, actorUserId, actorRole, ContractAccessActions.PageUrl, $"page {page.PageNumber} file {fileId}", cancellationToken);
        return signed;
    }

    /// <summary>Records one read in the contract access audit (own short transaction; the read itself never fails because of it).</summary>
    public async Task AuditAsync(Guid? contractId, Guid actorUserId, string actorRole, string action, string? detail, CancellationToken cancellationToken)
    {
        try
        {
            contracts.AddAccessAudit(new ContractAccessAudit { Id = Guid.NewGuid(), ContractId = contractId, ActorUserId = actorUserId, ActorRole = actorRole,
                Action = action, Detail = detail is { Length: > 500 } ? detail[..500] : detail, CreatedAt = DateTimeOffset.UtcNow });
            await contracts.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Contract access audit not recorded for {ContractId} ({Action}).", contractId, action);
        }
    }

    /// <summary>Same rules as <see cref="ContractHandlers.GetForStaffAsync"/> (read-all) and <see cref="ContractHandlers.GetForCustomerAsync"/>.</summary>
    private async Task<CustomerContract?> VisibleAsync(ContractViewer viewer, Guid actorUserId, bool isManager, Guid contractId, CancellationToken cancellationToken)
    {
        var contract = await contracts.FindAsync(contractId, false, cancellationToken);
        if (contract is null) return null;
        return viewer switch
        {
            ContractViewer.Customer when contract.CustomerUserId == actorUserId && ContractStatuses.IsCustomerVisible(contract) => contract,
            ContractViewer.Staff => contract,
            _ => null,
        };
    }

    private async Task<OperationResult<AttachedFileUrlView>> SignAsync(CustomerContract contract, Guid fileId, string purpose, StorageUrlDisposition disposition,
        CancellationToken cancellationToken)
    {
        var file = await files.FindAsync(fileId, cancellationToken);
        if (file is null) return OperationResult<AttachedFileUrlView>.Fail(FileErrorCodes.NotFound, "The document file was not found.");
        var signed = await urls.CreateAsync(file, purpose, disposition, null, cancellationToken);
        if (!signed.Succeeded)
        {
            logger.LogWarning("Contract {ContractId} v{Version}: read URL for {Purpose} file {FileId} not issued ({Outcome}).", contract.Id, contract.Version, purpose, fileId,
                signed.ErrorCode);
            return signed.As<AttachedFileUrlView>();
        }
        logger.LogInformation("Contract {ContractId} v{Version}: read URL issued for {Purpose} ({Disposition}).", contract.Id, contract.Version, purpose, disposition);
        return OperationResult<AttachedFileUrlView>.Ok(AttachedFileUrlView.From(fileId, signed.Value!));
    }

    private static OperationResult<AttachedFileUrlView> NotFound() => OperationResult<AttachedFileUrlView>.Fail(ContractErrorCodes.NotFound, "Contract was not found.");
}
