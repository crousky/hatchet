using System.Collections.Concurrent;
using System.Threading.Channels;
using Factory.Contracts;

namespace Factory.Engine.Orchestration;

/// <summary>Fans orchestrator events out to any number of subscribers, each with its own channel.</summary>
public sealed class FactoryEventBus
{
    private readonly ConcurrentDictionary<Guid, Channel<FactoryEvent>> _subscribers = new();

    public void Publish(FactoryEvent evt)
    {
        foreach (var channel in _subscribers.Values) channel.Writer.TryWrite(evt);
    }

    public Subscription Subscribe()
    {
        // A slow browser tab loses old stream fragments rather than growing memory or blocking the pipeline.
        var channel = Channel.CreateBounded<FactoryEvent>(new BoundedChannelOptions(20_000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });
        var id = Guid.NewGuid();
        _subscribers[id] = channel;
        return new Subscription(channel.Reader, () =>
        {
            _subscribers.TryRemove(id, out _);
            channel.Writer.TryComplete();
        });
    }

    public sealed class Subscription(ChannelReader<FactoryEvent> reader, Action unsubscribe) : IDisposable
    {
        public ChannelReader<FactoryEvent> Reader { get; } = reader;

        public void Dispose() => unsubscribe();
    }
}
