using System.Threading.Channels;

namespace CiRunner.Core.Engine;

/// <summary>
/// Broadcasts build lifecycle / queue-change notifications for the SSE endpoint <c>/api/events</c> (spec §5 F5).
/// Payloads are pre-serialized JSON strings.
/// </summary>
public sealed class GlobalEventHub
{
    private readonly List<Channel<string>> _subscribers = new();
    private readonly object _lock = new();

    public void Publish(string jsonPayload)
    {
        lock (_lock)
        {
            foreach (var sub in _subscribers)
            {
                sub.Writer.TryWrite(jsonPayload);
            }
        }
    }

    public ChannelReader<string> Subscribe()
    {
        var channel = Channel.CreateUnbounded<string>();
        lock (_lock)
        {
            _subscribers.Add(channel);
        }
        return channel.Reader;
    }

    public void Unsubscribe(ChannelReader<string> reader)
    {
        lock (_lock)
        {
            _subscribers.RemoveAll(c => c.Reader == reader);
        }
    }
}
