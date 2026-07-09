using System.Xml.Linq;
using CiRunner.Core.Models;

namespace CiRunner.Core.Pipeline;

/// <summary>
/// Parses JUnit-format XML (spec §5 F4: "testsuites/testsuite/testcase"), accepting both a
/// wrapping &lt;testsuites&gt; root and a bare &lt;testsuite&gt; root. Parse failures are reported
/// to the caller rather than thrown, so one bad file can be a warning without failing the build
/// (DSL spec §3.4 Register-JUnit).
/// </summary>
public static class JUnitXmlParser
{
    public static bool TryParse(string filePath, out List<TestResultRecord> results, out string? error)
    {
        results = new List<TestResultRecord>();
        try
        {
            var doc = XDocument.Load(filePath, LoadOptions.None);
            var root = doc.Root;
            if (root is null)
            {
                error = "empty document";
                return false;
            }

            var suites = root.Name.LocalName == "testsuites"
                ? root.Elements("testsuite")
                : root.Name.LocalName == "testsuite"
                    ? new[] { root }
                    : Enumerable.Empty<XElement>();

            foreach (var suite in suites)
            {
                var suiteName = (string?)suite.Attribute("name");
                foreach (var testcase in suite.Elements("testcase"))
                {
                    results.Add(ParseTestCase(suiteName, testcase));
                }
            }

            error = null;
            return true;
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or InvalidOperationException or IOException)
        {
            error = ex.Message;
            return false;
        }
    }

    private static TestResultRecord ParseTestCase(string? suiteName, XElement testcase)
    {
        var name = (string?)testcase.Attribute("name") ?? "(unnamed)";
        var classname = (string?)testcase.Attribute("classname");
        var timeSec = (double?)testcase.Attribute("time");
        long? durationMs = timeSec is { } t ? (long)Math.Round(t * 1000) : null;

        var failure = testcase.Element("failure");
        var errorEl = testcase.Element("error");
        var skipped = testcase.Element("skipped");

        string status;
        string? message;
        if (errorEl is not null)
        {
            status = TestCaseStatus.Error;
            message = FailureMessage(errorEl);
        }
        else if (failure is not null)
        {
            status = TestCaseStatus.Failed;
            message = FailureMessage(failure);
        }
        else if (skipped is not null)
        {
            status = TestCaseStatus.Skipped;
            message = null;
        }
        else
        {
            status = TestCaseStatus.Passed;
            message = null;
        }

        return new TestResultRecord
        {
            Suite = suiteName ?? classname,
            Name = name,
            Status = status,
            DurationMs = durationMs,
            Message = message,
        };
    }

    private static string? FailureMessage(XElement el)
    {
        var attr = (string?)el.Attribute("message");
        var text = el.Value?.Trim();
        return !string.IsNullOrEmpty(attr) && !string.IsNullOrEmpty(text)
            ? $"{attr}\n{text}"
            : !string.IsNullOrEmpty(attr) ? attr : !string.IsNullOrEmpty(text) ? text : null;
    }
}
