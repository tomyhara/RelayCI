using System.Runtime.CompilerServices;
using System.Text;

namespace CiRunner.Core.Pipeline;

/// <summary>
/// Tails a control file (JSON Lines, append-only, UTF-8 no BOM) per ci-runner-dsl-spec.md §4.
/// Incomplete trailing lines (mid-flush) are held over to the next read (§4.3).
/// </summary>
public sealed class ControlFileTailer
{
    public async IAsyncEnumerable<ControlFileEvent> TailAsync(
        string path,
        Func<bool> isProcessRunning,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        long position = 0;
        var carry = new StringBuilder();
        var exited = false;

        while (true)
        {
            foreach (var evt in ReadNewEvents(path, ref position, carry))
            {
                yield return evt;
            }

            if (exited)
            {
                break;
            }

            exited = !isProcessRunning();
            if (!exited)
            {
                await Task.Delay(50, ct);
            }
        }
    }

    private static List<ControlFileEvent> ReadNewEvents(string path, ref long position, StringBuilder carry)
    {
        var events = new List<ControlFileEvent>();
        if (!File.Exists(path))
        {
            return events;
        }

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (fs.Length <= position)
        {
            return events;
        }

        fs.Seek(position, SeekOrigin.Begin);
        using var reader = new StreamReader(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        var chunk = reader.ReadToEnd();
        position = fs.Position;

        if (chunk.Length == 0)
        {
            return events;
        }

        carry.Append(chunk);
        var buffered = carry.ToString();
        var lines = buffered.Split('\n');
        for (var i = 0; i < lines.Length - 1; i++)
        {
            var line = lines[i].TrimEnd('\r');
            var evt = ControlFileEvent.TryParse(line);
            if (evt is not null)
            {
                events.Add(evt);
            }
        }

        carry.Clear();
        carry.Append(lines[^1]);
        return events;
    }
}
