using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media.Imaging;

namespace ClaudeTaskBar;

/// <summary>Shared per-surface mascot image loading (was duplicated per window).</summary>
public static class MascotImages
{
    private static readonly Dictionary<string, BitmapImage?> _cache = new();

    public static BitmapImage? Load(string? surface)
    {
        string file = surface switch
        {
            "terminal" => "mascot-terminal.png",
            "ide" => "mascot-ide.png",
            "web" => "mascot-web.png",
            "chrome" => "mascot-chrome.png",
            "desktop" => "mascot-desktop.png",
            _ => "mascot.png",
        };
        if (_cache.TryGetValue(file, out var cached)) return cached;

        var path = Path.Combine(AppContext.BaseDirectory, "assets", file);
        if (!File.Exists(path)) path = Path.Combine(AppContext.BaseDirectory, "assets", "mascot.png");
        BitmapImage? result = null;
        if (File.Exists(path))
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(path);
                bmp.EndInit();
                result = bmp;
            }
            catch { result = null; }
        }
        _cache[file] = result;
        return result;
    }
}
