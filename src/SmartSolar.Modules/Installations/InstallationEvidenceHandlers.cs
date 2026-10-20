using Microsoft.Extensions.Logging;
using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.Scheduling.Domain;

namespace SmartSolar.Modules.Installations;

/// <summary>Who reads installation evidence; decides the project visibility rule.</summary>
public enum EvidenceViewerRole
{
    /// <summary>The project's customer only.</summary>
    Customer = 1,
    /// <summary>A technician with a current (PENDING/ACCEPTED) or COMPLETED assignment on the project.</summary>
    Technician = 2,
    /// <summary>Manager or Admin: any project.</summary>
    Staff = 3,
}

/// <summary>
/// One evidence photo's metadata (never its bucket, object key or version id). <see cref="Downloadable"/> is false when the
/// file is no longer VERIFIED (for example rejected by a later integrity check).
/// </summary>
public sealed record InstallationEvidenceFileView(Guid EvidenceId, int Round, string? Caption, DateTimeOffset AddedAt, string? ContentType, long? SizeBytes,
    string FileName, string? Sha256, bool Downloadable);

/// <summary>A short-lived presigned GET URL for one evidence photo. Request one per display; never store it beyond <see cref="ExpiresAt"/>.</summary>
public sealed record InstallationEvidenceUrlView(Guid EvidenceId, string Url, DateTimeOffset ExpiresAt, string Disposition, string ContentType, string FileName);

/// <summary>
/// Read access to installation evidence photos: list metadata and issue presigned read URLs, after authorizing the caller
/// for the project (customer owner, the involved technician, or staff; anyone else gets 404 without disclosure). The
/// object location always comes from the stored file reference, never from the client; signing is delegated to
/// <see cref="FileReadUrlService"/>. Logs carry evidence/project ids and the outcome only, never URLs or keys.
/// </summary>
public sealed class InstallationEvidenceHandlers(IInstallationUnitOfWork unitOfWork, IFileStore files, FileReadUrlService urls, WorkingCalendar calendar,
    ILogger<InstallationEvidenceHandlers> logger)
{
    public async Task<OperationResult<IReadOnlyList<InstallationEvidenceFileView>>> GetEvidenceAsync(EvidenceViewerRole role, Guid actorUserId, Guid projectId,
        CancellationToken cancellationToken)
    {
        if (!await CanSeeAsync(role, actorUserId, projectId, cancellationToken))
            return OperationResult<IReadOnlyList<InstallationEvidenceFileView>>.Fail(InstallationErrorCodes.ProjectNotFound, "Project was not found.");
        var evidence = (await unitOfWork.GetEvidenceAsync(projectId, cancellationToken)).OrderBy(e => e.Round).ThenBy(e => e.AddedAt).ToList();
        var stored = (await files.FindManyAsync(evidence.Select(e => e.FileId).Distinct().ToList(), cancellationToken)).ToDictionary(f => f.Id);
        var views = evidence.Select(e =>
        {
            var file = stored.GetValueOrDefault(e.FileId);
            var downloadable = file is { Status: FileStatuses.Verified, Purpose: FilePurposes.InstallationEvidence, VerifiedContentType: not null };
            return new InstallationEvidenceFileView(e.Id, e.Round, e.Caption, TimeZoneInfo.ConvertTime(e.AddedAt, calendar.TimeZone),
                downloadable ? file!.VerifiedContentType : null, downloadable ? file!.VerifiedSizeBytes : null,
                file is null ? "file" : ContentDispositionHeader.SafeFileName(file.OriginalFileName, file.VerifiedContentType ?? file.DeclaredContentType),
                downloadable ? file!.VerifiedChecksumSha256 : null, downloadable);
        }).ToList();
        return OperationResult<IReadOnlyList<InstallationEvidenceFileView>>.Ok(views);
    }

    public async Task<OperationResult<InstallationEvidenceUrlView>> CreateEvidenceUrlAsync(EvidenceViewerRole role, Guid actorUserId, Guid projectId, Guid evidenceId,
        StorageUrlDisposition disposition, CancellationToken cancellationToken)
    {
        if (!await CanSeeAsync(role, actorUserId, projectId, cancellationToken))
            return OperationResult<InstallationEvidenceUrlView>.Fail(InstallationErrorCodes.ProjectNotFound, "Project was not found.");
        var evidence = (await unitOfWork.GetEvidenceAsync(projectId, cancellationToken)).FirstOrDefault(e => e.Id == evidenceId);
        var file = evidence is null ? null : await files.FindAsync(evidence.FileId, cancellationToken);
        if (evidence is null || file is null)
            return OperationResult<InstallationEvidenceUrlView>.Fail(InstallationErrorCodes.EvidenceNotFound, "Evidence was not found.");

        var signed = await urls.CreateAsync(file, FilePurposes.InstallationEvidence, disposition, null, cancellationToken);
        if (!signed.Succeeded)
        {
            logger.LogWarning("Evidence {EvidenceId} of project {ProjectId}: read URL not issued ({Outcome}).", evidenceId, projectId, signed.ErrorCode);
            return signed.ErrorCode == FileErrorCodes.ObjectMissing
                ? OperationResult<InstallationEvidenceUrlView>.Fail(InstallationErrorCodes.EvidenceObjectMissing, "The photo is no longer available in storage.")
                : signed.As<InstallationEvidenceUrlView>();
        }

        var value = signed.Value!;
        logger.LogInformation("Evidence {EvidenceId} of project {ProjectId}: read URL issued ({Disposition}, role {Role}).", evidenceId, projectId, disposition, role);
        return OperationResult<InstallationEvidenceUrlView>.Ok(new InstallationEvidenceUrlView(evidenceId, value.Url, value.ExpiresAt,
            value.Disposition == StorageUrlDisposition.Attachment ? "attachment" : "inline", value.ContentType, value.FileName));
    }

    /// <summary>Same visibility rules as the project views: customer owner; technician current or completed assignment; staff any.</summary>
    private async Task<bool> CanSeeAsync(EvidenceViewerRole role, Guid actorUserId, Guid projectId, CancellationToken cancellationToken)
    {
        var project = await unitOfWork.FindProjectAsync(projectId, false, cancellationToken);
        if (project is null) return false;
        return role switch
        {
            EvidenceViewerRole.Staff => true,
            EvidenceViewerRole.Customer => project.CustomerUserId == actorUserId,
            EvidenceViewerRole.Technician => InstallationHandlers.VisibleAssignmentOf(await unitOfWork.GetAssignmentsAsync(projectId, false, cancellationToken), actorUserId) is not null,
            _ => false,
        };
    }
}
