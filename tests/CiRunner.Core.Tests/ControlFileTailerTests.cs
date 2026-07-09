using System.Text;
using CiRunner.Core.Pipeline;
using Xunit;

namespace CiRunner.Core.Tests;

public class ControlFileTailerTests
{
    private static async Task<List<ControlFileEvent>> CollectAsync(IAsyncEnumerable<ControlFileEvent> source, CancellationToken ct)
    {
        var result = new List<ControlFileEvent>();
        await foreach (var evt in source.WithCancellation(ct))
        {
            result.Add(evt);
        }
        return result;
    }

    [Fact]
    public async Task TailAsync_FileAlreadyComplete_YieldsAllLines()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ctl-{Guid.NewGuid()}.jsonl");
        await File.WriteAllTextAsync(path,
            """{"ev":"start","v":1}""" + "\n" +
            """{"ev":"stage-start","seq":1,"name":"A"}""" + "\n",
            new UTF8Encoding(false));
        try
        {
            var tailer = new ControlFileTailer();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var events = await CollectAsync(tailer.TailAsync(path, () => false), cts.Token);

            Assert.Equal(2, events.Count);
            Assert.Equal("start", events[0].Ev);
            Assert.Equal("stage-start", events[1].Ev);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task TailAsync_IncrementalWrites_ObservesLinesWhileRunning()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ctl-{Guid.NewGuid()}.jsonl");
        File.WriteAllText(path, "", new UTF8Encoding(false));
        var running = true;
        try
        {
            var tailer = new ControlFileTailer();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var collectTask = CollectAsync(tailer.TailAsync(path, () => running), cts.Token);

            await AppendLineAsync(path, """{"ev":"start","v":1}""");
            await Task.Delay(250);
            await AppendLineAsync(path, """{"ev":"stage-start","seq":1,"name":"A"}""");
            await Task.Delay(250);
            running = false;

            var events = await collectTask;

            Assert.Equal(2, events.Count);
            Assert.Equal("start", events[0].Ev);
            Assert.Equal("stage-start", events[1].Ev);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task TailAsync_IncompleteTrailingLine_HeldUntilNewlineArrives()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ctl-{Guid.NewGuid()}.jsonl");
        File.WriteAllText(path, "", new UTF8Encoding(false));
        var running = true;
        try
        {
            var tailer = new ControlFileTailer();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var collectTask = CollectAsync(tailer.TailAsync(path, () => running), cts.Token);

            // Write a partial line (no trailing newline) - must not be parsed yet.
            await File.AppendAllTextAsync(path, """{"ev":"stage-start","seq":1""", new UTF8Encoding(false));
            await Task.Delay(300);

            // Complete the line.
            await File.AppendAllTextAsync(path, ",\"name\":\"A\"}\n", new UTF8Encoding(false));
            await Task.Delay(250);
            running = false;

            var events = await collectTask;

            var single = Assert.Single(events);
            Assert.Equal("stage-start", single.Ev);
            Assert.Equal("A", single.GetString("name"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task TailAsync_MissingFile_YieldsNothingAndCompletes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ctl-missing-{Guid.NewGuid()}.jsonl");
        var tailer = new ControlFileTailer();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var events = await CollectAsync(tailer.TailAsync(path, () => false), cts.Token);
        Assert.Empty(events);
    }

    private static async Task AppendLineAsync(string path, string line)
    {
        await File.AppendAllTextAsync(path, line + "\n", new UTF8Encoding(false));
    }
}
