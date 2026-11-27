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
/// </summary>
public sealed class AppConfig
{
    public int Port { get; init; } = 8765;

    /// <summary>Optional full path to orca.exe; empty means "look in the usual places".</summary>
    public string OrcaPath { get; init; } = "";

    /// <summary>Screen pixels per pet dot (1-6). The pet sheet is 32 dots wide.</summary>
    public int PetScale { get; init; } = DefaultPetScale;

    public const int DefaultPetScale = 2;

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
            return new AppConfig { Port = port, OrcaPath = orcaPath, PetScale = petScale };
        }
        catch
        {
            return new AppConfig();
        }
    }
}
