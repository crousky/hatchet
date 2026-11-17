using System.Diagnostics;
using System.Text.Json;
using Factory.Contracts;
using Factory.Engine.Llm;

namespace Factory.Web.Services;

/// <summary>Reads the model list and quota from the Antigravity CLI (<c>agy</c>), with a short cache.</summary>
public static class AntigravityCatalog
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FailedTtl = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(30);

    private static string? _cachedPath;
    private static DateTime _cachedAt = DateTime.MinValue;
    private static IReadOnlyList<(string Value, string Label)> _cachedModels = [];

    /// <summary>The models <c>agy models</c> lists. Empty when agy can't run.</summary>
    public static async Task<IReadOnlyList<(string Value, string Label)>> ModelsAsync(string? path, bool refresh = false)
    {
        var key = string.IsNullOrWhiteSpace(path) ? "agy" : path.Trim();
        var age = DateTime.UtcNow - _cachedAt;
        if (!refresh && _cachedPath == key && age < (_cachedModels.Count > 0 ? Ttl : FailedTtl)) return _cachedModels;

        var output = await RunAsync(key, ["models"]);
        var models = output is null ? [] : Parse(output);
        _cachedPath = key;
        _cachedAt = DateTime.UtcNow;
        _cachedModels = models;
        return models;
    }

    /// <summary>A one-line summary of the quota that <c>/usage</c> reports, or null when it can't be read.</summary>
    public static async Task<string?> QuotaAsync(string? path)
    {
        var key = string.IsNullOrWhiteSpace(path) ? "agy" : path.Trim();
        var output = await RunAsync(key, ["--output-format", "json", "-p=/usage"]);
        return output is null ? null : Summarize(output);
    }

    public static List<(string Value, string Label)> Parse(string output)
    {
        var models = new List<(string Value, string Label)>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim('\r', ' ');
            if (line.Length == 0) continue;
            string id, label;
            var tab = line.IndexOf('\t');
            if (tab > 0)
            {
                id = line[..tab].Trim();
                label = line[(tab + 1)..].Trim();
            }
            else
            {
                id = line;
                label = "";
            }
            if (!AntigravityOptions.IsValidModelId(id) || models.Any(m => m.Value == id)) continue;
            models.Add((id, label.Length > 0 ? label : Fmt.ModelLabel(id)));
        }
        return models;
    }

    public static string? Summarize(string output)
    {
        output = output.Trim();
        if (output.Length == 0) return null;
        try
        {
            if (AntigravityLlmClient.ParseUsage(output, "").Quota is { Length: > 0 } structured)
                return structured;
        }
        catch (JsonException)
        {
        }

        string? text = null;
        try
        {
            using var document = JsonDocument.Parse(output);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var name in new[] { "response", "result", "text", "output" })
                {
                    if (document.RootElement.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
                    {
                        text = property.GetString();
                        break;
                    }
                }
            }
        }
        catch (JsonException)
        {
        }
        text ??= output;
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);
        var joined = string.Join(" · ", lines);
        if (joined.Length == 0) return null;
        return joined.Length <= 240 ? joined : joined[..240] + "…";
    }

    private static IEnumerable<string> Candidates(string path)
    {
        yield return path;
        if (OperatingSystem.IsWindows() && string.Equals(path, "agy", StringComparison.OrdinalIgnoreCase))
        {
            var local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (!string.IsNullOrEmpty(local))
            {
                var installed = Path.Combine(local, "agy", "bin", "agy.exe");
                if (File.Exists(installed)) yield return installed;
            }
        }
    }

    private static async Task<string?> RunAsync(string path, string[] args)
    {
        foreach (var executable in Candidates(path))
        {
            try
            {
                var info = new ProcessStartInfo(executable)
                {
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                foreach (var arg in args) info.ArgumentList.Add(arg);
                using var process = Process.Start(info);
                if (process is null) continue;
                process.StandardInput.Close();
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                using var cts = new CancellationTokenSource(RunTimeout);
                try
                {
                    await process.WaitForExitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(true); } catch (InvalidOperationException) { }
                    return null;
                }
                var text = await stdout;
                await stderr;
                if (process.ExitCode != 0 && string.IsNullOrWhiteSpace(text)) return null;
                return text;
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }
        return null;
    }
}
