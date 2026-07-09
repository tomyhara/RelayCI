using System.Text.Json;

namespace CiRunner.Core.Config;

/// <summary>
/// Loads config.json (infra settings only, per spec §10) and applies CLI-argument overrides
/// (<c>--port</c>, <c>--bind</c>, <c>--config</c>, <c>--root</c>).
/// </summary>
public static class ConfigLoader
{
    public static RunnerConfig Load(string[] args)
    {
        var configPath = "config.json";
        var root = Directory.GetCurrentDirectory();

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--config" && i + 1 < args.Length)
            {
                configPath = args[i + 1];
            }
            else if (args[i] == "--root" && i + 1 < args.Length)
            {
                root = args[i + 1];
            }
        }

        RunnerConfig config;
        var resolvedConfigPath = Path.IsPathRooted(configPath) ? configPath : Path.Combine(root, configPath);
        if (File.Exists(resolvedConfigPath))
        {
            var json = File.ReadAllText(resolvedConfigPath);
            config = JsonSerializer.Deserialize<RunnerConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            }) ?? new RunnerConfig();
        }
        else
        {
            config = new RunnerConfig();
        }

        config.RootDir = root;

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--port" && i + 1 < args.Length && int.TryParse(args[i + 1], out var port))
            {
                config.Port = port;
            }
            else if (args[i] == "--bind" && i + 1 < args.Length)
            {
                config.Bind = args[i + 1];
            }
        }

        return config;
    }
}
