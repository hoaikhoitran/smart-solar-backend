using Microsoft.Extensions.Configuration;
using SmartSolar.Infrastructure.Email;

namespace SmartSolar.Tests.Unit.Email;

/// <summary>
/// Proves the configuration key and its environment-variable form reach the option.
/// </summary>
public class SmtpOptionsBindingTests
{
    private static EmailOptions Bind(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var options = new EmailOptions();
        configuration.GetSection(EmailOptions.SectionName).Bind(options);
        return options;
    }

    [Fact]
    public void Key_binds_false_when_configured()
    {
        var options = Bind(new Dictionary<string, string?>
        {
            ["Email:Smtp:CheckCertificateRevocation"] = "false"
        });

        Assert.False(options.Smtp.CheckCertificateRevocation);
    }

    [Fact]
    public void Key_binds_true_when_configured()
    {
        var options = Bind(new Dictionary<string, string?>
        {
            ["Email:Smtp:CheckCertificateRevocation"] = "true"
        });

        Assert.True(options.Smtp.CheckCertificateRevocation);
    }

    [Fact]
    public void Absent_key_keeps_revocation_checking_on()
    {
        var options = Bind(new Dictionary<string, string?> { ["Email:Enabled"] = "true" });

        Assert.True(options.Smtp.CheckCertificateRevocation);
    }

    [Fact]
    public void Shipped_appsettings_keeps_the_secure_default()
    {
        // Walk up to the solution root instead of assuming the output layout.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SmartSolar.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var path = Path.Combine(directory!.FullName, "src", "SmartSolar.Api", "appsettings.json");

        var configuration = new ConfigurationBuilder().AddJsonFile(path).Build();
        var options = new EmailOptions();
        configuration.GetSection(EmailOptions.SectionName).Bind(options);

        Assert.True(options.Smtp.CheckCertificateRevocation);
        Assert.False(options.Smtp.AllowInsecureTransport);
    }
}
