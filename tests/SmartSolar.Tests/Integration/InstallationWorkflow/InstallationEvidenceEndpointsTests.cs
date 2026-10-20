using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.Installations;
using SmartSolar.Tests.Integration.Catalog;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.FeatureEndpointTestSupport;

namespace SmartSolar.Tests.Integration.InstallationWorkflow;

/// <summary>
/// Installation evidence read endpoints through the real pipeline (auth, roles, feature gate, rate limiting, envelope,
/// headers). Storage and the presigner are synthetic; an IN_PROGRESS project with one verified photo is seeded directly.
/// </summary>
public sealed class InstallationEvidenceEndpointsTests
{
    private sealed record Seeded(Guid ProjectId, Guid EvidenceId, StoredFile File, Guid CustomerId, Guid TechnicianId);

    private sealed record Api(AuthApiFactory Factory, FakeStorageObjectVerifier Storage, FakeStorageUrlSigner Signer, CapturingLoggerProvider Logs) : IDisposable
    {
        public void Dispose() => Factory.Dispose();
    }

    private static Api Start(Dictionary<string, string?>? extra = null, bool replaceSigner = true)
    {
        var storage = new FakeStorageObjectVerifier();
        var signer = new FakeStorageUrlSigner();
        var logs = new CapturingLoggerProvider();
        var factory = Enabled(storage, new FakeRagServiceClient(), extra, services =>
        {
            if (replaceSigner)
            {
                services.RemoveAll<IStorageUrlSigner>();
                services.AddSingleton<IStorageUrlSigner>(signer);
            }
            services.AddSingleton<ILoggerProvider>(logs);
        });
        return new Api(factory, storage, signer, logs);
    }

    /// <summary>An IN_PROGRESS project of <paramref name="customerId"/> with <paramref name="technicianId"/>'s ACCEPTED assignment and one verified photo.</summary>
    private static async Task<Seeded> SeedAsync(Api api, Guid customerId, Guid technicianId)
    {
        var now = DateTimeOffset.UtcNow;
        var key = $"{FilePolicies.ByPurpose[FilePurposes.InstallationEvidence].KeyPrefix}{technicianId}/{Guid.NewGuid():N}.jpg";
        var version = api.Storage.Put(Bucket, key, FakeStorageObjectVerifier.JpegBytes);
        var file = new StoredFile
        {
            Id = Guid.NewGuid(), Purpose = FilePurposes.InstallationEvidence, Provider = "S3", Bucket = Bucket, ObjectKey = key, OriginalFileName = "mái nhà.jpg",
            DeclaredContentType = FilePolicies.Jpeg, DeclaredSizeBytes = FakeStorageObjectVerifier.JpegBytes.Length, ObjectVersionId = version, ETag = "\"etag\"",
            VerifiedContentType = FilePolicies.Jpeg, VerifiedSizeBytes = FakeStorageObjectVerifier.JpegBytes.Length,
            VerifiedChecksumSha256 = FakeStorageObjectVerifier.Sha256(FakeStorageObjectVerifier.JpegBytes), Status = FileStatuses.Verified, VerificationAttempts = 1,
            UploadedByUserId = technicianId, VerifiedAt = now, CreatedAt = now, UpdatedAt = now,
        };
        var projectId = Guid.NewGuid();
        var evidenceId = Guid.NewGuid();
        await using var db = api.Factory.CreateDbContext();
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF");
        try
        {
            db.InstallationProjects.Add(new InstallationProject
            {
                Id = projectId, ContractId = Guid.NewGuid(), SurveyRequestId = Guid.NewGuid(), CustomerUserId = customerId, PropertySiteId = Guid.NewGuid(),
                ManagerUserId = Guid.NewGuid(), Status = InstallationStatuses.InProgress, ResponsibleTechnicianUserId = technicianId, TechnicianAcceptedAt = now.AddDays(-2),
                ScheduledStartsAt = now.AddHours(-2), ScheduledEndsAt = now.AddHours(6), StartedAt = now.AddHours(-1), CreatedAt = now.AddDays(-3), UpdatedAt = now,
            });
            db.InstallationAssignments.Add(new InstallationAssignment
            {
                Id = Guid.NewGuid(), ProjectId = projectId, TechnicianUserId = technicianId, StartsAt = now.AddHours(-2), EndsAt = now.AddHours(6),
                Status = AssignmentStatuses.Accepted, AssignedByUserId = Guid.NewGuid(), AssignedAt = now.AddDays(-3), RespondedAt = now.AddDays(-2),
            });
            db.StoredFiles.Add(file);
            db.InstallationEvidence.Add(new InstallationEvidence
            {
                Id = evidenceId, ProjectId = projectId, Round = 1, FileId = file.Id, Caption = "Array", AddedByUserId = technicianId, AddedAt = now,
            });
            await db.SaveChangesAsync();
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON");
        }
        return new Seeded(projectId, evidenceId, file, customerId, technicianId);
    }

