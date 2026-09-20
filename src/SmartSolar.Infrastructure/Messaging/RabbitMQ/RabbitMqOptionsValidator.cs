using Microsoft.Extensions.Options;

namespace SmartSolar.Infrastructure.Messaging.RabbitMQ;

internal sealed class RabbitMqOptionsValidator
    : IValidateOptions<RabbitMqOptions>
{
    public ValidateOptionsResult Validate(string? name, RabbitMqOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.Host))
        {
            failures.Add(Required(nameof(RabbitMqOptions.Host)));
        }

        if (options.Port == 0)
        {
            failures.Add(
                $"'{RabbitMqOptions.SectionName}:{nameof(RabbitMqOptions.Port)}' must be between 1 and 65535.");
        }

        if (string.IsNullOrWhiteSpace(options.VirtualHost))
        {
            failures.Add(Required(nameof(RabbitMqOptions.VirtualHost)));
        }

        if (string.IsNullOrWhiteSpace(options.Username))
        {
            failures.Add(Required(nameof(RabbitMqOptions.Username)));
        }

        if (string.IsNullOrWhiteSpace(options.Password))
        {
            failures.Add(Required(nameof(RabbitMqOptions.Password)));
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static string Required(string key)
        => $"'{RabbitMqOptions.SectionName}:{key}' is required when RabbitMQ is enabled.";
}
