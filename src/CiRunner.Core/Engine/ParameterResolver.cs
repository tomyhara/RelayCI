using System.Text.Json;
using CiRunner.Core.Models;

namespace CiRunner.Core.Engine;

/// <summary>Validates requested parameters against a job's declared definitions (spec §5 F1a).</summary>
public static class ParameterResolver
{
    public sealed record Result(bool Success, string ParametersJson, string? Error);

    public static Result Resolve(string parametersDefJson, IReadOnlyDictionary<string, string>? requested)
    {
        List<JobParameterDef> defs;
        try
        {
            defs = JsonSerializer.Deserialize<List<JobParameterDef>>(parametersDefJson) ?? new();
        }
        catch (JsonException)
        {
            defs = new();
        }

        requested ??= new Dictionary<string, string>();
        var resolved = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var def in defs)
        {
            if (requested.TryGetValue(def.Name, out var value))
            {
                resolved[def.Name] = value;
            }
            else if (def.Required)
            {
                return new Result(false, "{}", $"missing required parameter '{def.Name}'");
            }
            else if (def.Default is not null)
            {
                resolved[def.Name] = def.Default;
            }
        }

        // Undeclared names in `requested` are silently dropped (spec: "未宣言の名前は warning 付きで無視").
        return new Result(true, JsonSerializer.Serialize(resolved), null);
    }

    /// <summary>CI_-prefixed parameter names are reserved (spec §5 F1a) and rejected at job-definition time.</summary>
    public static bool IsReservedName(string name) => name.StartsWith("CI_", StringComparison.Ordinal);
}
