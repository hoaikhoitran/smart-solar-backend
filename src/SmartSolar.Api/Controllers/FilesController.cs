using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SmartSolar.Api.Contracts.Files;
using SmartSolar.Api.Extensions;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.Common.Features;

namespace SmartSolar.Api.Controllers;

/// <summary>
/// Object references for files the frontend uploaded to S3. The backend never receives bytes; it
/// checks the key belongs to the caller and verifies the object against storage before any use.
/// </summary>
[ApiController]
[Route("api/files")]
[Authorize]
[RequireFeature(FeatureOptions.InstallationWorkflow, FeatureOptions.KnowledgeBase)]
public sealed class FilesController(FileService files, FileUploadUrlService uploads) : OperationResultControllerBase
{
    /// <summary>
    /// Step 1 of an upload: a short-lived presigned PUT URL for a new object key under the caller's own prefix. Then PUT the
    /// bytes to that URL with the returned headers (no Bearer token), and register the key with POST /api/files.
    /// </summary>
    [HttpPost("presigned-upload")]
    [EnableRateLimiting(RateLimitingExtensions.FilesWritePolicy)]
    public async Task<IActionResult> PresignedUpload([FromBody] PresignedUploadRequest request, CancellationToken cancellationToken)
    {
        NoStore();
        if (CurrentUserId() is not { } user) return UnauthorizedFailure();
        return Respond(await uploads.CreateAsync(new CreateUploadUrlCommand(user, IsInRole, request.Purpose ?? "", request.FileName ?? "",
            request.ContentType ?? "", request.SizeBytes), cancellationToken));
    }

    [HttpPost]
    [EnableRateLimiting(RateLimitingExtensions.FilesWritePolicy)]
    public async Task<IActionResult> Register([FromBody] RegisterFileRequest request, CancellationToken cancellationToken)
        => CurrentUserId() is { } user
            ? Respond(await files.RegisterAsync(new RegisterFileCommand(user, request.Purpose ?? "", request.Bucket, request.ObjectKey ?? "",
                request.FileName ?? "", request.ContentType ?? "", request.SizeBytes, request.ChecksumSha256), cancellationToken), StatusCodes.Status201Created)
            : UnauthorizedFailure();

    /// <summary>Retries verification of a PENDING_VERIFICATION file (for example after storage was unreachable).</summary>
    [HttpPost("{fileId:guid}/verify")]
    [EnableRateLimiting(RateLimitingExtensions.FilesWritePolicy)]
    public async Task<IActionResult> Verify(Guid fileId, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await files.VerifyAsync(fileId, user, IsStaff(), cancellationToken)) : UnauthorizedFailure();

    [HttpGet("{fileId:guid}")]
    [EnableRateLimiting(RateLimitingExtensions.FilesReadPolicy)]
    public async Task<IActionResult> Get(Guid fileId, CancellationToken cancellationToken)
        => CurrentUserId() is { } user ? Respond(await files.GetAsync(fileId, user, IsStaff(), cancellationToken)) : UnauthorizedFailure();

    private bool IsStaff() => IsInRole(RoleCodes.Admin) || IsInRole(RoleCodes.Manager) || IsInRole(RoleCodes.Sales);
}
