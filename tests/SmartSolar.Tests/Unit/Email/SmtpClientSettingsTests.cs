using MailKit.Net.Smtp;
using SmartSolar.Infrastructure.Email;

namespace SmartSolar.Tests.Unit.Email;

/// <summary>
/// Drives the real MailKit client object (never connected) so the settings
/// asserted here are the ones production code applies before ConnectAsync.
/// </summary>
public class SmtpClientSettingsTests
{
    private static SmtpOptions Options(bool? checkRevocation = null)
    {
        var options = new SmtpOptions
        {
            Host = "smtp.example.com",
            Port = 587,
            UseSsl = true,
            FromAddress = "no-reply@example.com"
        };

        if (checkRevocation is not null)
        {
            options.CheckCertificateRevocation = checkRevocation.Value;
        }

        return options;
    }

    [Fact]
    public void Revocation_checking_is_on_by_default()
    {
        using var client = new SmtpClient();
        // Poisoned first: MailKit's own default is true, so without this the
        // assertion would pass even if Apply did nothing.
        client.CheckCertificateRevocation = false;

        SmtpClientSettings.Apply(client, Options());

        Assert.True(client.CheckCertificateRevocation);
    }

    [Fact]
    public void Configured_true_enables_revocation_checking()
    {
        using var client = new SmtpClient();
        client.CheckCertificateRevocation = false;

        SmtpClientSettings.Apply(client, Options(checkRevocation: true));

        Assert.True(client.CheckCertificateRevocation);
    }

    [Fact]
    public void Configured_false_disables_revocation_checking()
    {
        using var client = new SmtpClient();

        SmtpClientSettings.Apply(client, Options(checkRevocation: false));

        Assert.False(client.CheckCertificateRevocation);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_client_production_uses_carries_the_configured_setting(bool checkRevocation)
    {
        // CreateClient is the only way the sender obtains a client, so this
        // binds the production call site rather than just Apply's body.
        using var client = SmtpClientSettings.CreateClient(Options(checkRevocation));

        Assert.Equal(checkRevocation, client.CheckCertificateRevocation);
        Assert.Null(client.ServerCertificateValidationCallback);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void No_certificate_validation_callback_is_ever_installed(bool checkRevocation)
    {
        // Turning off revocation must not turn off chain or hostname validation:
        // a callback here would accept certificates MailKit would otherwise reject.
        using var client = new SmtpClient();

        SmtpClientSettings.Apply(client, Options(checkRevocation));

        Assert.Null(client.ServerCertificateValidationCallback);
    }

    [Fact]
    public void Disabling_revocation_changes_no_other_client_setting()
    {
        using var untouched = new SmtpClient();
        using var client = new SmtpClient();

        SmtpClientSettings.Apply(client, Options(checkRevocation: false));

        // Every settable-looking value on the client (bools, enums, timeouts,
        // SslProtocols...) must match an untouched client. Reference-typed
        // collaborators such as ProtocolLogger are distinct instances by design
        // and carry no value equality, so they are compared by nullness only.
        var compared = 0;

        foreach (var property in typeof(SmtpClient).GetProperties()
                     .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
                     .Where(p => p.Name != nameof(SmtpClient.CheckCertificateRevocation)))
        {
            object? expected;
            object? actual;

            try
            {
                expected = property.GetValue(untouched);
                actual = property.GetValue(client);
            }
            catch
            {
                // Properties that require a live connection.
                continue;
            }

            if (HasValueSemantics(expected) && HasValueSemantics(actual))
            {
                Assert.Equal(expected, actual);
            }
            else
            {
                Assert.Equal(expected is null, actual is null);
            }

            compared++;
        }

        Assert.True(compared > 5, $"Expected to compare several properties, compared {compared}.");
    }

    private static bool HasValueSemantics(object? value)
        => value is null or string || (value.GetType() is { } type
            && (type.IsPrimitive || type.IsEnum || type == typeof(TimeSpan)));

    [Fact]
    public void Default_options_keep_the_secure_value()
    {
        // The option defaults to true in code, independent of appsettings.
        Assert.True(new SmtpOptions().CheckCertificateRevocation);
    }

    [Fact]
    public void No_warning_is_reported_while_revocation_checking_is_on()
    {
        Assert.Null(SmtpClientSettings.DescribeRevocationRisk(Options(checkRevocation: true)));
    }

    [Fact]
    public void Disabled_revocation_reports_what_is_lost_and_what_still_applies()
    {
        var warning = SmtpClientSettings.DescribeRevocationRisk(Options(checkRevocation: false));

        Assert.NotNull(warning);
        // Names the residual risk...
        Assert.Contains("revoked", warning, StringComparison.OrdinalIgnoreCase);
        // ...and what keeps protecting the connection.
        Assert.Contains("chain", warning, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("hostname", warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Warning_does_not_leak_credentials()
    {
        var options = Options(checkRevocation: false);
        options.Username = "mailer@example.com";
        options.Password = "smtp-secret-value";

        var warning = SmtpClientSettings.DescribeRevocationRisk(options)!;

        Assert.DoesNotContain("smtp-secret-value", warning);
        Assert.DoesNotContain("mailer@example.com", warning);
    }
}
