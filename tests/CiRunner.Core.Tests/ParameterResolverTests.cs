using System.Text.Json;
using CiRunner.Core.Engine;
using CiRunner.Core.Models;
using Xunit;

namespace CiRunner.Core.Tests;

public class ParameterResolverTests
{
    private static string DefsJson(params JobParameterDef[] defs) => JsonSerializer.Serialize(defs);

    [Fact]
    public void Resolve_NoDefinitions_ReturnsEmptyObject()
    {
        var result = ParameterResolver.Resolve("[]", null);
        Assert.True(result.Success);
        Assert.Equal("{}", result.ParametersJson);
    }

    [Fact]
    public void Resolve_DeclaredParamProvided_IsIncluded()
    {
        var defs = DefsJson(new JobParameterDef { Name = "Foo", Required = false });
        var result = ParameterResolver.Resolve(defs, new Dictionary<string, string> { ["Foo"] = "bar" });

        Assert.True(result.Success);
        using var doc = JsonDocument.Parse(result.ParametersJson);
        Assert.Equal("bar", doc.RootElement.GetProperty("Foo").GetString());
    }

    [Fact]
    public void Resolve_MissingOptionalParam_UsesDefault()
    {
        var defs = DefsJson(new JobParameterDef { Name = "Foo", Default = "defaultVal", Required = false });
        var result = ParameterResolver.Resolve(defs, null);

        Assert.True(result.Success);
        using var doc = JsonDocument.Parse(result.ParametersJson);
        Assert.Equal("defaultVal", doc.RootElement.GetProperty("Foo").GetString());
    }

    [Fact]
    public void Resolve_MissingRequiredParam_Fails()
    {
        var defs = DefsJson(new JobParameterDef { Name = "Foo", Required = true });
        var result = ParameterResolver.Resolve(defs, null);

        Assert.False(result.Success);
        Assert.Contains("Foo", result.Error);
    }

    [Fact]
    public void Resolve_UndeclaredParam_IsSilentlyDropped()
    {
        var defs = DefsJson(new JobParameterDef { Name = "Foo", Required = false });
        var result = ParameterResolver.Resolve(defs, new Dictionary<string, string> { ["Foo"] = "bar", ["Undeclared"] = "x" });

        Assert.True(result.Success);
        using var doc = JsonDocument.Parse(result.ParametersJson);
        Assert.False(doc.RootElement.TryGetProperty("Undeclared", out _));
        Assert.True(doc.RootElement.TryGetProperty("Foo", out _));
    }

    [Fact]
    public void Resolve_RequiredParamProvided_OverridesDefault()
    {
        var defs = DefsJson(new JobParameterDef { Name = "Foo", Default = "def", Required = true });
        var result = ParameterResolver.Resolve(defs, new Dictionary<string, string> { ["Foo"] = "override" });

        Assert.True(result.Success);
        using var doc = JsonDocument.Parse(result.ParametersJson);
        Assert.Equal("override", doc.RootElement.GetProperty("Foo").GetString());
    }

    [Theory]
    [InlineData("CI_FOO", true)]
    [InlineData("Foo", false)]
    public void IsReservedName_ChecksCiPrefix(string name, bool expected)
    {
        Assert.Equal(expected, ParameterResolver.IsReservedName(name));
    }
}
