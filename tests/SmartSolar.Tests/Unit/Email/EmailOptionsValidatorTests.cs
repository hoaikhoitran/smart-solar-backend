using SmartSolar.Infrastructure.Email;

namespace SmartSolar.Tests.Unit.Email;

/// <summary>Startup validation of the Email section: nothing is needed while disabled; an enabled setup must be complete and failures never echo secrets.</summary>
public class EmailOptionsValidatorTests
{
    private const string Secret = "sup3r-secret-smtp-password";

    private static EmailOptions Valid() => new()
    {
        Enabled = true,
        Smtp = new SmtpOptions
        {
            Host = "smtp.example.com", Port = 587, UseSsl = true, Username = "mailer@example.com", Password = Secret,
            FromAddress = "no-reply@example.com", FromName = "Smart Solar",
        },
    };

    private static Microsoft.Extensions.Options.ValidateOptionsResult Validate(EmailOptions options) => new EmailOptionsValidator().Validate(null, options);

    [Theory]
    [InlineData("complete")]
    [InlineData("disabled without credentials")]
    [InlineData("server without authentication")]
    public void Valid_configurations_pass(string setup)
    {
        var options = Valid();
        switch (setup)
        {
            case "disabled without credentials": options = new EmailOptions { Enabled = false, Smtp = new SmtpOptions() }; break;
            case "server without authentication": options.Smtp.Username = ""; options.Smtp.Password = ""; break;
        }

        Assert.True(Validate(options).Succeeded);
    }

    [Theory]
    [InlineData("Host", "")]
    [InlineData("Port", "0")]
    [InlineData("Port", "-1")]
    [InlineData("Port", "70000")]
    [InlineData("FromAddress", "")]
    [InlineData("FromAddress", "not-an-address")]
    [InlineData("Password", "")]   // a username needs a password
    public void An_enabled_configuration_with_a_bad_value_fails_naming_the_setting(string setting, string value)
    {
        var options = Valid();
        switch (setting)
        {
            case "Host": options.Smtp.Host = value; break;
            case "Port": options.Smtp.Port = int.Parse(value); break;
            case "FromAddress": options.Smtp.FromAddress = value; break;
            case "Password": options.Smtp.Password = value; break;
        }

        var result = Validate(options);

        Assert.True(result.Failed);
        if (value != "not-an-address") Assert.Contains(setting, string.Join(" ", result.Failures!));
    }

    [Fact]
    public void Failure_messages_never_contain_secret_values()
    {
        var options = Valid();
        options.Smtp.Host = "";
        options.Smtp.FromAddress = "";

        var message = string.Join(" ", Validate(options).Failures!);

        Assert.DoesNotContain(Secret, message);
        Assert.DoesNotContain("mailer@example.com", message);
    }
}
