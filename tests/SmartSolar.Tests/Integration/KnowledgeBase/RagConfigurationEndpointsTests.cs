using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.KnowledgeBase;
using SmartSolar.Tests.Integration.Catalog;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.FeatureEndpointTestSupport;

namespace SmartSolar.Tests.Integration.KnowledgeBase;

/// <summary>Admin RAG configuration through the real pipeline: RBAC, feature gate, envelope, validation, revisions, activation and its effect on chat.</summary>
public sealed class RagConfigurationEndpointsTests
{
    private const string Route = "/api/admin/rag-configuration";
    private const string Secret = "do-not-leak-this-shared-secret-0123456789";
    private const string ServiceUrl = "http://rag-internal.example:8123";

    private static AuthApiFactory Factory(FakeRagServiceClient rag, FakeStorageObjectVerifier? storage = null) => Enabled(storage ?? new FakeStorageObjectVerifier(), rag,
        new Dictionary<string, string?>
        {
            ["Rag:ServiceSharedSecret"] = Secret,
            ["Rag:ServiceBaseUrl"] = ServiceUrl,
            // A reasoning-style model added to the shipped three.
            ["Rag:GenerationModels:3:Id"] = "o4-mini",
            ["Rag:GenerationModels:3:DisplayName"] = "o4 mini",
            ["Rag:GenerationModels:3:SupportsTemperature"] = "false",
            ["Rag:GenerationModels:3:TokenParameter"] = "max_completion_tokens",
            ["Rag:GenerationModels:3:MaxOutputTokensLimit"] = "1000",
            ["Rag:ConfigurationCacheSeconds"] = "15",
        });

    private static async Task<JsonElement> ErrorDetails(HttpResponseMessage r, HttpStatusCode status, string code)
    {
        var body = await BodyOf(r);
        Assert.True(status == r.StatusCode, $"Expected {status} but got {(int)r.StatusCode}: {body}");
        Assert.Equal(code, body.GetProperty("error").GetProperty("code").GetString());
        return body.GetProperty("error").GetProperty("details");
    }

