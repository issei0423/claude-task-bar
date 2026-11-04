using System;
using System.IO;
using System.Text.Json;

namespace ClaudeTaskBar;

/// <summary>
/// User settings from %LOCALAPPDATA%\claude-task-bar\config.json.
/// Missing or broken files fall back to defaults instead of crashing,
/// but values are no longer hardcoded across the app and the hook:
/// notify.ps1 reads the same file for the port.
/// Example: { "port": 8765 }
/// </summary>
public sealed class AppConfig
{
    public int Port { get; init; } = 8765;

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
            return new AppConfig { Port = port };
        }
        catch
        {
            return new AppConfig();
        }
    }
}
