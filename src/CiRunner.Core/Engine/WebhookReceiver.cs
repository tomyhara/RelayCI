using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CiRunner.Core.Data;
using CiRunner.Core.Paths;

namespace CiRunner.Core.Engine;

public enum WebhookReceiveResult
{
    HookNotFound,
    Unauthorized,
    Accepted,
}

/// <summary>
/// Generic webhook receiver (spec §5 F1): "受けて、検証して、ハンドラに渡す" - HMAC-SHA256
/// signature verification, X-GitHub-Delivery idempotency, payload/header persistence, then
/// launches the handler script without waiting for it (dedicated slot pool, not an Executor).
/// </summary>
public sealed class WebhookReceiver
{
    private readonly RunnerPaths _paths;
    private readonly HookRepository _hookRepo;
    private readonly HookRunRepository _hookRunRepo;
    private readonly HandlerRunner _handlerRunner;
    private readonly SemaphoreSlim _slotPool;

    public WebhookReceiver(RunnerPaths paths, HookRepository hookRepo, HookRunRepository hookRunRepo, HandlerRunner handlerRunner, int maxConcurrency = 4)
    {
        _paths = paths;
        _hookRepo = hookRepo;
        _hookRunRepo = hookRunRepo;
        _handlerRunner = handlerRunner;
        _slotPool = new SemaphoreSlim(maxConcurrency, maxConcurrency);
    }

    public async Task<WebhookReceiveResult> ReceiveAsync(string hookName, byte[] body, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        var hook = _hookRepo.FindByName(hookName);
        if (hook is null || !hook.Enabled)
        {
            return WebhookReceiveResult.HookNotFound;
        }

        if (!string.IsNullOrEmpty(hook.Secret))
        {
            if (!headers.TryGetValue("X-Hub-Signature-256", out var signature) || !VerifySignature(hook.Secret, body, signature))
            {
                return WebhookReceiveResult.Unauthorized;
            }
        }

        headers.TryGetValue("X-GitHub-Delivery", out var deliveryId);
        headers.TryGetValue("X-GitHub-Event", out var eventName);
        deliveryId = string.IsNullOrEmpty(deliveryId) ? null : deliveryId;
        eventName = string.IsNullOrEmpty(eventName) ? null : eventName;

        if (deliveryId is not null && _hookRunRepo.IsDuplicateDelivery(hook.Id, deliveryId))
        {
            // Known delivery (GHES re-send): 200, no-op - spec §5 F1 step 2.
            return WebhookReceiveResult.Accepted;
        }

        Directory.CreateDirectory(_paths.HookPayloadsDir);
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ");
        var payloadPath = Path.Combine(_paths.HookPayloadsDir, $"{hook.Name}-{stamp}.json");
        var headersPath = Path.Combine(_paths.HookPayloadsDir, $"{hook.Name}-{stamp}.headers.json");
        await File.WriteAllBytesAsync(payloadPath, body, ct);
        await File.WriteAllTextAsync(headersPath, JsonSerializer.Serialize(headers), ct);

        var runId = _hookRunRepo.CreateRunning(hook.Id, deliveryId, eventName, payloadPath);

        // Fire-and-forget: the HTTP response must not wait for the handler (GHES timeout avoidance,
        // spec §5 F1 step 3). Concurrency is capped by the slot pool, not by delaying the response.
        _ = Task.Run(async () =>
        {
            await _slotPool.WaitAsync(CancellationToken.None);
            try
            {
                await _handlerRunner.RunAsync(hook, runId, payloadPath, headersPath, eventName, deliveryId, CancellationToken.None);
            }
            finally
            {
                _slotPool.Release();
            }
        }, CancellationToken.None);

        return WebhookReceiveResult.Accepted;
    }

    private static bool VerifySignature(string secret, byte[] body, string signatureHeader)
    {
        const string prefix = "sha256=";
        if (!signatureHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        byte[] providedBytes;
        try
        {
            providedBytes = Convert.FromHexString(signatureHeader.AsSpan(prefix.Length));
        }
        catch (FormatException)
        {
            return false;
        }

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var computed = hmac.ComputeHash(body);
        return computed.Length == providedBytes.Length && CryptographicOperations.FixedTimeEquals(computed, providedBytes);
    }
}