    private static string CustomerUrl(Seeded s, string? query = null) => $"/api/customers/me/installation-projects/{s.ProjectId}/evidence/{s.EvidenceId}/url{query}";
    private static string TechnicianUrl(Seeded s) => $"/api/technicians/me/installation-projects/{s.ProjectId}/evidence/{s.EvidenceId}/url";
    private static string StaffUrl(Seeded s) => $"/api/installation-projects/{s.ProjectId}/evidence/{s.EvidenceId}/url";

    private static void AssertNoStore(HttpResponseMessage response)
    {
        Assert.True(response.Headers.CacheControl?.NoStore, $"Cache-Control was '{response.Headers.CacheControl}'.");
        Assert.Contains("no-cache", response.Headers.Pragma.ToString());
    }

    /// <summary>Nothing outside the url field reveals where or how the object is stored.</summary>
    private static void AssertNoStorageLocation(string json, StoredFile file)
    {
        Assert.DoesNotContain(file.ObjectKey, json);
        Assert.DoesNotContain(Bucket, json);
        Assert.DoesNotContain(file.ObjectVersionId!, json, StringComparison.Ordinal);
        Assert.DoesNotContain("objectKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bucket", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("versionId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_customer_lists_evidence_and_gets_inline_and_attachment_urls_sent_no_store_without_storage_locations()
    {
        using var api = Start(new Dictionary<string, string?> { ["Storage:S3:PresignedUrlTtlSeconds"] = "120" });
        var customerId = Guid.NewGuid();
        var customer = CatalogEndpointsTests.ClientFor(api.Factory, RoleCodes.Customer, customerId);
        var s = await SeedAsync(api, customerId, Guid.NewGuid());

        var listResponse = await customer.GetAsync($"/api/customers/me/installation-projects/{s.ProjectId}/evidence");
        var listJson = await listResponse.Content.ReadAsStringAsync();
        var item = Assert.Single((await DataOf(listResponse)).EnumerateArray());
        Assert.Equal(s.EvidenceId, item.GetProperty("evidenceId").GetGuid());
        Assert.Equal(FilePolicies.Jpeg, item.GetProperty("contentType").GetString());
        Assert.Equal(FakeStorageObjectVerifier.JpegBytes.Length, item.GetProperty("sizeBytes").GetInt64());
        Assert.Equal("mái nhà.jpg", item.GetProperty("fileName").GetString());
        Assert.Equal(s.File.VerifiedChecksumSha256, item.GetProperty("sha256").GetString());
        Assert.True(item.GetProperty("downloadable").GetBoolean());
        AssertNoStorageLocation(listJson, s.File);

        foreach (var (query, expected) in new[] { ("", "inline"), ("?disposition=inline", "inline"), ("?disposition=ATTACHMENT", "attachment") })
        {
            var before = DateTimeOffset.UtcNow;
            var response = await customer.GetAsync(CustomerUrl(s, query));
            AssertNoStore(response);
            var data = await DataOf(response);
            Assert.Equal(expected, data.GetProperty("disposition").GetString());
            Assert.Equal(FilePolicies.Jpeg, data.GetProperty("contentType").GetString());
            Assert.Equal("mái nhà.jpg", data.GetProperty("fileName").GetString());
            var url = data.GetProperty("url").GetString()!;
            Assert.StartsWith(FakeStorageUrlSigner.Host, url);
            Assert.Contains($"versionId={s.File.ObjectVersionId}", url);
            Assert.Contains("X-Amz-Expires=120", url);
            var expiresAt = data.GetProperty("expiresAt").GetDateTimeOffset();
            Assert.InRange(expiresAt, before.AddSeconds(119), DateTimeOffset.UtcNow.AddSeconds(121));
            var withoutUrl = (await response.Content.ReadAsStringAsync()).Replace(url, "", StringComparison.Ordinal);
            AssertNoStorageLocation(withoutUrl, s.File);
        }

        // The signer only ever received the stored location; logs never carry the URL, key or signature.
        Assert.All(api.Signer.Calls, c => Assert.Equal(new StorageObjectLocation(Bucket, s.File.ObjectKey, s.File.ObjectVersionId), c.Location));
        Assert.Contains(api.Logs.Messages, m => m.Contains(s.EvidenceId.ToString()));
        Assert.All(api.Logs.Messages, m =>
        {
            Assert.DoesNotContain(s.File.ObjectKey, m);
            Assert.DoesNotContain(FakeStorageUrlSigner.Host, m);
            Assert.DoesNotContain(FakeStorageUrlSigner.Signature, m);
            Assert.DoesNotContain("X-Amz-", m);
        });
    }

    [Fact]
    public async Task Only_the_owner_the_involved_technician_and_staff_get_urls_and_other_routes_are_forbidden()
    {
        using var api = Start();
        var customerId = Guid.NewGuid();
        var technicianId = Guid.NewGuid();
        var customer = CatalogEndpointsTests.ClientFor(api.Factory, RoleCodes.Customer, customerId);
        var technician = CatalogEndpointsTests.ClientFor(api.Factory, RoleCodes.Technician, technicianId);
        var s = await SeedAsync(api, customerId, technicianId);
        var otherCustomer = CatalogEndpointsTests.ClientFor(api.Factory, RoleCodes.Customer);
        var otherTechnician = CatalogEndpointsTests.ClientFor(api.Factory, RoleCodes.Technician);
        var manager = CatalogEndpointsTests.ClientFor(api.Factory, RoleCodes.Manager);
        var admin = CatalogEndpointsTests.ClientFor(api.Factory, RoleCodes.Admin);

        Assert.Equal(HttpStatusCode.OK, (await customer.GetAsync(CustomerUrl(s))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await technician.GetAsync(TechnicianUrl(s))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await technician.GetAsync($"/api/technicians/me/installation-projects/{s.ProjectId}/evidence")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync(StaffUrl(s) + "?disposition=attachment")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(StaffUrl(s))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/installation-projects/{s.ProjectId}/evidence")).StatusCode);
        var signed = api.Signer.Calls.Count;

        await AssertFailureAsync(await otherCustomer.GetAsync(CustomerUrl(s)), HttpStatusCode.NotFound, InstallationErrorCodes.ProjectNotFound);
        await AssertFailureAsync(await otherCustomer.GetAsync($"/api/customers/me/installation-projects/{s.ProjectId}/evidence"), HttpStatusCode.NotFound,
            InstallationErrorCodes.ProjectNotFound);
        await AssertFailureAsync(await otherTechnician.GetAsync(TechnicianUrl(s)), HttpStatusCode.NotFound, InstallationErrorCodes.ProjectNotFound);
        await AssertFailureAsync(await otherTechnician.GetAsync($"/api/technicians/me/installation-projects/{s.ProjectId}/evidence"), HttpStatusCode.NotFound,
            InstallationErrorCodes.ProjectNotFound);
        Assert.Equal(HttpStatusCode.Forbidden, (await technician.GetAsync(CustomerUrl(s))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync(TechnicianUrl(s))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync(StaffUrl(s))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await technician.GetAsync(StaffUrl(s))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync(CustomerUrl(s))).StatusCode);
        foreach (var path in new[] { CustomerUrl(s), TechnicianUrl(s), StaffUrl(s), $"/api/installation-projects/{s.ProjectId}/evidence" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await api.Factory.CreateClient().GetAsync(path)).StatusCode);
        Assert.Equal(signed, api.Signer.Calls.Count);
    }

    [Fact]
    public async Task Refusals_map_to_their_codes()
    {
        using var api = Start();
        var customerId = Guid.NewGuid();
        var customer = CatalogEndpointsTests.ClientFor(api.Factory, RoleCodes.Customer, customerId);
        var manager = CatalogEndpointsTests.ClientFor(api.Factory, RoleCodes.Manager);
        var s = await SeedAsync(api, customerId, Guid.NewGuid());
        var other = await SeedAsync(api, Guid.NewGuid(), Guid.NewGuid());

        var invalid = await customer.GetAsync(CustomerUrl(s, "?disposition=download"));
        await AssertFailureAsync(invalid, HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        AssertNoStore(invalid);
        await AssertFailureAsync(await customer.GetAsync($"/api/customers/me/installation-projects/{s.ProjectId}/evidence/{Guid.NewGuid()}/url"),
            HttpStatusCode.NotFound, InstallationErrorCodes.EvidenceNotFound);
        await AssertFailureAsync(await customer.GetAsync($"/api/customers/me/installation-projects/{Guid.NewGuid()}/evidence/{s.EvidenceId}/url"),
            HttpStatusCode.NotFound, InstallationErrorCodes.ProjectNotFound);
        await AssertFailureAsync(await manager.GetAsync($"/api/installation-projects/{Guid.NewGuid()}/evidence"), HttpStatusCode.NotFound, InstallationErrorCodes.ProjectNotFound);
        // Evidence of another project, requested through a project the caller can see.
        await AssertFailureAsync(await manager.GetAsync($"/api/installation-projects/{s.ProjectId}/evidence/{other.EvidenceId}/url"),
            HttpStatusCode.NotFound, InstallationErrorCodes.EvidenceNotFound);

        api.Storage.Unavailable = true;
        var outage = await customer.GetAsync(CustomerUrl(s));
        await AssertFailureAsync(outage, HttpStatusCode.ServiceUnavailable, StorageErrorCodes.Unavailable);
        AssertNoStore(outage);
        api.Storage.Unavailable = false;

        api.Storage.DeleteVersion(Bucket, s.File.ObjectKey, s.File.ObjectVersionId!);
        await AssertFailureAsync(await customer.GetAsync(CustomerUrl(s)), HttpStatusCode.Gone, InstallationErrorCodes.EvidenceObjectMissing);
        Assert.Empty(api.Signer.Calls);
        Assert.All(api.Logs.Messages, m => Assert.DoesNotContain(s.File.ObjectKey, m));
    }

    [Fact]
    public async Task Without_s3_configured_urls_answer_storage_not_configured()
    {
        using var api = Start(replaceSigner: false);
        var customerId = Guid.NewGuid();
        var customer = CatalogEndpointsTests.ClientFor(api.Factory, RoleCodes.Customer, customerId);
        var s = await SeedAsync(api, customerId, Guid.NewGuid());

        var response = await customer.GetAsync(CustomerUrl(s));
        await AssertFailureAsync(response, HttpStatusCode.ServiceUnavailable, StorageErrorCodes.NotConfigured);
        AssertNoStore(response);
        // Metadata stays readable.
        Assert.Equal(HttpStatusCode.OK, (await customer.GetAsync($"/api/customers/me/installation-projects/{s.ProjectId}/evidence")).StatusCode);
    }

    [Fact]
    public async Task Evidence_endpoints_are_feature_gated_after_authentication()
    {
        using var factory = new AuthApiFactory();
        var customer = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Customer);
        var technician = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Technician);
        var manager = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Manager);
        var (p, e) = (Guid.NewGuid(), Guid.NewGuid());

        foreach (var (client, path) in new[]
                 {
                     (customer, $"/api/customers/me/installation-projects/{p}/evidence"), (customer, $"/api/customers/me/installation-projects/{p}/evidence/{e}/url"),
                     (technician, $"/api/technicians/me/installation-projects/{p}/evidence"), (technician, $"/api/technicians/me/installation-projects/{p}/evidence/{e}/url"),
                     (manager, $"/api/installation-projects/{p}/evidence"), (manager, $"/api/installation-projects/{p}/evidence/{e}/url"),
                 })
        {
            await AssertFailureAsync(await client.GetAsync(path), HttpStatusCode.ServiceUnavailable, "FEATURE_PENDING_MIGRATION");
            Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync(path)).StatusCode);
        }
    }

    [Theory]
    [InlineData("29")]
    [InlineData("3601")]
    public void An_out_of_range_url_lifetime_fails_startup(string ttl)
    {
        using var factory = Enabled(new FakeStorageObjectVerifier(), new FakeRagServiceClient(),
            new Dictionary<string, string?> { ["Storage:S3:PresignedUrlTtlSeconds"] = ttl });
        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        var messages = string.Join(" | ", Flatten(error).Select(x => x.Message));
        Assert.Contains("PresignedUrlTtlSeconds", messages);
    }

    private static IEnumerable<Exception> Flatten(Exception error)
    {
        for (var e = error; e is not null; e = e.InnerException) yield return e;
        if (error is AggregateException aggregate)
            foreach (var inner in aggregate.InnerExceptions.SelectMany(Flatten)) yield return inner;
    }
}
