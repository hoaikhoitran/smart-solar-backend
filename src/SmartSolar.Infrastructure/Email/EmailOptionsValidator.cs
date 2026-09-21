using Microsoft.Extensions.Options;

namespace SmartSolar.Infrastructure.Email;

/// <summary>
/// Validates SMTP settings only when email is enabled, so local development
/// needs no credentials. Messages name keys, never values.
/// </summary>
public sealed class EmailOptionsValidator : IValidateOptions<EmailOptions>
{
    public ValidateOptionsResult Validate(string? name, EmailOptions options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();
        var smtp = options.Smtp;

        if (string.IsNullOrWhiteSpace(smtp.Host))
        {
            failures.Add(Required(nameof(SmtpOptions.Host)));
        }

        if (smtp.Port is <= 0 or > 65535)
        {
            failures.Add($"'{EmailOptions.SectionName}:Smtp:{nameof(SmtpOptions.Port)}' must be between 1 and 65535.");
        }

        if (string.IsNullOrWhiteSpace(smtp.FromAddress))
        {
            failures.Add(Required(nameof(SmtpOptions.FromAddress)));
        }
        else if (!IsProbablyAnAddress(smtp.FromAddress))
        {
            failures.Add(
                $"'{EmailOptions.SectionName}:Smtp:{nameof(SmtpOptions.FromAddress)}' must be a valid email address.");
        }

        // A username means the server expects authentication.
        if (!string.IsNullOrWhiteSpace(smtp.Username) && string.IsNullOrWhiteSpace(smtp.Password))
        {
            failures.Add(Required(nameof(SmtpOptions.Password)));
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static bool IsProbablyAnAddress(string value)
    {
        var at = value.IndexOf('@');

        return at > 0
            && at < value.Length - 1
            && value.IndexOf('@', at + 1) < 0
            && value.LastIndexOf('.') > at
            && !value.Any(char.IsWhiteSpace);
    }

    private static string Required(string key)
        => $"'{EmailOptions.SectionName}:Smtp:{key}' is required when email is enabled.";
}
