namespace SmartSolar.Modules.Common.Email;

/// <summary>A message to deliver. Transport details stay in Infrastructure.</summary>
public sealed record EmailMessage(
    string To,
    string Subject,
    string HtmlBody,
    string? TextBody = null);

public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}