    [Fact]
    public async Task Configuration_endpoints_are_admin_only_and_feature_gated()
    {
        using var factory = Factory(new FakeRagServiceClient());
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync(Route)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PutAsJsonAsync($"{Route}/draft", new { topK = 3 })).StatusCode);
        foreach (var role in new[] { RoleCodes.Customer, RoleCodes.Sales, RoleCodes.Manager, RoleCodes.Technician })
        {
            var client = CatalogEndpointsTests.ClientFor(factory, role);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Route)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{Route}/models")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"{Route}/draft", new { topK = 3 })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"{Route}/draft/activate", new { expectedRevision = 1 })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"{Route}/rollback", new { version = 1 })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"{Route}/reindex-stale", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync($"{Route}/draft")).StatusCode);
        }

        using var off = new AuthApiFactory();
        await AssertFailureAsync(await CatalogEndpointsTests.ClientFor(off, RoleCodes.Admin).GetAsync(Route), HttpStatusCode.ServiceUnavailable, "FEATURE_PENDING_MIGRATION");
    }

    [Fact]
    public async Task Admin_reads_edits_validates_and_activates_the_configuration()
    {
        var rag = new FakeRagServiceClient();
        using var factory = Factory(rag);
        var admin = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Admin);

        var initial = await DataOf(await admin.GetAsync(Route));
        Assert.True(initial.GetProperty("bootstrap").GetBoolean());
        Assert.Equal(0, initial.GetProperty("effective").GetProperty("version").GetInt32());
        Assert.Equal(JsonValueKind.Null, initial.GetProperty("active").ValueKind);

        var catalog = await DataOf(await admin.GetAsync($"{Route}/models"));
        Assert.Equal(["gpt-4.1-mini", "gpt-4.1", "gpt-4o-mini", "o4-mini"], catalog.GetProperty("models").EnumerateArray().Select(m => m.GetProperty("id").GetString()));
        Assert.Equal("max_completion_tokens", catalog.GetProperty("models")[3].GetProperty("tokenParameter").GetString());
        Assert.Equal(8000, catalog.GetProperty("limits").GetProperty("chunkCharactersMax").GetInt32());
        Assert.Equal("v1", catalog.GetProperty("promptVersions")[0].GetString());

        // Validation: unknown model, out-of-range chunk, overlap not below chunk, temperature for a model without it.
        Assert.True((await ErrorDetails(await admin.PutAsJsonAsync($"{Route}/draft", new { generationModel = "gpt-9" }), HttpStatusCode.BadRequest,
            RagConfigurationErrorCodes.ValidationFailed)).TryGetProperty("generationModel", out _));
        Assert.True((await ErrorDetails(await admin.PutAsJsonAsync($"{Route}/draft", new { chunkCharacters = 100 }), HttpStatusCode.BadRequest,
            RagConfigurationErrorCodes.ValidationFailed)).TryGetProperty("chunkCharacters", out _));
        Assert.True((await ErrorDetails(await admin.PutAsJsonAsync($"{Route}/draft", new { chunkCharacters = 800, chunkOverlap = 800 }), HttpStatusCode.BadRequest,
            RagConfigurationErrorCodes.ValidationFailed)).TryGetProperty("chunkOverlap", out _));
        Assert.True((await ErrorDetails(await admin.PutAsJsonAsync($"{Route}/draft", new { generationModel = "o4-mini", temperature = 0.3, maxOutputTokens = 800 }),
            HttpStatusCode.BadRequest, RagConfigurationErrorCodes.ValidationFailed)).TryGetProperty("temperature", out _));
        await AssertFailureAsync(await admin.PutAsJsonAsync($"{Route}/draft", new { temperature = "warm" }), HttpStatusCode.BadRequest, "VALIDATION_FAILED");

        var draft = await DataOf(await admin.PutAsJsonAsync($"{Route}/draft", new
        {
            generationModel = "o4-mini", temperature = (decimal?)null, maxOutputTokens = 800, chunkCharacters = 1200, chunkOverlap = 150, topK = 4, minScore = 0.35,
            maxContextTokens = 2500, maxHistoryMessages = 2, promptVersion = "v1", notes = "Reasoning model trial",
        }));
        Assert.Equal("DRAFT", draft.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, draft.GetProperty("temperature").ValueKind);
        var revision = draft.GetProperty("revision").GetInt32();

        // Revision conflicts.
        await AssertFailureAsync(await admin.PutAsJsonAsync($"{Route}/draft", new { topK = 5 }), HttpStatusCode.Conflict, RagConfigurationErrorCodes.StaleRevision);
        await AssertFailureAsync(await admin.PutAsJsonAsync($"{Route}/draft", new { topK = 5, expectedRevision = revision + 3 }), HttpStatusCode.Conflict,
            RagConfigurationErrorCodes.StaleRevision);
        await AssertFailureAsync(await admin.PostAsJsonAsync($"{Route}/draft/activate", new { expectedRevision = revision + 3 }), HttpStatusCode.Conflict,
            RagConfigurationErrorCodes.StaleRevision);

        var validation = await DataOf(await admin.PostAsync($"{Route}/draft/validate", null));
        Assert.True(validation.GetProperty("valid").GetBoolean());
        Assert.True(validation.GetProperty("chunkSettingsChanged").GetBoolean());

        var activated = await DataOf(await admin.PostAsJsonAsync($"{Route}/draft/activate", new { expectedRevision = revision }));
        Assert.Equal("ACTIVE", activated.GetProperty("active").GetProperty("status").GetString());
        Assert.Equal(0, activated.GetProperty("versionsNeedingReindex").GetInt32());
        await AssertFailureAsync(await admin.PostAsJsonAsync($"{Route}/draft/activate", new { expectedRevision = revision }), HttpStatusCode.NotFound,
            RagConfigurationErrorCodes.DraftNotFound);

        var state = await DataOf(await admin.GetAsync(Route));
        Assert.False(state.GetProperty("bootstrap").GetBoolean());
        Assert.Equal(1, state.GetProperty("active").GetProperty("version").GetInt32());
        Assert.Equal(JsonValueKind.Null, state.GetProperty("draft").ValueKind);

        // Persisted: a new DI scope reads the same effective configuration.
        using (var scope = factory.Services.CreateScope())
        {
            var effective = await scope.ServiceProvider.GetRequiredService<IRagConfigurationProvider>().GetAsync(CancellationToken.None);
            Assert.Equal((1, "o4-mini", 1200), (effective.Version, effective.Values.GenerationModel, effective.Values.ChunkCharacters));
        }

        // Second version, then rollback to the first creates version 3.
        var second = await DataOf(await admin.PutAsJsonAsync($"{Route}/draft", new { generationModel = "gpt-4.1", temperature = 0.2, maxOutputTokens = 700 }));
        Assert.Equal(1, second.GetProperty("basedOnVersion").GetInt32());
        await DataOf(await admin.PostAsJsonAsync($"{Route}/draft/activate", new { expectedRevision = second.GetProperty("revision").GetInt32() }));
        var rollback = await DataOf(await admin.PostAsJsonAsync($"{Route}/rollback", new { version = 1 }));
        Assert.Equal(3, rollback.GetProperty("active").GetProperty("version").GetInt32());
        Assert.Equal("o4-mini", rollback.GetProperty("active").GetProperty("generationModel").GetString());
        Assert.Equal(2, rollback.GetProperty("supersededVersion").GetInt32());
        await AssertFailureAsync(await admin.PostAsJsonAsync($"{Route}/rollback", new { version = 3 }), HttpStatusCode.Conflict, RagConfigurationErrorCodes.InvalidState);
        await AssertFailureAsync(await admin.PostAsJsonAsync($"{Route}/rollback", new { version = 99 }), HttpStatusCode.NotFound, RagConfigurationErrorCodes.VersionNotFound);

        var history = await DataOf(await admin.GetAsync($"{Route}/versions?page=1&pageSize=2"));
        Assert.Equal(3, history.GetProperty("total").GetInt32());
        Assert.Equal([3, 2], history.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("version").GetInt32()));

        // Draft deletion.
        var third = await DataOf(await admin.PutAsJsonAsync($"{Route}/draft", new { topK = 2 }));
        await AssertFailureAsync(await admin.DeleteAsync($"{Route}/draft?expectedRevision={third.GetProperty("revision").GetInt32() + 1}"), HttpStatusCode.Conflict,
            RagConfigurationErrorCodes.StaleRevision);
        Assert.True((await DataOf(await admin.DeleteAsync($"{Route}/draft?expectedRevision={third.GetProperty("revision").GetInt32()}"))).GetBoolean());
        await AssertFailureAsync(await admin.DeleteAsync($"{Route}/draft"), HttpStatusCode.NotFound, RagConfigurationErrorCodes.DraftNotFound);

        var reindex = await DataOf(await admin.PostAsync($"{Route}/reindex-stale", null), HttpStatusCode.Accepted);
        Assert.Equal(3, reindex.GetProperty("configVersion").GetInt32());
        Assert.Equal(0, reindex.GetProperty("queued").GetInt32());
    }

    [Fact]
    public async Task Settings_responses_never_contain_secrets_or_service_urls()
    {
        using var factory = Factory(new FakeRagServiceClient());
        var admin = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Admin);
        var draft = await DataOf(await admin.PutAsJsonAsync($"{Route}/draft", new { topK = 3 }));
        await DataOf(await admin.PostAsJsonAsync($"{Route}/draft/activate", new { expectedRevision = draft.GetProperty("revision").GetInt32() }));
        await admin.PutAsJsonAsync($"{Route}/draft", new { topK = 4 });

        foreach (var path in new[] { Route, $"{Route}/models", $"{Route}/versions" })
        {
            var text = await (await admin.GetAsync(path)).Content.ReadAsStringAsync();
            Assert.DoesNotContain(Secret, text);
            Assert.DoesNotContain("rag-internal.example", text);
            Assert.DoesNotContain("secret", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("serviceBaseUrl", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("apiKey", text, StringComparison.OrdinalIgnoreCase);
        }
        // Unknown fields (e.g. a secret) in the request are not stored anywhere either.
        await admin.PutAsJsonAsync($"{Route}/draft", new { serviceSharedSecret = "attacker-value", expectedRevision = 1 });
        Assert.DoesNotContain("attacker-value", await (await admin.GetAsync(Route)).Content.ReadAsStringAsync());
    }
}
