using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;

namespace CiRunner.Core.Engine;

/// <summary>
/// Owns the append-only per-build log file and fans out newly written lines to SSE subscribers.
/// Completed builds are served as static files by the API layer (spec §5 F5); this hub only
/// tracks builds that are currently running.
/// </summary>
public sealed class LiveLogHub
{
    private sealed class BuildLog
    {
        public required string FilePath;
        public readonly object Lock = new();
        public readonly List<Channel<string>> Subscribers = new();
        public long Length;
    }

    private readonly ConcurrentDictionary<long, BuildLog> _builds = new();
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public void OpenForWriting(long buildId, string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        File.WriteAllText(filePath, string.Empty, Utf8NoBom);
        _builds[buildId] = new BuildLog { FilePath = filePath, Length = 0 };
    }

    /// <summary>Appends a line (newline appended) to the log file and pushes it to live subscribers. Returns file length after append.</summary>
    public long AppendLine(long buildId, string line)
    {
        if (!_builds.TryGetValue(buildId, out var state))
        {
            throw new InvalidOperationException($"Build {buildId} log is not open for writing.");
        }

        var text = line + "\n";
        var bytes = Utf8NoBom.GetBytes(text);
        lock (state.Lock)
        {
            using (var fs = new FileStream(state.FilePath, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                fs.Write(bytes, 0, bytes.Length);
            }
            state.Length += bytes.Length;
            foreach (var sub in state.Subscribers)
            {
                sub.Writer.TryWrite(text);
            }
            return state.Length;
        }
    }

    public long CurrentLength(long buildId) => _builds.TryGetValue(buildId, out var s) ? s.Length : 0;

    public bool IsLive(long buildId) => _builds.ContainsKey(buildId);

    /// <summary>Registers a subscriber and returns the existing log content plus a channel for future lines, captured atomically.</summary>
    public (string Backlog, ChannelReader<string> Reader)? Subscribe(long buildId)
    {
        if (!_builds.TryGetValue(buildId, out var state))
        {
            return null;
        }

        var channel = Channel.CreateUnbounded<string>();
        lock (state.Lock)
        {
            state.Subscribers.Add(channel);
            var backlog = File.Exists(state.FilePath) ? File.ReadAllText(state.FilePath, Utf8NoBom) : "";
            return (backlog, channel.Reader);
        }
    }

    public void Complete(long buildId)
    {
        if (_builds.TryRemove(buildId, out var state))
        {
            lock (state.Lock)
            {
                foreach (var sub in state.Subscribers)
                {
                    sub.Writer.TryComplete();
                }
            }
        }
    }
}
