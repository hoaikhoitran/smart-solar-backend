using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.Files;

namespace SmartSolar.Modules.AfterSales;

/// <summary>
/// Short-lived read URLs for after-sales evidence photos (customer fault photos and technician visit photos). Access follows the
/// existing views: the request's customer, the technician of a visit of that request, or a Manager / Admin. Only evidence of
/// that request is signed; anyone else gets 404. The object location comes from the stored file, never from the client.
/// </summary>
public sealed class AfterSalesEvidenceUrlHandlers(IAfterSalesUnitOfWork data, IFileStore files, FileReadUrlService urls)
{
    public async Task<OperationResult<AttachedFileUrlView>> ForCustomerAsync(Guid customerUserId, Guid requestId, Guid evidenceId,
        StorageUrlDisposition disposition, CancellationToken cancellationToken)
    {
        var request = await data.FindRequestAsync(requestId, false, cancellationToken);
        if (request is null || request.CustomerUserId != customerUserId)
            return OperationResult<AttachedFileUrlView>.Fail(AfterSalesErrorCodes.RequestNotFound, "Service request was not found.");
        return await SignAsync(request.Id, evidenceId, disposition, cancellationToken);
    }

    public async Task<OperationResult<AttachedFileUrlView>> ForTechnicianAsync(Guid technicianUserId, Guid visitId, Guid evidenceId,
        StorageUrlDisposition disposition, CancellationToken cancellationToken)
    {
        var visit = await data.FindVisitAsync(visitId, false, cancellationToken);
        if (visit is null || visit.TechnicianUserId != technicianUserId)
            return OperationResult<AttachedFileUrlView>.Fail(AfterSalesErrorCodes.VisitNotFound, "Visit was not found.");
        return await SignAsync(visit.RequestId, evidenceId, disposition, cancellationToken);
    }

    public async Task<OperationResult<AttachedFileUrlView>> ForStaffAsync(Guid requestId, Guid evidenceId, StorageUrlDisposition disposition,
        CancellationToken cancellationToken)
    {
        if (await data.FindRequestAsync(requestId, false, cancellationToken) is null)
            return OperationResult<AttachedFileUrlView>.Fail(AfterSalesErrorCodes.RequestNotFound, "Service request was not found.");
        return await SignAsync(requestId, evidenceId, disposition, cancellationToken);
    }

    private async Task<OperationResult<AttachedFileUrlView>> SignAsync(Guid requestId, Guid evidenceId, StorageUrlDisposition disposition,
        CancellationToken cancellationToken)
    {
        var evidence = (await data.ListEvidenceAsync(requestId, cancellationToken)).FirstOrDefault(e => e.Id == evidenceId);
        var file = evidence is null ? null : await files.FindAsync(evidence.FileId, cancellationToken);
        if (evidence is null || file is null)
            return OperationResult<AttachedFileUrlView>.Fail(AfterSalesErrorCodes.EvidenceNotFound, "Evidence was not found.");

        var signed = await urls.CreateAsync(file, FilePurposes.ServiceEvidence, disposition, null, cancellationToken);
        return signed.Succeeded
            ? OperationResult<AttachedFileUrlView>.Ok(AttachedFileUrlView.From(evidenceId, signed.Value!))
            : signed.As<AttachedFileUrlView>();
    }
}
