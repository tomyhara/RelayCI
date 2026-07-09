using System.Text.Json;

namespace CiRunner.Core.Pipeline;

/// <summary>One JSON-Lines event from the control file. Schema per ci-runner-dsl-spec.md §4.</summary>
public sealed class ControlFileEvent
{
    public required string Ev { get; init; }
    public required JsonElement Raw { get; init; }

    public string? GetString(string prop) =>
        Raw.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public int? GetInt(string prop) =>
        Raw.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    public JsonElement? GetArray(string prop) =>
        Raw.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Array ? v : null;

    public static ControlFileEvent? TryParse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement.Clone();
            if (!root.TryGetProperty("ev", out var evProp) || evProp.ValueKind != JsonValueKind.String)
            {
                return null;
            }
            return new ControlFileEvent { Ev = evProp.GetString()!, Raw = root };
        }
        catch (JsonException)
        {
            // Incomplete/partial line (flush race) - caller should retry once more bytes arrive.
            return null;
        }
    }
}
