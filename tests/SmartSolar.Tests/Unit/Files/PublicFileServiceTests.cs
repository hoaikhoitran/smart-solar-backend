using Microsoft.Extensions.Logging.Abstractions;
using SmartSolar.Api.Controllers;
using SmartSolar.Infrastructure.Persistence;
using SmartSolar.Modules.Files;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.Files;

/// <summary>
/// Catalog pictures uploaded from a staff device: their stored address is the anonymous redirect endpoint under the configured public base,
/// and that endpoint signs only VERIFIED files of the two public purposes. Storage and the presigner are synthetic.
/// </summary>
public sealed class PublicFileServiceTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private const string PublicBase = "https://app.smartsolar.test";

    private static (PublicFileService Service, FakeStorageUrlSigner Signer) Service(InstallationWorkflowTestContext ctx, string? publicBase = PublicBase)
    {
        var options = new StorageOptions { DefaultBucket = InstallationWorkflowTestContext.Bucket, PublicFileBaseUrl = publicBase ?? "" };
        var signer = new FakeStorageUrlSigner(ctx.Clock);
        var urls = new FileReadUrlService(signer, ctx.Storage, options, ctx.Clock, NullLogger<FileReadUrlService>.Instance);
        return (new PublicFileService(new FileStore(ctx.Db), urls, options, NullLogger<PublicFileService>.Instance), signer);
    }

    private static async Task<VerifiedFile> VerifiedAsync(InstallationWorkflowTestContext ctx, Guid fileId, string purpose)
        => (await ctx.Files().RequireVerifiedAsync([fileId], purpose, null, None)).Value![0];

    [Fact]
    public async Task A_verified_catalog_picture_gets_a_stable_public_address_and_an_anonymous_presigned_read()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var admin = (await ctx.Scheduling.Inner.SeedUserAsync("Admin")).Id;
        var product = await ctx.VerifiedFileAsync(admin, FilePurposes.ProductImage);
        var package = await ctx.VerifiedFileAsync(admin, FilePurposes.PackageImage, "png");
        var (service, signer) = Service(ctx);

        Assert.Equal($"{PublicBase}/api/files/public/{product:D}", (service.PublicUrlFor(await VerifiedAsync(ctx, product, FilePurposes.ProductImage))).Value);
        Assert.Equal($"{PublicBase}/api/files/public/{package:D}", (service.PublicUrlFor(await VerifiedAsync(ctx, package, FilePurposes.PackageImage))).Value);
        // A trailing slash in the configuration does not double the separator.
        Assert.Equal($"{PublicBase}/api/files/public/{product:D}", Service(ctx, PublicBase + "/").Service.PublicUrlFor(await VerifiedAsync(ctx, product, FilePurposes.ProductImage)).Value);

        var read = await service.CreateReadUrlAsync(product, None);
        Assert.True(read.Succeeded, read.Message);
        Assert.StartsWith(FakeStorageUrlSigner.Host, read.Value!.Url);
        Assert.Equal((StorageUrlDisposition.Inline, FilePolicies.Jpeg), (read.Value.Disposition, read.Value.ContentType));
        var call = Assert.Single(signer.Calls);
        Assert.StartsWith("products/", call.Location.ObjectKey);
        Assert.NotNull(call.Location.VersionId);   // pinned to the verified version
    }

    [Fact]
    public async Task Only_verified_pictures_of_the_public_purposes_are_served_everything_else_is_not_found()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var admin = (await ctx.Scheduling.Inner.SeedUserAsync("Admin")).Id;
        var evidence = await ctx.VerifiedFileAsync(admin, FilePurposes.InstallationEvidence);
        var document = await ctx.VerifiedFileAsync(admin, FilePurposes.KnowledgeDocument, "pdf");
        var pending = await ctx.UnverifiedFileAsync(admin, FilePurposes.ProductImage);
        var (service, signer) = Service(ctx);

        foreach (var id in new[] { evidence, document, pending, Guid.NewGuid() })
        {
            var refused = await service.CreateReadUrlAsync(id, None);
            Assert.Equal(FileErrorCodes.NotFound, refused.ErrorCode);
            Assert.Equal(404, OperationResultControllerBase.StatusFor(refused.ErrorCode!));
        }
        Assert.Empty(signer.Calls);   // nothing was signed for a private or unverified file
        var wrongPurpose = service.PublicUrlFor(await VerifiedAsync(ctx, evidence, FilePurposes.InstallationEvidence));
        Assert.Equal(FileErrorCodes.WrongPurpose, wrongPurpose.ErrorCode);
    }

    [Fact]
    public async Task Without_a_public_base_url_no_address_is_issued_and_the_outcome_is_a_503()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var admin = (await ctx.Scheduling.Inner.SeedUserAsync("Admin")).Id;
        var product = await ctx.VerifiedFileAsync(admin, FilePurposes.ProductImage);
        var (service, _) = Service(ctx, publicBase: null);

        var refused = service.PublicUrlFor(await VerifiedAsync(ctx, product, FilePurposes.ProductImage));

        Assert.Equal(StorageErrorCodes.PublicUrlNotConfigured, refused.ErrorCode);
        Assert.Equal(503, OperationResultControllerBase.StatusFor(refused.ErrorCode!));
        Assert.Null(service.PublicBase);
        // Reading already uploaded pictures does not depend on the base URL (the redirect endpoint signs by file id).
        Assert.True((await service.CreateReadUrlAsync(product, None)).Succeeded);
    }
}
