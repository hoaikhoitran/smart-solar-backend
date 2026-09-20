using SmartSolar.Infrastructure.Email;

namespace SmartSolar.Tests.Unit.Email;

public class EmailOptionsValidatorTests
{
    private const string Secret = "sup3r-secret-smtp-password";

    private static EmailOptions Valid() => new()
    {
        Enabled = true,
        Smtp = new SmtpOptions
        {
            Host = "smtp.example.com",
            Port = 587,
            UseSsl = true,
            Username = "mailer@example.com",
            Password = Secret,
            FromAddress = "no-reply@example.com",
            FromName = "Smart Solar"
        }
    };

    private static EmailOptionsValidator Validator() => new();

    [Fact]
    public void Disabled_configuration_needs_no_credentials()
    {
        var options = new EmailOptions { Enabled = false, Smtp = new SmtpOptions() };

        Assert.True(Validator().Validate(null, options).Succeeded);
    }

    [Fact]
    public void Enabled_configuration_accepts_a_complete_setup()
    {
        Assert.True(Validator().Validate(null, Valid()).Succeeded);
    }

    [Fact]
    public void Enabled_configuration_rejects_a_missing_host()
    {
        var options = Valid();
        options.Smtp.Host = "";

        var result = Validator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("Host", string.Join(" ", result.Failures!));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(70000)]
    public void Enabled_configuration_rejects_an_invalid_port(int port)
    {
        var options = Valid();
        options.Smtp.Port = port;

        var result = Validator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("Port", string.Join(" ", result.Failures!));
    }

    [Fact]
    public void Enabled_configuration_rejects_a_missing_from_address()
    {
        var options = Valid();
        options.Smtp.FromAddress = "";

        var result = Validator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("FromAddress", string.Join(" ", result.Failures!));
    }

    [Fact]
    public void Enabled_configuration_rejects_a_malformed_from_address()
    {
        var options = Valid();
        options.Smtp.FromAddress = "not-an-address";

        var result = Validator().Validate(null, options);

        Assert.True(result.Failed);
    }

    [Fact]
    public void Enabled_configuration_requires_a_password_when_a_username_is_set()
    {
        var options = Valid();
        options.Smtp.Password = "";

        var result = Validator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("Password", string.Join(" ", result.Failures!));
    }

    [Fact]
    public void Enabled_configuration_allows_a_server_without_authentication()
    {
        var options = Valid();
        options.Smtp.Username = "";
        options.Smtp.Password = "";

        Assert.True(Validator().Validate(null, options).Succeeded);
    }

    [Fact]
    public void Failure_messages_never_contain_secret_values()
    {
        var options = Valid();
        options.Smtp.Host = "";
        options.Smtp.FromAddress = "";

        var result = Validator().Validate(null, options);
        var message = string.Join(" ", result.Failures!);

        Assert.DoesNotContain(Secret, message);
        Assert.DoesNotContain("mailer@example.com", message);
    }
}
