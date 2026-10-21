using MailKit.Net.Smtp;
using Microsoft.Extensions.Configuration;
using SmartSolar.Infrastructure.Email;

namespace SmartSolar.Tests.Unit.Email;

/// <summary>
/// SMTP certificate revocation checking: how the setting binds from configuration (secure default when absent), and what the real
/// MailKit client object (never connected) carries before ConnectAsync. Turning revocation off must not weaken anything else.
/// </summary>
public class SmtpClientSettingsTests
{
    private static SmtpOptions Options(bool? checkRevocation = null)
    {
        var options = new SmtpOptions { Host = "smtp.example.com", Port = 587, UseSsl = true, FromAddress = "no-reply@example.com" };
        if (checkRevocation is not null) options.CheckCertificateRevocation = checkRevocation.Value;
        return options;
    }

    [Theory]
    [InlineData(null, true)]      // absent key: the secure default
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void The_configuration_key_binds_and_reaches_the_client(string? configured, bool expected)
    {
        var settings = new Dictionary<string, string?> { ["Email:Enabled"] = "true" };
        if (configured is not null) settings["Email:Smtp:CheckCertificateRevocation"] = configured;
        var options = new EmailOptions();
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build().GetSection(EmailOptions.SectionName).Bind(options);
        Assert.Equal(expected, options.Smtp.CheckCertificateRevocation);

        using var client = new SmtpClient();
        // Poisoned first: MailKit's own default is true, so the assertion would otherwise pass even if Apply did nothing.
        client.CheckCertificateRevocation = !expected;
        SmtpClientSettings.Apply(client, options.Smtp);
        Assert.Equal(expected, client.CheckCertificateRevocation);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_client_production_uses_carries_the_setting_and_never_a_validation_callback(bool checkRevocation)
    {
        // CreateClient is the only way the sender obtains a client. A validation callback would accept certificates MailKit rejects.
        using var client = SmtpClientSettings.CreateClient(Options(checkRevocation));

        Assert.Equal(checkRevocation, client.CheckCertificateRevocation);
        Assert.Null(client.ServerCertificateValidationCallback);
    }

    [Fact]
    public void Disabling_revocation_changes_no_other_client_setting()
    {
        using var untouched = new SmtpClient();
        using var client = new SmtpClient();

        SmtpClientSettings.Apply(client, Options(checkRevocation: false));

        // Every value-like property (bools, enums, timeouts, SslProtocols...) must match an untouched client. Reference-typed
        // collaborators such as ProtocolLogger are distinct instances by design, so they are compared by nullness only.
        var compared = 0;
        foreach (var property in typeof(SmtpClient).GetProperties()
                     .Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && p.Name != nameof(SmtpClient.CheckCertificateRevocation)))
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
                continue;   // properties that require a live connection
            }

            if (HasValueSemantics(expected) && HasValueSemantics(actual)) Assert.Equal(expected, actual);
            else Assert.Equal(expected is null, actual is null);
            compared++;
        }

        Assert.True(compared > 5, $"Expected to compare several properties, compared {compared}.");
    }

    private static bool HasValueSemantics(object? value)
        => value is null or string || (value.GetType() is { } type && (type.IsPrimitive || type.IsEnum || type == typeof(TimeSpan)));

    [Fact]
    public void Disabled_revocation_warns_about_what_is_lost_without_leaking_credentials()
    {
        Assert.Null(SmtpClientSettings.DescribeRevocationRisk(Options(checkRevocation: true)));

        var options = Options(checkRevocation: false);
        options.Username = "mailer@example.com";
        options.Password = "smtp-secret-value";
        var warning = SmtpClientSettings.DescribeRevocationRisk(options)!;

        Assert.Contains("revoked", warning, StringComparison.OrdinalIgnoreCase);   // the residual risk...
        Assert.Contains("chain", warning, StringComparison.OrdinalIgnoreCase);     // ...and what still protects the connection
        Assert.Contains("hostname", warning, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("smtp-secret-value", warning);
        Assert.DoesNotContain("mailer@example.com", warning);
    }

    [Fact]
    public void Shipped_appsettings_keeps_the_secure_defaults()
    {
        // Walk up to the solution root instead of assuming the output layout.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SmartSolar.sln"))) directory = directory.Parent;
        Assert.NotNull(directory);

        var options = new EmailOptions();
        new ConfigurationBuilder().AddJsonFile(Path.Combine(directory!.FullName, "src", "SmartSolar.Api", "appsettings.json")).Build()
            .GetSection(EmailOptions.SectionName).Bind(options);

        Assert.True(options.Smtp.CheckCertificateRevocation);
        Assert.False(options.Smtp.AllowInsecureTransport);
    }
}
