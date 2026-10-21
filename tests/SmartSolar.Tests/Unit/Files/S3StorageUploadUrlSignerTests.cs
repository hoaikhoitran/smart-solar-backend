using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Microsoft.Extensions.Logging.Abstractions;
using SmartSolar.Infrastructure.Storage;
using SmartSolar.Modules.Files;

namespace SmartSolar.Tests.Unit.Files;

/// <summary>
/// The real AWS SDK presigner with a fake credential: signing is computed locally, so nothing is sent to AWS. Checks the PUT URL
/// targets the right bucket/key over HTTPS, signs the Content-Type header, expires as asked and sets no ACL.
/// </summary>
public sealed class S3StorageUploadUrlSignerTests
{
    private const string Bucket = "smartsolar-test";
    private const string Key = "onsite-surveys/0c7d6f1e-5a51-4e43-8d0e-3f1b2a9c4d5e/abc123.jpg";

    private static S3StorageUploadUrlSigner Create()
    {
        var client = new AmazonS3Client(new BasicAWSCredentials("AKIAFAKEFORTESTS0000", "fake-secret-for-unit-tests-only"),
            new AmazonS3Config { RegionEndpoint = RegionEndpoint.APSoutheast1 });
        var options = new StorageOptions { DefaultBucket = Bucket };
        return new S3StorageUploadUrlSigner(client, options, TimeProvider.System, NullLogger<S3StorageUploadUrlSigner>.Instance);
    }

    [Fact]
    public async Task The_put_url_is_https_for_the_key_signs_the_content_type_and_expires_as_asked()
    {
        using var signer = Create();

        var result = await signer.CreateUploadUrlAsync(new StorageObjectLocation(Bucket, Key), FilePolicies.Jpeg, TimeSpan.FromSeconds(300), CancellationToken.None);

        Assert.Equal(StorageUrlStatus.Signed, result.Status);
        var url = new Uri(result.Url!);
        Assert.Equal(Uri.UriSchemeHttps, url.Scheme);
        Assert.Contains(Bucket, url.Host + url.AbsolutePath);
        Assert.EndsWith(Key, url.AbsolutePath);
        var query = Uri.UnescapeDataString(url.Query);
        Assert.Contains("X-Amz-Expires=300", query);
        Assert.Contains("content-type", query.Split('&').Single(p => p.StartsWith("X-Amz-SignedHeaders=", StringComparison.Ordinal)));
        Assert.DoesNotContain("x-amz-acl", query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fake-secret", result.Url!);
    }

    [Fact]
    public async Task Buckets_outside_the_configuration_are_never_signed()
    {
        using var signer = Create();

        var result = await signer.CreateUploadUrlAsync(new StorageObjectLocation("someone-elses-bucket", Key), FilePolicies.Jpeg, TimeSpan.FromSeconds(300),
            CancellationToken.None);

        Assert.Equal((StorageUrlStatus.Unavailable, (string?)null), (result.Status, result.Url));
    }
}
