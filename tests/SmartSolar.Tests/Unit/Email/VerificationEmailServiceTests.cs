using Microsoft.Extensions.Logging.Abstractions;
using SmartSolar.Infrastructure.Email;
using SmartSolar.Modules.Identity.Events;
using SmartSolar.Modules.Identity.Options;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.Email;

/// <summary>Content of the account verification email (HTML and plain text) and failure propagation for retries.</summary>
public class VerificationEmailServiceTests
{
    private const string Url = "https://app.test/verify-email?token=RAW-TOKEN-VALUE";

    private static EmailVerificationRequestedEvent Event(string fullName = "Nguyen An") => new(Guid.NewGuid(), "someone@example.com", fullName, Url);

    private static (VerificationEmailService Service, FakeEmailSender Sender) Create(int lifetimeMinutes = 1440)
    {
        var sender = new FakeEmailSender();
        return (new VerificationEmailService(sender, new EmailVerificationOptions { LifetimeMinutes = lifetimeMinutes }, NullLogger<VerificationEmailService>.Instance), sender);
    }

    [Fact]
    public async Task The_message_greets_the_user_and_carries_the_link_in_html_and_plain_text()
    {
        var (service, sender) = Create();

        await service.SendVerificationEmailAsync(Event(), CancellationToken.None);

        var message = Assert.Single(sender.Sent);
        Assert.Equal("someone@example.com", message.To);
        Assert.Equal("Verify your Smart Solar account", message.Subject);
        Assert.Contains("Nguyen An", message.HtmlBody);
        Assert.Contains(Url, message.HtmlBody);
        Assert.Contains("ignore", message.HtmlBody, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Url, message.TextBody!);
        Assert.DoesNotContain("<html>", message.TextBody!);
    }

    [Theory]
    [InlineData(1440, "24 hours")]
    [InlineData(2880, "48 hours")]
    [InlineData(60, "1 hour")]
    [InlineData(120, "2 hours")]
    [InlineData(30, "30 minutes")]
    public async Task The_body_states_the_configured_lifetime(int lifetimeMinutes, string expected)
    {
        var (service, sender) = Create(lifetimeMinutes);

        await service.SendVerificationEmailAsync(Event(), CancellationToken.None);

        Assert.Contains(expected, sender.Sent[0].HtmlBody);
        if (lifetimeMinutes != 1440) Assert.DoesNotContain("24 hours", sender.Sent[0].HtmlBody);
    }

    [Fact]
    public async Task The_full_name_is_html_encoded()
    {
        var (service, sender) = Create();

        await service.SendVerificationEmailAsync(Event("<script>alert('x')</script>"), CancellationToken.None);

        Assert.DoesNotContain("<script>", sender.Sent[0].HtmlBody);
        Assert.Contains("&lt;script&gt;", sender.Sent[0].HtmlBody);
    }

    [Fact]
    public async Task Sender_failures_propagate_so_the_message_can_be_retried()
    {
        var (service, sender) = Create();
        sender.ThrowOnSend = new InvalidOperationException("smtp down");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SendVerificationEmailAsync(Event(), CancellationToken.None));
    }
}
