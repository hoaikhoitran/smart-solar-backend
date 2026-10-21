using SmartSolar.Infrastructure.Email;

namespace SmartSolar.Tests.Unit.Email;

/// <summary>The startup description of whether account emails can be delivered (RabbitMQ and SMTP both needed).</summary>
public class EmailDeliveryReadinessTests
{
    [Theory]
    [InlineData(true, true, null)]
    [InlineData(true, false, "Email:Enabled")]
    [InlineData(false, true, "RabbitMq:Enabled")]
    [InlineData(false, false, "RabbitMq:Enabled")]
    public void Delivery_works_only_with_both_sides_enabled_and_the_warning_names_the_missing_side(bool messaging, bool email, string? namedKey)
    {
        var state = EmailDeliveryReadiness.Describe(messaging, email);

        Assert.Equal(namedKey is null, state.CanDeliver);
        if (namedKey is null) Assert.Null(state.Warning);
        else Assert.Contains(namedKey, state.Warning);
    }

    [Fact]
    public void Messaging_without_email_is_described_as_refused_not_as_discarded()
    {
        // The guard rejects this combination at startup; the text must not suggest that consuming and discarding events is supported.
        var warning = EmailDeliveryReadiness.Describe(messagingEnabled: true, emailEnabled: false).Warning!;

        Assert.Contains("startup is refused", warning);
        Assert.DoesNotContain("discards each message", warning);
    }
}
