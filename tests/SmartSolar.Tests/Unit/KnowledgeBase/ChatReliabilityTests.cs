using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SmartSolar.Api.Controllers;
using SmartSolar.Infrastructure.ExternalProviders.Rag;
using SmartSolar.Infrastructure.Persistence;
using SmartSolar.Infrastructure.Persistence.KnowledgeBase;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.KnowledgeBase;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.KnowledgeBase;

/// <summary>
/// 2026-11-17 chatbot reliability fixes: the API tells the RAG service how long it waits (so abandoned work stops), the service's 503 reasons
/// reach the client as distinct codes (timeout 504, busy 503, unavailable 503), a failed answer never leaves the quota reserved or stores a
/// message, and a retried FIRST question (no session id yet) returns the stored exchange instead of a second session and generation.
/// </summary>
public sealed class ChatReliabilityTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private sealed class Rig(InstallationWorkflowTestContext ctx)
    {
        public InstallationWorkflowTestContext Ctx { get; } = ctx;
        public FakeRagServiceClient Rag { get; } = new();
        public RagOptions Options { get; } = new() { Enabled = true, ServiceSharedSecret = new string('x', 32) };

        public KnowledgeDocumentHandlers Documents() => new(new KnowledgeUnitOfWork(Ctx.Db), Ctx.Files(), Options, Ctx.Clock, NullLogger<KnowledgeDocumentHandlers>.Instance);
        public KnowledgeIngestionService Ingestion() => new(new KnowledgeUnitOfWork(Ctx.Db), new FileStore(Ctx.Db), Rag, Options, Ctx.Clock, NullLogger<KnowledgeIngestionService>.Instance);
        public ChatService Chat() => new(new KnowledgeUnitOfWork(Ctx.Db), Rag, Options, Ctx.Clock, NullLogger<ChatService>.Instance);

        public async Task<Guid> UserAsync() => (await Ctx.Scheduling.SeedTechnicianAsync($"User {Guid.NewGuid():N}", null, withRole: false)).Id;

        public async Task PublishAsync(Guid admin)
        {
            var file = await Ctx.VerifiedFileAsync(admin, FilePurposes.KnowledgeDocument, "pdf");
            var registered = await Documents().RegisterAsync(admin, file, "Chính sách bảo hành", "warranty", ["pin"], None);
            Assert.True(registered.Succeeded, registered.Message);
            Assert.Equal(1, await Ingestion().RunDueAsync(10, None));
            var ready = (await Documents().GetAsync(registered.Value!.DocumentId, None)).Value!;
            Assert.True((await Documents().PublishAsync(ready.DocumentId, ready.Versions![0].VersionId, ready.Revision, None)).Succeeded);
        }
    }

    [Theory]
    [InlineData("timeout", KnowledgeErrorCodes.Timeout, 504)]
    [InlineData("busy", KnowledgeErrorCodes.Busy, 503)]
    [InlineData("RAG service unavailable", KnowledgeErrorCodes.ServiceUnavailable, 503)]
    public async Task Each_rag_failure_has_its_own_code_and_releases_the_quota_without_storing_messages(string error, string code, int status)
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var rig = new Rig(ctx);
        var user = await rig.UserAsync();
        await rig.PublishAsync(user);
        rig.Rag.Answer = _ => new RagAnswerResult(false, error, null, false, [], 0, 0, null, 0, 0);

        // More failures than the per-user concurrency cap (2): none is refused as "too many concurrent", so nothing stays reserved.
        for (var i = 0; i < 3; i++)
        {
            var failed = await rig.Chat().AskAsync(user, null, "Tấm pin bảo hành bao lâu?", null, None, KnowledgeAudience.Staff);
            Assert.Equal(code, failed.ErrorCode);
        }
        Assert.Equal(status, OperationResultControllerBase.StatusFor(code));
        Assert.Equal(0, await ctx.Db.RagUsageReservations.CountAsync());
        Assert.Equal(0, await ctx.Db.ChatMessages.CountAsync());

        // The service recovers: the next question is answered normally.
        rig.Rag.Answer = null;
        Assert.True((await rig.Chat().AskAsync(user, null, "Tấm pin bảo hành bao lâu?", null, None, KnowledgeAudience.Staff)).Succeeded);
    }

    [Fact]
    public async Task A_retried_first_question_returns_the_stored_exchange_instead_of_a_second_session_and_generation()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var rig = new Rig(ctx);
        var user = await rig.UserAsync();
        var other = await rig.UserAsync();
        await rig.PublishAsync(user);

        var first = (await rig.Chat().AskAsync(user, null, "Tấm pin bảo hành bao lâu?", "first-try", None, KnowledgeAudience.Staff)).Value!;
        // The client lost the response (connection cut) and sends the same question again without a session id.
        var retried = (await rig.Chat().AskAsync(user, null, "Tấm pin bảo hành bao lâu?", "first-try", None, KnowledgeAudience.Staff)).Value!;

        Assert.Equal(first.SessionId, retried.SessionId);
        Assert.Equal(first.Answer.MessageId, retried.Answer.MessageId);
        Assert.Equal(1, rig.Rag.AnswerCalls);
        Assert.Equal(1, await ctx.Db.ChatSessions.CountAsync());
        Assert.Equal(2, await ctx.Db.ChatMessages.CountAsync());

        // The same client id from ANOTHER user is a different question (ids are only unique per user).
        var foreign = (await rig.Chat().AskAsync(other, null, "Tấm pin bảo hành bao lâu?", "first-try", None, KnowledgeAudience.Staff)).Value!;
        Assert.NotEqual(first.SessionId, foreign.SessionId);
        Assert.Equal(2, rig.Rag.AnswerCalls);
    }

    [Fact]
    public async Task Only_the_last_history_messages_are_read_and_in_order()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var rig = new Rig(ctx);
        var user = await rig.UserAsync();
        await rig.PublishAsync(user);
        var session = (await rig.Chat().AskAsync(user, null, "Câu 1", "q1", None, KnowledgeAudience.Staff)).Value!.SessionId;
        for (var i = 2; i <= 6; i++)
        {
            ctx.Clock.Now += TimeSpan.FromSeconds(1);
            Assert.True((await rig.Chat().AskAsync(user, session, $"Câu {i}", $"q{i}", None, KnowledgeAudience.Staff)).Succeeded);
        }

        var last = await new KnowledgeUnitOfWork(ctx.Db).GetMessagesAsync(session, 4, None);
        Assert.Equal(["Câu 5", "assistant", "Câu 6", "assistant"], last.Select(m => m.Role == ChatRoles.Assistant ? "assistant" : m.Content));
        Assert.Equal(12, (await new KnowledgeUnitOfWork(ctx.Db).GetMessagesAsync(session, null, None)).Count);
    }

    // ---------------------------------------------------------------- HTTP client to the RAG service

    private sealed class CapturingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? LastBody;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static (RagServiceClient Client, CapturingHandler Handler) Client(HttpStatusCode status, string body, int timeoutSeconds = 60)
    {
        var handler = new CapturingHandler(status, body);
        var options = new RagOptions { Enabled = true, ServiceSharedSecret = new string('s', 32), ServiceBaseUrl = "http://rag.test", RequestTimeoutSeconds = timeoutSeconds };
        return (new RagServiceClient(new HttpClient(handler) { BaseAddress = new Uri("http://rag.test/") }, options, TimeProvider.System,
            NullLogger<RagServiceClient>.Instance), handler);
    }

    private static RagAnswerRequest Request() => new("Câu hỏi?", [], [new RagAllowedVersion(Guid.NewGuid(), Guid.NewGuid(), "T", 1)], 6, 3000, 700);

    [Theory]
    [InlineData(60, 59)]
    [InlineData(30, 29)]
    [InlineData(500, 119)]   // clamped like the API's own wait (5–120 s)
    public async Task The_answer_request_tells_the_service_how_long_the_api_waits(int timeoutSeconds, int expectedDeadline)
    {
        var (client, handler) = Client(HttpStatusCode.OK, "{\"answer\":\"x\",\"grounded\":false,\"citations\":[],\"usage\":{\"promptTokens\":1,\"completionTokens\":1}}",
            timeoutSeconds);

        Assert.True((await client.AnswerAsync(Request(), None)).Reached);

        using var sent = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal(expectedDeadline, sent.RootElement.GetProperty("deadlineSeconds").GetInt32());
    }

    [Theory]
    [InlineData(503, "{\"detail\":\"busy\"}", "busy")]
    [InlineData(503, "{\"detail\":\"deadline exceeded\"}", "timeout")]
    [InlineData(503, "{\"detail\":\"provider unavailable\"}", "RAG service returned HTTP 503")]
    [InlineData(503, "not json", "RAG service returned HTTP 503")]
    [InlineData(500, "{\"detail\":\"busy\"}", "RAG service returned HTTP 500")]
    public async Task The_service_reason_is_kept_for_503_only(int status, string body, string expected)
    {
        var (client, _) = Client((HttpStatusCode)status, body);

        var result = await client.AnswerAsync(Request(), None);

        Assert.False(result.Reached);
        Assert.Equal(expected, result.Error);
    }
}
