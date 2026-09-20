namespace SmartSolar.Infrastructure.Messaging.RabbitMQ;

internal sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    public bool Enabled { get; set; }

    public string Host { get; set; } = string.Empty;

    public ushort Port { get; set; } = 5672;

    public string VirtualHost { get; set; } = "/";

    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;
}
