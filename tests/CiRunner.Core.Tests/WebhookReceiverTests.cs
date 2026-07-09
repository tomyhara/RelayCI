using System.Security.Cryptography;
using System.Text;
using CiRunner.Core.Engine;
using CiRunner.Core.Models;
using CiRunner.Core.Tests.Support;
using Xunit;

namespace CiRunner.Core.Tests;

/// <summary>Webhook receiver tests (ci-runner-test-spec.md §3.4 WH-001/002/003/008).</summary>
public class WebhookReceiverTests
{
    private static string Sign(string secret, byte[] body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return "sha256=" + Convert.ToHexString(hmac.ComputeHash(body)).ToLowerInvariant();
    }

    private static async Task<HookRunRecord> WaitForCompletionAsync(EngineFixture fx, long hookId, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (!cts.IsCancellationRequested)
        {
            var runs = fx.HookRuns.ListRecent(hookId, 10);
            var run = runs.FirstOrDefault();
            if (run is not null && run.Status != HookRunStatus.Running)
            {
                return run;
            }
            await Task.Delay(50, CancellationToken.None);
        }
        throw new TimeoutException("hook run did not complete in time");
    }

    [Fact]
    public async Task ReceiveAsync_ValidSignature_AcceptsAndRunsHandler_WH001()
    {
        using var fx = new EngineFixture();
        var hook = fx.CreateHook("h1", "return", secret: "s3cret");
        var body = Encoding.UTF8.GetBytes("""{"ref":"refs/heads/main"}""");
        var headers = new Dictionary<string, string>
        {
            ["X-Hub-Signature-256"] = Sign("s3cret", body),
            ["X-GitHub-Delivery"] = "delivery-1",
            ["X-GitHub-Event"] = "push",
        };

        var result = await fx.Webhook.ReceiveAsync("h1", body, headers, CancellationToken.None);

        Assert.Equal(WebhookReceiveResult.Accepted, result);
        var run = await WaitForCompletionAsync(fx, hook.Id, TimeSpan.FromSeconds(10));
        Assert.Equal(HookRunStatus.Success, run.Status);
        Assert.Equal("delivery-1", run.DeliveryId);
        Assert.Equal("push", run.Event);
    }

    [Fact]
    public async Task ReceiveAsync_InvalidSignature_ReturnsUnauthorized_WH002()
    {
        using var fx = new EngineFixture();
        var hook = fx.CreateHook("h1", "return", secret: "s3cret");
        var body = Encoding.UTF8.GetBytes("""{"ref":"refs/heads/main"}""");
        var headers = new Dictionary<string, string>
        {
            ["X-Hub-Signature-256"] = "sha256=" + new string('0', 64),
            ["X-GitHub-Delivery"] = "delivery-2",
        };

        var result = await fx.Webhook.ReceiveAsync("h1", body, headers, CancellationToken.None);

        Assert.Equal(WebhookReceiveResult.Unauthorized, result);
        Assert.Empty(fx.HookRuns.ListRecent(hook.Id, 10));
    }

    [Fact]
    public async Task ReceiveAsync_MissingSignatureHeader_ReturnsUnauthorized()
    {
        using var fx = new EngineFixture();
        fx.CreateHook("h1", "return", secret: "s3cret");
        var body = Encoding.UTF8.GetBytes("{}");

        var result = await fx.Webhook.ReceiveAsync("h1", body, new Dictionary<string, string>(), CancellationToken.None);

        Assert.Equal(WebhookReceiveResult.Unauthorized, result);
    }

    [Fact]
    public async Task ReceiveAsync_NoSecretConfigured_SkipsVerification()
    {
        using var fx = new EngineFixture();
        var hook = fx.CreateHook("h1", "return", secret: null);
        var body = Encoding.UTF8.GetBytes("{}");

        var result = await fx.Webhook.ReceiveAsync("h1", body, new Dictionary<string, string>(), CancellationToken.None);

        Assert.Equal(WebhookReceiveResult.Accepted, result);
        await WaitForCompletionAsync(fx, hook.Id, TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ReceiveAsync_UnknownHook_ReturnsNotFound()
    {
        using var fx = new EngineFixture();
        var result = await fx.Webhook.ReceiveAsync("does-not-exist", Encoding.UTF8.GetBytes("{}"), new Dictionary<string, string>(), CancellationToken.None);
        Assert.Equal(WebhookReceiveResult.HookNotFound, result);
    }

    [Fact]
    public async Task ReceiveAsync_DuplicateDeliveryId_RunsHandlerOnceOnly_WH003()
    {
        using var fx = new EngineFixture();
        var hook = fx.CreateHook("h1", "return", secret: null);
        var body = Encoding.UTF8.GetBytes("{}");
        var headers = new Dictionary<string, string> { ["X-GitHub-Delivery"] = "dup-1" };

        var first = await fx.Webhook.ReceiveAsync("h1", body, headers, CancellationToken.None);
        await WaitForCompletionAsync(fx, hook.Id, TimeSpan.FromSeconds(10));
        var second = await fx.Webhook.ReceiveAsync("h1", body, headers, CancellationToken.None);

        Assert.Equal(WebhookReceiveResult.Accepted, first);
        Assert.Equal(WebhookReceiveResult.Accepted, second);
        var runs = fx.HookRuns.ListRecent(hook.Id, 10);
        Assert.Single(runs);
    }

    [Fact]
    public async Task ReceiveAsync_HandlerFailure_RecordsFailedStatus()
    {
        using var fx = new EngineFixture();
        var hook = fx.CreateHook("h1", "throw 'boom'", secret: null);
        var body = Encoding.UTF8.GetBytes("{}");

        await fx.Webhook.ReceiveAsync("h1", body, new Dictionary<string, string>(), CancellationToken.None);
        var run = await WaitForCompletionAsync(fx, hook.Id, TimeSpan.FromSeconds(10));

        Assert.Equal(HookRunStatus.Failed, run.Status);
        Assert.NotNull(run.LogPath);
        Assert.Contains("boom", File.ReadAllText(run.LogPath!));
    }

    [Fact]
    public async Task ReceiveAsync_HandlerTimeout_RecordsTimeoutStatus_WH008()
    {
        using var fx = new EngineFixture();
        var hook = fx.CreateHook("h1", "Start-Sleep -Seconds 30", secret: null, timeoutSec: 1);
        var body = Encoding.UTF8.GetBytes("{}");

        await fx.Webhook.ReceiveAsync("h1", body, new Dictionary<string, string>(), CancellationToken.None);
        var run = await WaitForCompletionAsync(fx, hook.Id, TimeSpan.FromSeconds(15));

        Assert.Equal(HookRunStatus.Timeout, run.Status);
    }
}
