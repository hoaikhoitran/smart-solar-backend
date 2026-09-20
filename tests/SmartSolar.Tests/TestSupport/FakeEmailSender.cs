using SmartSolar.Modules.Common.Email;

namespace SmartSolar.Tests.TestSupport;

public sealed class FakeEmailSender : IEmailSender
{
    private readonly List<EmailMessage> _sent = new();

    public IReadOnlyList<EmailMessage> Sent => _sent;

    public CancellationToken LastCancellationToken { get; private set; }

    /// <summary>When set, sending throws to simulate an SMTP failure.</summary>
    public Exception? ThrowOnSend { get; set; }

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        LastCancellationToken = cancellationToken;

        if (ThrowOnSend is not null)
        {
            return Task.FromException(ThrowOnSend);
        }

        _sent.Add(message);
        return Task.CompletedTask;
    }
}
