using Microsoft.Extensions.Logging.Abstractions;
using SmartSolar.Infrastructure.Email;
using SmartSolar.Modules.Identity.Options;
using SmartSolar.Modules.Identity.Events;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.Email;

public class VerificationEmailServiceTests
{
    private const string Url = "https://app.test/verify-email?token=RAW-TOKEN-VALUE";

    private static EmailVerificationRequestedEvent Event(
        string email = "person@example.com",
        string fullName = "Test Person")
        => new(Guid.NewGuid(), email, fullName, Url);

    private static (VerificationEmailService Service, FakeEmailSender Sender) Create(
        int lifetimeMinutes = 1440)
    {
        var sender = new FakeEmailSender();
        var options = new EmailVerificationOptions { LifetimeMinutes = lifetimeMinutes };
        return (
            new VerificationEmailService(sender, options, NullLogger<VerificationEmailService>.Instance),
            sender);
    }

    [Fact]
    public async Task Sends_to_the_address_from_the_event()
    {
        var (service, sender) = Create();

        await service.SendVerificationEmailAsync(Event(email: "someone@example.com"), CancellationToken.None);

        var message = Assert.Single(sender.Sent);
        Assert.Equal("someone@example.com", message.To);
    }

    [Fact]
    public async Task Uses_the_agreed_subject()
    {
        var (service, sender) = Create();

        await service.SendVerificationEmailAsync(Event(), CancellationToken.None);

        Assert.Equal("Verify your Smart Solar account", sender.Sent[0].Subject);
    }

    [Fact]
    public async Task Body_contains_the_verification_link()
    {
        var (service, sender) = Create();

        await service.SendVerificationEmailAsync(Event(), CancellationToken.None);

        Assert.Contains(Url, sender.Sent[0].HtmlBody);
    }

    [Fact]
    public async Task Body_states_the_configured_expiry_and_the_ignore_notice()
    {
        var (service, sender) = Create(lifetimeMinutes: 1440);

        await service.SendVerificationEmailAsync(Event(), CancellationToken.None);

        var body = sender.Sent[0].HtmlBody;
        Assert.Contains("24 hours", body);
        Assert.Contains("ignore", body, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(60, "1 hour")]
    [InlineData(120, "2 hours")]
    [InlineData(30, "30 minutes")]
    [InlineData(2880, "48 hours")]
    public async Task Body_follows_the_configured_lifetime(int lifetimeMinutes, string expected)
    {
        var (service, sender) = Create(lifetimeMinutes);

        await service.SendVerificationEmailAsync(Event(), CancellationToken.None);

        var body = sender.Sent[0].HtmlBody;
        Assert.Contains(expected, body);
        Assert.DoesNotContain("24 hours", body);
    }

    [Fact]
    public async Task Sends_a_plain_text_alternative_containing_the_link()
    {
        var (service, sender) = Create();

        await service.SendVerificationEmailAsync(Event(fullName: "A <b>Name</b>"), CancellationToken.None);

        var text = sender.Sent[0].TextBody;
        Assert.NotNull(text);
        Assert.Contains(Url, text);
        Assert.DoesNotContain("<html>", text);
    }

    [Fact]
    public async Task Greets_the_user_by_name()
    {
        var (service, sender) = Create();

        await service.SendVerificationEmailAsync(Event(fullName: "Nguyen An"), CancellationToken.None);

        Assert.Contains("Nguyen An", sender.Sent[0].HtmlBody);
    }

    [Fact]
    public async Task Html_encodes_the_full_name()
    {
        var (service, sender) = Create();

        await service.SendVerificationEmailAsync(
            Event(fullName: "<script>alert('x')</script>"), CancellationToken.None);

        var body = sender.Sent[0].HtmlBody;
        Assert.DoesNotContain("<script>", body);
        Assert.Contains("&lt;script&gt;", body);
    }

    [Fact]
    public async Task Propagates_sender_failures_so_the_message_can_be_retried()
    {
        var (service, sender) = Create();
        sender.ThrowOnSend = new InvalidOperationException("smtp down");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SendVerificationEmailAsync(Event(), CancellationToken.None));
    }

    [Fact]
    public async Task Passes_the_cancellation_token_to_the_sender()
    {
        var (service, sender) = Create();
        using var cts = new CancellationTokenSource();

        await service.SendVerificationEmailAsync(Event(), cts.Token);

        Assert.Equal(cts.Token, sender.LastCancellationToken);
    }
}
