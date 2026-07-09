using Xunit;

// Each test class launches its own Chromium instance and its own CiRunner.Host subprocess; running
// multiple test classes concurrently starves them all and causes spurious 30s Playwright timeouts
// under load (e.g. when the whole solution's test suite runs together). These tests are already slow
// (real subprocess + real browser) so serializing them is a small, worthwhile cost for reliability.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
