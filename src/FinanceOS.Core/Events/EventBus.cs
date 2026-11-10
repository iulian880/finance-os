namespace FinanceOS.Core.Events;

/// <summary>Something that happened in the domain, published after it was committed.</summary>
public interface IDomainEvent
{
    /// <summary>Changes made by one user action share a batch id.</summary>
    string BatchId { get; }

    ChangeOrigin Origin { get; }
}

/// <summary>Why the change happened. Automations (M20) ignore undo, so undoing never re-triggers a rule.</summary>
public enum ChangeOrigin
{
    Command,
    Undo,
}

public interface IEventBus
{
    /// <summary>
    /// Calls <paramref name="handler"/> for every published event assignable to <typeparamref name="T"/>
    /// (subscribe to <see cref="IDomainEvent"/> to see everything). Dispose the result to stop.
    /// </summary>
    IDisposable Subscribe<T>(Action<T> handler)
        where T : IDomainEvent;

    void Publish(IDomainEvent domainEvent);
}

/// <summary>
/// In-process, synchronous bus (ARCHITECTURE §4.4). Handlers run on the publishing thread, in
/// subscription order. A failing handler never affects the command (already committed) or the other
/// handlers: the error is reported through <see cref="HandlerFailed"/>.
/// </summary>
public sealed class EventBus : IEventBus
{
    private readonly Lock _gate = new();
    private readonly List<Subscription> _subscriptions = [];

    /// <summary>A handler threw. Hook up logging here.</summary>
    public event Action<IDomainEvent, Exception>? HandlerFailed;

    public IDisposable Subscribe<T>(Action<T> handler)
        where T : IDomainEvent
    {
        ArgumentNullException.ThrowIfNull(handler);
        var subscription = new Subscription(this, typeof(T), e => handler((T)e));
        lock (_gate)
        {
            _subscriptions.Add(subscription);
        }

        return subscription;
    }

    public void Publish(IDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        Subscription[] targets;
        lock (_gate)
        {
            // A copy: handlers may subscribe or unsubscribe while being called.
            targets = [.. _subscriptions];
        }

        foreach (var subscription in targets)
        {
            if (!subscription.IsActive || !subscription.EventType.IsInstanceOfType(domainEvent))
            {
                continue;
            }

            try
            {
                subscription.Handler(domainEvent);
            }
            catch (Exception ex)
            {
                HandlerFailed?.Invoke(domainEvent, ex);
            }
        }
    }

    private void Remove(Subscription subscription)
    {
        lock (_gate)
        {
            _subscriptions.Remove(subscription);
        }
    }

    private sealed class Subscription(EventBus bus, Type eventType, Action<IDomainEvent> handler) : IDisposable
    {
        public Type EventType { get; } = eventType;

        public Action<IDomainEvent> Handler { get; } = handler;

        public bool IsActive { get; private set; } = true;

        public void Dispose()
        {
            IsActive = false;
            bus.Remove(this);
        }
    }
}
