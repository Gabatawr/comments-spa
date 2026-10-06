using System.Text.Json;
using Comments.Application.Abstractions.Messaging;
using Comments.Application.Dtos;
using Comments.Infrastructure.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommentsApi.Tests;

/// <summary>
/// Covers the two things every event consumer depends on: which queue a work subscriber is called
/// for, and how a comment is read out of a payload that may arrive either as a live object or as
/// JSON after a broker round-trip.
///
/// The queue-routing test is a regression guard: the bus used to hand every work delivery to every
/// work subscriber, so one handler registered on both queues ran twice per event (two Elasticsearch
/// index calls and two cache-generation bumps for a single comment).
/// </summary>
public class MessagingTests
{
    [Fact]
    public async Task A_work_subscriber_is_called_once_per_queue_it_registered_for()
    {
        using var consumer = new InMemoryEventConsumer(NullLogger<InMemoryEventConsumer>.Instance);
        var searchDeliveries = 0;
        var cacheDeliveries = 0;

        consumer.Subscribe(EventQueues.Search, (_, _) =>
        {
            Interlocked.Increment(ref searchDeliveries);
            return Task.CompletedTask;
        });
        consumer.Subscribe(EventQueues.Cache, (_, _) =>
        {
            Interlocked.Increment(ref cacheDeliveries);
            return Task.CompletedTask;
        });

        await consumer.StartAsync(CancellationToken.None);
        await consumer.PublishAsync(CommentCreated(1));
        await WaitForProcessedAsync(consumer);

        // One event, one delivery per queue — not one per queue per subscriber.
        Assert.Equal(1, searchDeliveries);
        Assert.Equal(1, cacheDeliveries);
    }

    [Fact]
    public async Task A_work_subscriber_never_receives_a_queue_it_did_not_ask_for()
    {
        using var consumer = new InMemoryEventConsumer(NullLogger<InMemoryEventConsumer>.Instance);
        var deliveries = 0;

        consumer.Subscribe(EventQueues.Search, (_, _) =>
        {
            Interlocked.Increment(ref deliveries);
            return Task.CompletedTask;
        });

        await consumer.StartAsync(CancellationToken.None);
        await consumer.PublishAsync(CommentCreated(7));
        await WaitForProcessedAsync(consumer);

        Assert.Equal(1, deliveries);
    }

    [Fact]
    public void An_unknown_queue_is_rejected_instead_of_silently_never_firing()
    {
        using var consumer = new InMemoryEventConsumer(NullLogger<InMemoryEventConsumer>.Instance);

        var error = Assert.Throws<ArgumentOutOfRangeException>(
            () => consumer.Subscribe("comments.events.typo", (_, _) => Task.CompletedTask));

        Assert.Contains(EventQueues.Search, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failing_work_subscriber_is_isolated_from_the_pump()
    {
        using var consumer = new InMemoryEventConsumer(NullLogger<InMemoryEventConsumer>.Instance);
        var healthyDeliveries = 0;

        consumer.Subscribe(EventQueues.Search, (_, _) => throw new InvalidOperationException("index is down"));
        consumer.Subscribe(EventQueues.Cache, (_, _) =>
        {
            Interlocked.Increment(ref healthyDeliveries);
            return Task.CompletedTask;
        });

        await consumer.StartAsync(CancellationToken.None);
        await consumer.PublishAsync(CommentCreated(9));
        await WaitForProcessedAsync(consumer);

        // The failed queue is logged, not fatal: the other side effect still happened.
        Assert.Equal(1, healthyDeliveries);
    }

    [Fact]
    public void A_payload_is_read_the_same_way_before_and_after_the_broker()
    {
        var dto = new CommentDto { Id = 42, UserName = "Alisa", TextPlain = "текст" };

        // In-process: the live payload object.
        Assert.Equal(42, ((object)new CommentCreatedPayload(dto)).AsComment()?.Id);

        // After a round-trip: { comment: {...} } as JSON.
        var envelope = new DomainEventEnvelope { EventType = DomainEventPayload.CommentCreated, Payload = new CommentCreatedPayload(dto) };
        var json = JsonSerializer.SerializeToUtf8Bytes(envelope, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var roundTripped = JsonSerializer.Deserialize<DomainEventEnvelope>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(42, roundTripped!.Payload.AsComment()?.Id);
        Assert.Equal("Alisa", roundTripped.Payload.AsComment()?.UserName);
    }

    [Fact]
    public void An_unreadable_payload_yields_null_instead_of_throwing()
    {
        Assert.Null(((object?)null).AsComment());
        Assert.Null(((object)"not a comment").AsComment());
        Assert.Null(((object)new { unrelated = true }).AsComment());
    }

    [Fact]
    public void Only_comment_created_envelopes_are_recognised()
    {
        Assert.True(NewEnvelope("CommentCreated").IsCommentCreated());
        Assert.True(NewEnvelope("commentcreated").IsCommentCreated());
        Assert.False(NewEnvelope("CommentIndexed").IsCommentCreated());
    }

    private static DomainEventEnvelope NewEnvelope(string eventType) => new()
    {
        EventType = eventType,
        Payload = new CommentCreatedPayload(new CommentDto { Id = 1 }),
    };

    private static DomainEventEnvelope CommentCreated(int id) => new()
    {
        EventType = DomainEventPayload.CommentCreated,
        Payload = new CommentCreatedPayload(new CommentDto { Id = id }),
    };

    private static async Task WaitForProcessedAsync(InMemoryEventConsumer consumer)
    {
        for (var attempt = 0; attempt < 200 && consumer.Processed == 0; attempt++)
        {
            await Task.Delay(10);
        }

        Assert.True(consumer.Processed > 0, "the in-memory pump did not dispatch the event in time");
    }
}
