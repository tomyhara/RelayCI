using CiRunner.Core.Pipeline;
using Xunit;

namespace CiRunner.Core.Tests;

public class ControlFileEventTests
{
    [Fact]
    public void TryParse_ValidLine_ReturnsEventWithFields()
    {
        var evt = ControlFileEvent.TryParse("""{"t":"2026-01-01T00:00:00.000+09:00","ev":"stage-start","seq":1,"name":"Build","post":null}""");

        Assert.NotNull(evt);
        Assert.Equal("stage-start", evt!.Ev);
        Assert.Equal(1, evt.GetInt("seq"));
        Assert.Equal("Build", evt.GetString("name"));
        Assert.Null(evt.GetString("post"));
    }

    [Fact]
    public void TryParse_MissingEvField_ReturnsNull()
    {
        var evt = ControlFileEvent.TryParse("""{"t":"2026-01-01T00:00:00.000+09:00","seq":1}""");
        Assert.Null(evt);
    }

    [Fact]
    public void TryParse_MalformedJson_ReturnsNull()
    {
        var evt = ControlFileEvent.TryParse("""{"t":"2026-01-01T00:00:00.000+09:00","ev":"star""");
        Assert.Null(evt);
    }

    [Fact]
    public void TryParse_EmptyLine_ReturnsNull()
    {
        Assert.Null(ControlFileEvent.TryParse(""));
        Assert.Null(ControlFileEvent.TryParse("   "));
    }

    [Fact]
    public void GetString_WrongType_ReturnsNull()
    {
        var evt = ControlFileEvent.TryParse("""{"ev":"stage-start","seq":1}""");
        Assert.NotNull(evt);
        Assert.Null(evt!.GetString("seq"));
    }
}
