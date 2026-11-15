using Microsoft.Extensions.Logging;
using SmartSolar.Modules.Common.Results;

namespace SmartSolar.Modules.Files;

/// <summary>
/// Catalog pictures (product gallery images, service package covers) uploaded from a staff member's device through the normal presigned
/// upload. The bucket stays private: the picture is stored on the product or package as the stable address
/// <c>{Storage:PublicFileBaseUrl}/api/files/public/{fileId}</c>, and that endpoint answers anyone with a redirect to a short-lived presigned
/// URL. Only VERIFIED files of <see cref="FilePurposes.PublicImagePurposes"/> are ever served this way; every other purpose stays behind the
/// module authorizations. File ids are random, so an uploaded-but-unused picture is not enumerable.
/// </summary>
public sealed class PublicFileService(IFileStore store, FileReadUrlService urls, StorageOptions options, ILogger<PublicFileService> logger)
{
    /// <summary>Route of the anonymous redirect endpoint, appended to <see cref="StorageOptions.PublicFileBaseUrl"/>.</summary>
    public const string RoutePrefix = "/api/files/public/";

    public static bool IsPublicPurpose(string purpose) => FilePurposes.PublicImagePurposes.Contains(purpose);

    /// <summary>Normalised base (no trailing slash), or null when device uploads for catalog images are not configured.</summary>
    public string? PublicBase => string.IsNullOrWhiteSpace(options.PublicFileBaseUrl) ? null : options.PublicFileBaseUrl.Trim().TrimEnd('/');

    /// <summary>The stable https address to store for a verified public image; STORAGE_PUBLIC_URL_NOT_CONFIGURED when there is none.</summary>
    public OperationResult<string> PublicUrlFor(VerifiedFile file)
    {
        if (!IsPublicPurpose(file.Purpose))
            return OperationResult<string>.Fail(FileErrorCodes.WrongPurpose, $"File {file.FileId} was not registered as a catalog image.");
        return PublicBase is { } root
            ? OperationResult<string>.Ok($"{root}{RoutePrefix}{file.FileId:D}")
            : OperationResult<string>.Fail(StorageErrorCodes.PublicUrlNotConfigured,
                "Uploading catalog images from a device is not configured on this server (Storage:PublicFileBaseUrl); use a link to a hosted picture instead.");
    }

    /// <summary>
    /// Anonymous read of one catalog image: a short-lived presigned inline URL for a VERIFIED file of a public purpose. Unknown ids, other
    /// purposes and unverified files all answer FILE_NOT_FOUND (no disclosure); a deleted object answers FILE_OBJECT_MISSING (410).
    /// </summary>
    public async Task<OperationResult<FileReadUrl>> CreateReadUrlAsync(Guid fileId, CancellationToken cancellationToken)
    {
        var file = await store.FindAsync(fileId, cancellationToken);
        if (file is null || !IsPublicPurpose(file.Purpose) || file.Status != FileStatuses.Verified)
            return OperationResult<FileReadUrl>.Fail(FileErrorCodes.NotFound, "File was not found.");
        var signed = await urls.CreateAsync(file, file.Purpose, StorageUrlDisposition.Inline, null, cancellationToken);
        if (!signed.Succeeded) logger.LogWarning("Public image {FileId}: read URL not issued ({Outcome}).", fileId, signed.ErrorCode);
        return signed;
    }
}
