using Microsoft.Playwright;

namespace CiRunner.E2E.Tests.Support;

/// <summary>Shared headless Chromium instance for the whole test run (spec's own L4 fixture note:
/// "Playwright のブラウザバイナリは %LOCALAPPDATA%\ms-playwright に配置され管理者権限不要"). Pages
/// are created fresh per test via NewPageAsync() from a caller-owned browser/context.</summary>
public sealed class PlaywrightFixture : IAsyncLifetime
{
    public IPlaywright Playwright { get; private set; } = null!;
    public IBrowser Browser { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        // --no-sandbox: the VSTest testhost process that runs these tests is itself wrapped in a
        // restrictive Windows Job Object, which conflicts with Chromium's own sandbox job object and
        // otherwise hangs navigation with no error (a plain `dotnet run` console app doesn't hit this).
        Browser = await Playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            Args = new[] { "--no-sandbox" },
        });
    }

    public async Task DisposeAsync()
    {
        await Browser.CloseAsync();
        Playwright.Dispose();
    }
}
