using System.Text.Json;

namespace Scada.Core.Diagnostics;

/// <summary>把冒烟结果写成 JSON，供 scripts/verify.ps1 读取并判定。</summary>
public static class SmokeReport
{
    public static string Write(string appName, bool success, IReadOnlyDictionary<string, object?> details)
    {
        var path = Configuration.AppPaths.DataFile($"smoke-{Sanitize(appName)}.json");
        var payload = new Dictionary<string, object?>
        {
            ["app"] = appName,
            ["success"] = success,
            ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
        };
        foreach (var pair in details)
            payload[pair.Key] = pair.Value;

        File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    private static string Sanitize(string name)
        => string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-'));
}
