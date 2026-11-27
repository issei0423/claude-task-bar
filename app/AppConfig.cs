using System;
using System.IO;
using System.Text.Json;

namespace ClaudeTaskBar;

/// <summary>
/// User settings from %LOCALAPPDATA%\claude-task-bar\config.json.
/// Missing or broken files fall back to defaults instead of crashing,
/// but values are no longer hardcoded across the app and the hook:
/// notify.ps1 reads the same file for the port.
/// Example: { "port": 8765, "orcaPath": "D:\\Tools\\orca\\resources\\bin\\orca.exe", "petScale": 2 }
/// Fleeing (5-hour usage high): "usageFile", "fleeThreshold" (0-100, default 90), "fleeEnabled" (default true).
/// </summary>
public sealed class AppConfig
{
    public int Port { get; init; } = 8765;

    /// <summary>Optional full path to orca.exe; empty means "look in the usual places".</summary>
    public string OrcaPath { get; init; } = "";

    /// <summary>Screen pixels per pet dot (1-6). The pet sheet is 32 dots wide.</summary>
    public int PetScale { get; init; } = DefaultPetScale;

    public const int DefaultPetScale = 2;

    /// <summary>
    /// Where the 5-hour usage is read from (the statusline's file). Missing = the
    /// pets never flee. A leading "~" or "%VAR%" is expanded.
    /// </summary>
    public string UsageFile { get; init; } = UsageReader.DefaultPath();

    /// <summary>The pets flee when the 5-hour usage reaches this percent (0-100).</summary>
    public double FleeThreshold { get; init; } = DefaultFleeThreshold;

    public const double DefaultFleeThreshold = 90;

    /// <summary>False turns the fleeing off.</summary>
    public bool FleeEnabled { get; init; } = true;

    /// <summary>"~" at the start means the user's folder; %VAR% is expanded.</summary>
    public static string ExpandPath(string path)
    {
        path = Environment.ExpandEnvironmentVariables(path);
        if (path == "~" || path.StartsWith("~/") || path.StartsWith("~\\"))
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Length > 2 ? path.Substring(2) : "");
        return path;
    }

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "claude-task-bar", "config.json");

    public static AppConfig Load() => Load(DefaultPath());

    public static AppConfig Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new AppConfig();
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            int port = 8765;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("port", out var p) &&
                p.ValueKind == JsonValueKind.Number &&
                p.TryGetInt32(out var v) && v >= 1 && v <= 65535)
            {
                port = v;
            }
            string orcaPath = "";
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("orcaPath", out var o) &&
                o.ValueKind == JsonValueKind.String)
            {
                orcaPath = o.GetString() ?? "";
            }
            int petScale = DefaultPetScale;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("petScale", out var ps) &&
                ps.ValueKind == JsonValueKind.Number &&
                ps.TryGetInt32(out var sv) && sv >= 1 && sv <= 6)
            {
                petScale = sv;
            }
            string usageFile = UsageReader.DefaultPath();
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("usageFile", out var uf) &&
                uf.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(uf.GetString()))
            {
                usageFile = ExpandPath(uf.GetString()!.Trim());
            }
            double fleeThreshold = DefaultFleeThreshold;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("fleeThreshold", out var ft) &&
                ft.ValueKind == JsonValueKind.Number &&
                ft.TryGetDouble(out var fv) && fv >= 0 && fv <= 100)
            {
                fleeThreshold = fv;
            }
            bool fleeEnabled = true;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("fleeEnabled", out var fe) &&
                (fe.ValueKind == JsonValueKind.True || fe.ValueKind == JsonValueKind.False))
            {
                fleeEnabled = fe.GetBoolean();
            }
            return new AppConfig
            {
                Port = port, OrcaPath = orcaPath, PetScale = petScale,
                UsageFile = usageFile, FleeThreshold = fleeThreshold, FleeEnabled = fleeEnabled,
            };
        }
        catch
        {
            return new AppConfig();
        }
    }
}
