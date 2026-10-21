using Microsoft.Extensions.Logging.Abstractions;
using SmartSolar.Infrastructure.Email;
using SmartSolar.Modules.Identity.Events;
using SmartSolar.Modules.Identity.Options;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.Email;

/// <summary>Content of the password reset email and failure propagation for retries (the consumer is in EmailConsumerTests).</summary>
public class PasswordResetEmailServiceTests
{
    private const string Url = "https://app.test/reset-password?token=RAW-RESET-TOKEN";

    private static PasswordResetRequestedEvent Event(string fullName = "Test Person")
        => new(Guid.NewGuid(), "person@example.com", fullName, Url);

    private static (PasswordResetEmailService Service, FakeEmailSender Sender) Create(int lifetimeMinutes = 60)
    {
        var sender = new FakeEmailSender();
        return (
            new PasswordResetEmailService(
                sender,
                new PasswordResetOptions { LifetimeMinutes = lifetimeMinutes },
                NullLogger<PasswordResetEmailService>.Instance),
            sender);
    }

    [Fact]
    public async Task Sends_to_the_address_with_the_reset_subject_and_link()
    {
        var (service, sender) = Create();

        await service.SendPasswordResetEmailAsync(Event(), CancellationToken.None);

        var message = Assert.Single(sender.Sent);
        Assert.Equal("person@example.com", message.To);
        Assert.Equal("Reset your Smart Solar password", message.Subject);
        Assert.Contains(Url, message.HtmlBody);
        Assert.Contains(Url, message.TextBody!);
    }

    [Fact]
    public async Task Html_encodes_the_full_name()
    {
        var (service, sender) = Create();

        await service.SendPasswordResetEmailAsync(
            Event(fullName: "<script>alert('x')</script>"), CancellationToken.None);

        Assert.DoesNotContain("<script>", sender.Sent[0].HtmlBody);
        Assert.Contains("&lt;script&gt;", sender.Sent[0].HtmlBody);
    }

    [Theory]
    [InlineData(60, "1 hour")]
    [InlineData(30, "30 minutes")]
    public async Task States_the_configured_expiry(int lifetimeMinutes, string expected)
    {
        var (service, sender) = Create(lifetimeMinutes);

        await service.SendPasswordResetEmailAsync(Event(), CancellationToken.None);

        Assert.Contains(expected, sender.Sent[0].HtmlBody);
    }

    [Fact]
    public async Task Propagates_sender_failures_so_the_message_can_be_retried()
    {
        var (service, sender) = Create();
        sender.ThrowOnSend = new InvalidOperationException("smtp down");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SendPasswordResetEmailAsync(Event(), CancellationToken.None));
    }
}
