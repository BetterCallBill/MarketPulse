using System.Text;
using MarketPulse.Api.Messaging;
using Microsoft.Extensions.Logging.Testing;
using NSubstitute;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace MarketPulse.UnitTests.Messaging;

public class TransientRetryTests
{
    private static BasicDeliverEventArgs Delivery(IReadOnlyBasicProperties properties) =>
        new("tag", 7UL, false, "", "q", properties, new ReadOnlyMemory<byte>([1, 2, 3]));

    private static IReadOnlyBasicProperties Properties(IDictionary<string, object?>? headers)
    {
        var properties = Substitute.For<IReadOnlyBasicProperties>();
        properties.Headers.Returns(headers);
        properties.MessageId.Returns("m1");
        properties.CorrelationId.Returns("c1");
        properties.ContentType.Returns("application/json");
        return properties;
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData(3, 3)]
    [InlineData(3L, 3)]
    public void ReadRetryCount_handles_absent_int_and_long(object? headerValue, int expected)
    {
        var headers = headerValue is null
            ? null
            : new Dictionary<string, object?> { ["x-retry-count"] = headerValue };

        Assert.Equal(expected, TransientRetry.ReadRetryCount(Properties(headers)));
    }

    [Theory]
    [InlineData("3", 3)]
    [InlineData("not-a-number", 0)]
    public void ReadRetryCount_handles_byte_array_headers(string headerText, int expected)
    {
        // RabbitMQ's own client round-trips headers as byte[] (see the int/long cases
        // above, which cover what *this* code writes back). An unparseable byte[] falling
        // back to 0 rather than throwing is deliberately safe: the very next republish
        // overwrites the header with a plain int, so an alien value costs at most one extra
        // retry round before the int counter takes over for good — it does not reopen the
        // unbounded loop this task exists to close.
        var headers = new Dictionary<string, object?>
        {
            ["x-retry-count"] = Encoding.UTF8.GetBytes(headerText),
        };

        Assert.Equal(expected, TransientRetry.ReadRetryCount(Properties(headers)));
    }

    [Fact]
    public async Task Below_the_cap_republishes_with_incremented_count_and_acks()
    {
        var channel = Substitute.For<IChannel>();
        var ea = Delivery(Properties(new Dictionary<string, object?>
        {
            ["x-retry-count"] = 2,
            ["traceparent"] = Encoding.UTF8.GetBytes("00-abc-def-01"),
        }));

        await TransientRetry.RetryOrDeadLetterAsync(
            channel, ea, "q", retryLimit: 5,
            new FakeLogger(), CancellationToken.None);

        await channel.Received(1).BasicPublishAsync(
            exchange: "",
            routingKey: "q",
            mandatory: true,
            basicProperties: Arg.Is<BasicProperties>(p =>
                (int)p!.Headers!["x-retry-count"]! == 3
                && p.Headers!.ContainsKey("traceparent")
                && p.CorrelationId == "c1"),
            body: Arg.Any<ReadOnlyMemory<byte>>(),
            cancellationToken: Arg.Any<CancellationToken>());
        await channel.Received(1).BasicAckAsync(7UL, false, Arg.Any<CancellationToken>());
        await channel.DidNotReceive().BasicNackAsync(
            Arg.Any<ulong>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task At_the_cap_dead_letters()
    {
        var channel = Substitute.For<IChannel>();
        var ea = Delivery(Properties(new Dictionary<string, object?> { ["x-retry-count"] = 5 }));

        await TransientRetry.RetryOrDeadLetterAsync(
            channel, ea, "q", retryLimit: 5, new FakeLogger(), CancellationToken.None);

        await channel.Received(1).BasicNackAsync(7UL, false, false, Arg.Any<CancellationToken>());
        await channel.DidNotReceive().BasicPublishAsync(
            exchange: Arg.Any<string>(), routingKey: Arg.Any<string>(), mandatory: Arg.Any<bool>(),
            basicProperties: Arg.Any<BasicProperties>(), body: Arg.Any<ReadOnlyMemory<byte>>(),
            cancellationToken: Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_republish_leaves_the_original_unacked()
    {
        var channel = Substitute.For<IChannel>();
        channel.BasicPublishAsync(
                exchange: Arg.Any<string>(), routingKey: Arg.Any<string>(), mandatory: Arg.Any<bool>(),
                basicProperties: Arg.Any<BasicProperties>(), body: Arg.Any<ReadOnlyMemory<byte>>(),
                cancellationToken: Arg.Any<CancellationToken>())
            .Returns<ValueTask>(_ => throw new InvalidOperationException("broker gone"));
        var ea = Delivery(Properties(null));

        await TransientRetry.RetryOrDeadLetterAsync(
            channel, ea, "q", retryLimit: 5, new FakeLogger(), CancellationToken.None);

        await channel.DidNotReceive().BasicAckAsync(
            Arg.Any<ulong>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await channel.DidNotReceive().BasicNackAsync(
            Arg.Any<ulong>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }
}
