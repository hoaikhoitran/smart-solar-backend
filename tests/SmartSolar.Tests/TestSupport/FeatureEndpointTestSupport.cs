using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.KnowledgeBase;

namespace SmartSolar.Tests.TestSupport;

/// <summary>API factory and envelope helpers for the Installation Flow and knowledge-base endpoint tests. Storage and RAG are synthetic.</summary>
public static class FeatureEndpointTestSupport
{
    public const string Bucket = "smartsolar-test";

    /// <summary>Both features on by default; <paramref name="extra"/> overrides any key.</summary>
    public static AuthApiFactory Enabled(FakeStorageObjectVerifier storage, FakeRagServiceClient rag, Dictionary<string, string?>? extra = null,
        Action<IServiceCollection>? services = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Features:InstallationWorkflowEnabled"] = "true",
            ["Features:KnowledgeBaseEnabled"] = "true",
            ["Storage:DefaultBucket"] = Bucket,
            ["Rag:Enabled"] = "false",
        };
        foreach (var (k, v) in extra ?? []) settings[k] = v;
        return new AuthApiFactory(settings)
        {
            ConfigureTestServices = collection =>
            {
                collection.RemoveAll<IStorageObjectVerifier>();
                collection.AddSingleton<IStorageObjectVerifier>(storage);
                collection.RemoveAll<IRagServiceClient>();
                collection.AddSingleton<IRagServiceClient>(rag);
                services?.Invoke(collection);
            },
        };
    }

    public static async Task<JsonElement> BodyOf(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    public static async Task<JsonElement> DataOf(HttpResponseMessage r, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var body = await BodyOf(r);
        Assert.True(expected == r.StatusCode, $"Expected {expected} but got {(int)r.StatusCode}: {body}");
        return body.GetProperty("data");
    }

    public static async Task AssertFailureAsync(HttpResponseMessage r, HttpStatusCode status, string code)
    {
        var body = await BodyOf(r);
        Assert.True(status == r.StatusCode, $"Expected {status} but got {(int)r.StatusCode}: {body}");
        Assert.Equal(code, body.GetProperty("error").GetProperty("code").GetString());
    }

    public static async Task<Guid> UploadAsync(HttpClient client, FakeStorageObjectVerifier storage, Guid owner, string purpose = FilePurposes.KnowledgeDocument)
    {
        var key = $"{FilePolicies.ByPurpose[purpose].KeyPrefix}{owner}/{Guid.NewGuid():N}.pdf";
        storage.Put(Bucket, key, FakeStorageObjectVerifier.PdfBytes);
        var data = await DataOf(await client.PostAsJsonAsync("/api/files", new
        {
            purpose, objectKey = key, fileName = "guide.pdf", contentType = FilePolicies.Pdf, sizeBytes = FakeStorageObjectVerifier.PdfBytes.Length,
        }), HttpStatusCode.Created);
        Assert.Equal(FileStatuses.Verified, data.GetProperty("status").GetString());
        return data.GetProperty("fileId").GetGuid();
    }
}
