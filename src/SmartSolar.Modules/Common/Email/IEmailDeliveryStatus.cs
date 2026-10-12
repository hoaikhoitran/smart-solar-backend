namespace SmartSolar.Modules.Common.Email;

/// <summary>Whether queued emails can actually reach a mailbox in this environment.</summary>
public interface IEmailDeliveryStatus
{
    bool CanDeliver { get; }
}
