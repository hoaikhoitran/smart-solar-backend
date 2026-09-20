using SmartSolar.Infrastructure.Email;

namespace SmartSolar.Tests.Unit.Email;

public class EmailDeliveryReadinessTests
{
    [Fact]
    public void Delivery_is_operational_only_when_messaging_and_email_are_both_enabled()
    {
        var state = EmailDeliveryReadiness.Describe(messagingEnabled: true, emailEnabled: true);

        Assert.True(state.CanDeliver);
        Assert.Null(state.Warning);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void Any_disabled_side_cannot_deliver_and_warns(bool messagingEnabled, bool emailEnabled)
    {
        var state = EmailDeliveryReadiness.Describe(messagingEnabled, emailEnabled);

        Assert.False(state.CanDeliver);
        Assert.False(string.IsNullOrWhiteSpace(state.Warning));
    }

    [Fact]
    public void Warning_names_the_side_that_is_switched_off()
    {
        Assert.Contains(
            "Email:Enabled",
            EmailDeliveryReadiness.Describe(messagingEnabled: true, emailEnabled: false).Warning);
        Assert.Contains(
            "RabbitMq:Enabled",
            EmailDeliveryReadiness.Describe(messagingEnabled: false, emailEnabled: true).Warning);
    }

    [Fact]
    public void Messaging_without_email_is_described_as_refused_not_as_discarded()
    {
        // The guard rejects this combination at startup; the text must not
        // suggest that consuming and discarding events is supported.
        var warning = EmailDeliveryReadiness.Describe(messagingEnabled: true, emailEnabled: false).Warning!;

        Assert.Contains("startup is refused", warning);
        Assert.DoesNotContain("discards each message", warning);
    }
}
