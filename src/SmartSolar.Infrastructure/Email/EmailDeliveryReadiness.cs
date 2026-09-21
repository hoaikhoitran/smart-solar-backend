namespace SmartSolar.Infrastructure.Email;

/// <summary>Whether the end-to-end delivery path is actually switched on.</summary>
public sealed record EmailDeliveryState(bool CanDeliver, string? Warning);

/// <summary>
/// Real delivery needs the broker and the mailer together. When only one side
/// is enabled the application still runs, but it says so instead of looking
/// like it is delivering email.
/// </summary>
public static class EmailDeliveryReadiness
{
    public static EmailDeliveryState Describe(bool messagingEnabled, bool emailEnabled)
    {
        if (messagingEnabled && emailEnabled)
        {
            return new EmailDeliveryState(true, null);
        }

        if (messagingEnabled)
        {
            // Unreachable in-process: AddInfrastructure refuses this combination
            // before the host is built. Kept so the matrix stays complete.
            return new EmailDeliveryState(
                false,
                "Messaging is on while 'Email:Enabled' is false. This is not a supported configuration "
                + "and startup is refused, because queued verification events would be discarded.");
        }

        if (emailEnabled)
        {
            return new EmailDeliveryState(
                false,
                "Verification emails will NOT be delivered: SMTP is configured but 'RabbitMq:Enabled' is false, "
                + "so verification events are dropped before reaching the consumer.");
        }

        return new EmailDeliveryState(
            false,
            "Verification emails will NOT be delivered: both 'RabbitMq:Enabled' and 'Email:Enabled' are false.");
    }
}
