using System.Text.Json;
using CiRunner.Core.Data;
using CiRunner.Core.Paths;

namespace CiRunner.Core.Engine;

/// <summary>
/// Discovers webhook hooks by scanning <c>hooks/&lt;name&gt;.cipipe</c> and registers any not yet
/// present in the DB, reading an optional sibling <c>hooks/&lt;name&gt;.json</c> for secret/timeout/
/// enabled. Stand-in for the F6 hook-management admin UI, which is a later milestone.
/// </summary>
public sealed class HookScanner
{
    private readonly RunnerPaths _paths;
    private readonly HookRepository _hookRepo;

    public HookScanner(RunnerPaths paths, HookRepository hookRepo)
    {
        _paths = paths;
        _hookRepo = hookRepo;
    }

    public void ScanAndRegister()
    {
        if (!Directory.Exists(_paths.HooksDir))
        {
            return;
        }

        foreach (var handlerPath in Directory.GetFiles(_paths.HooksDir, "*.cipipe"))
        {
            var name = Path.GetFileNameWithoutExtension(handlerPath);
            var config = ReadConfig(_paths.HookConfigPath(name));
            _hookRepo.UpsertDiscoveredHook(name, handlerPath, config.Secret, config.TimeoutSec, config.Enabled);
        }
    }

    private static HookConfig ReadConfig(string configPath)
    {
        if (!File.Exists(configPath))
        {
            return new HookConfig(null, 60, true);
        }

        try
        {
            var json = File.ReadAllText(configPath);
            var doc = JsonSerializer.Deserialize<HookConfigDto>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
            return new HookConfig(doc?.Secret, doc?.TimeoutSec ?? 60, doc?.Enabled ?? true);
        }
        catch (JsonException)
        {
            // Malformed config: fall back to a disabled-signature-check default rather than
            // failing startup over one bad hook config file.
            return new HookConfig(null, 60, true);
        }
    }

    private sealed record HookConfig(string? Secret, int TimeoutSec, bool Enabled);

    private sealed class HookConfigDto
    {
        public string? Secret { get; set; }
        public int? TimeoutSec { get; set; }
        public bool? Enabled { get; set; }
    }
}
