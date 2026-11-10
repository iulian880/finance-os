using FinanceOS.Core.Common;
using FinanceOS.Core.Events;
using FinanceOS.Core.Tasks;
using Xunit;

namespace FinanceOS.Core.Tests;

public sealed class EventBusTests
{
    private static readonly TaskItem Task = new() { Id = "t1", Title = "Task" };

    [Fact]
    public void Handlers_receive_events_of_their_type_and_of_derived_types_in_subscription_order()
    {
        var bus = new EventBus();
        var seen = new List<string>();
        bus.Subscribe<IDomainEvent>(_ => seen.Add("any"));
        bus.Subscribe<TaskEvent>(_ => seen.Add("task"));
        bus.Subscribe<TaskCreated>(_ => seen.Add("created"));
        bus.Subscribe<TaxonomyChanged>(_ => seen.Add("taxonomy"));

        bus.Publish(new TaskCreated(Task, "b1", ChangeOrigin.Command));

        Assert.Equal(["any", "task", "created"], seen);
    }

    [Fact]
    public void Disposing_a_subscription_stops_delivery_even_during_the_same_publish()
    {
        var bus = new EventBus();
        var calls = 0;
        IDisposable? second = null;
        bus.Subscribe<TaskCreated>(_ => second!.Dispose());
        second = bus.Subscribe<TaskCreated>(_ => calls++);

        bus.Publish(new TaskCreated(Task, "b1", ChangeOrigin.Command));
        bus.Publish(new TaskCreated(Task, "b2", ChangeOrigin.Command));

        Assert.Equal(0, calls);
    }

    [Fact]
    public void A_failing_handler_is_reported_and_the_others_still_run()
    {
        var bus = new EventBus();
        var after = 0;
        var failures = new List<(IDomainEvent Event, Exception Error)>();
        bus.HandlerFailed += (e, ex) => failures.Add((e, ex));
        bus.Subscribe<TaskCreated>(_ => throw new InvalidOperationException("boom"));
        bus.Subscribe<TaskCreated>(_ => after++);

        var published = new TaskCreated(Task, "b1", ChangeOrigin.Command);
        bus.Publish(published);

        Assert.Equal(1, after);
        var failure = Assert.Single(failures);
        Assert.Same(published, failure.Event);
        Assert.Equal("boom", failure.Error.Message);
    }

    [Fact]
    public void Handlers_may_subscribe_while_an_event_is_being_published()
    {
        var bus = new EventBus();
        var late = 0;
        bus.Subscribe<TaskCreated>(_ => bus.Subscribe<TaskCreated>(_ => late++));

        bus.Publish(new TaskCreated(Task, "b1", ChangeOrigin.Command));
        Assert.Equal(0, late); // not called for the event that was already being delivered

        bus.Publish(new TaskCreated(Task, "b2", ChangeOrigin.Command));
        Assert.Equal(1, late);
    }
}

public sealed class SnapshotTests
{
    [Fact]
    public void Snapshots_compare_list_contents_not_references()
    {
        var a = new TaskItem { Id = "t", Title = "x", Tags = ["a", "b"], Checklist = [new("c", "Check", false, 1)] };
        var b = a with { Tags = ["a", "b"], Checklist = [new("c", "Check", false, 1)] };

        Assert.NotEqual(a, b); // record equality compares list references
        Assert.True(Snapshots.Same(a, b));
        Assert.False(Snapshots.Same(a, b with { Tags = ["a"] }));
        Assert.True(Snapshots.Same<TaskItem>(null, null));
        Assert.False(Snapshots.Same(a, null));
    }

    [Fact]
    public void Snapshot_json_is_camel_case_with_readable_enums_and_without_computed_flags()
    {
        var json = Snapshots.Json(new TaskItem
        {
            Id = "t",
            Title = "VAT",
            Priority = Priority.Urgent,
            DueDate = new DateOnly(2026, 11, 25),
            DueTime = new TimeOnly(9, 30),
            DeletedAt = new DateTimeOffset(2026, 11, 5, 8, 0, 0, TimeSpan.Zero),
        })!;

        Assert.Contains("\"title\":\"VAT\"", json);
        Assert.Contains("\"priority\":\"urgent\"", json);
        Assert.Contains("\"dueDate\":\"2026-11-25\"", json);
        Assert.Contains("\"dueTime\":\"09:30:00\"", json);
        Assert.DoesNotContain("isDeleted", json);
        Assert.DoesNotContain("description", json); // nulls are left out
    }

    [Fact]
    public void Now_is_utc_and_truncated_to_the_millisecond_like_the_database()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 11, 5, 10, 30, 15, TimeSpan.FromHours(2)).AddTicks(1_234_567));

        var now = Ids.Now(clock);

        Assert.Equal(TimeSpan.Zero, now.Offset);
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 8, 30, 15, 123, TimeSpan.Zero), now);
    }

    [Fact]
    public void New_ids_are_uuid_v7_carrying_the_clock_time()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 11, 5, 8, 0, 0, TimeSpan.Zero));
        var id = Guid.Parse(Ids.New(clock));

        Assert.Equal(7, id.Version);
        var ms = Convert.ToInt64(id.ToString("N")[..12], 16);
        Assert.Equal(clock.GetUtcNow().ToUnixTimeMilliseconds(), ms);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }
}
