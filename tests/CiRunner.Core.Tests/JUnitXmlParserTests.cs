using CiRunner.Core.Models;
using CiRunner.Core.Pipeline;
using Xunit;

namespace CiRunner.Core.Tests;

/// <summary>JUnit XML parsing (ci-runner-test-spec.md §3.3 ENG-060/061).</summary>
public class JUnitXmlParserTests
{
    private static string WriteTemp(string xml)
    {
        var path = Path.Combine(Path.GetTempPath(), $"junit-{Guid.NewGuid()}.xml");
        File.WriteAllText(path, xml);
        return path;
    }

    [Fact]
    public void TryParse_WrappedTestsuites_ParsesAllCases_ENG060()
    {
        var path = WriteTemp("""
            <testsuites>
              <testsuite name="suite.a">
                <testcase name="passes" time="0.312"/>
                <testcase name="fails" time="0.012">
                  <failure message="expected 1, got 2">stack trace here</failure>
                </testcase>
                <testcase name="skips" time="0">
                  <skipped/>
                </testcase>
                <testcase name="errors" time="1.5">
                  <error message="boom">oops</error>
                </testcase>
              </testsuite>
            </testsuites>
            """);

        var ok = JUnitXmlParser.TryParse(path, out var results, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(4, results.Count);
        Assert.All(results, r => Assert.Equal("suite.a", r.Suite));

        var pass = results.Single(r => r.Name == "passes");
        Assert.Equal(TestCaseStatus.Passed, pass.Status);
        Assert.Equal(312, pass.DurationMs);

        var fail = results.Single(r => r.Name == "fails");
        Assert.Equal(TestCaseStatus.Failed, fail.Status);
        Assert.Contains("expected 1, got 2", fail.Message);

        var skip = results.Single(r => r.Name == "skips");
        Assert.Equal(TestCaseStatus.Skipped, skip.Status);

        var err = results.Single(r => r.Name == "errors");
        Assert.Equal(TestCaseStatus.Error, err.Status);
        Assert.Contains("boom", err.Message);
    }

    [Fact]
    public void TryParse_BareTestsuiteRoot_IsAlsoAccepted()
    {
        var path = WriteTemp("""
            <testsuite name="bare">
              <testcase name="one" time="0.001"/>
            </testsuite>
            """);

        var ok = JUnitXmlParser.TryParse(path, out var results, out _);

        Assert.True(ok);
        var single = Assert.Single(results);
        Assert.Equal("bare", single.Suite);
        Assert.Equal(TestCaseStatus.Passed, single.Status);
    }

    [Fact]
    public void TryParse_MultipleSuites_AllCollected()
    {
        var path = WriteTemp("""
            <testsuites>
              <testsuite name="a"><testcase name="1"/></testsuite>
              <testsuite name="b"><testcase name="2"/></testsuite>
            </testsuites>
            """);

        var ok = JUnitXmlParser.TryParse(path, out var results, out _);

        Assert.True(ok);
        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Suite == "a" && r.Name == "1");
        Assert.Contains(results, r => r.Suite == "b" && r.Name == "2");
    }

    [Fact]
    public void TryParse_MalformedXml_ReturnsFalseWithoutThrowing_ENG061()
    {
        var path = WriteTemp("<testsuites><testsuite name=\"broken\"><testcase name=\"x\"");

        var ok = JUnitXmlParser.TryParse(path, out var results, out var error);

        Assert.False(ok);
        Assert.Empty(results);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryParse_MissingFile_ReturnsFalseWithoutThrowing()
    {
        var ok = JUnitXmlParser.TryParse(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid() + ".xml"), out var results, out var error);

        Assert.False(ok);
        Assert.Empty(results);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryParse_UnrecognizedRootElement_YieldsNoResultsButSucceeds()
    {
        var path = WriteTemp("<somethingElse/>");

        var ok = JUnitXmlParser.TryParse(path, out var results, out var error);

        Assert.True(ok);
        Assert.Empty(results);
        Assert.Null(error);
    }
}
