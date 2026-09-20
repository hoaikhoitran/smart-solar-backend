using SmartSolar.Infrastructure.Messaging.RabbitMQ.Consumers;

namespace SmartSolar.Tests.Unit.Email;

/// <summary>
/// Pins the endpoint contract. These values are asserted, not exercised: proving
/// they reach MassTransit needs a bus, and the queue topology needs a broker.
/// </summary>
public class EmailVerificationEndpointTests
{
    [Fact]
    public void Uses_the_agreed_durable_queue_name()
    {
        Assert.Equal("email-verification", EmailVerificationEndpoint.QueueName);
    }

    [Fact]
    public void Retries_three_times_with_a_five_second_interval()
    {
        Assert.Equal(3, EmailVerificationEndpoint.RetryLimit);
        Assert.Equal(TimeSpan.FromSeconds(5), EmailVerificationEndpoint.RetryInterval);
    }

    [Fact]
    public void Retry_delay_stays_bounded_and_short()
    {
        Assert.InRange(EmailVerificationEndpoint.RetryInterval, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));
        Assert.InRange(EmailVerificationEndpoint.RetryLimit, 1, 5);
    }
}
