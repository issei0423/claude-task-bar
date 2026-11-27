using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ClaudeTaskBar;

/// <summary>
/// One animation of the pet sheet: a horizontal strip PNG (32 dots per frame,
/// left to right) and how long each frame is shown. WPF-free on purpose so
/// CoreTests can check it.
/// </summary>
public sealed class PetAnimInfo
{
    public PetAnimInfo(string id, string file, IReadOnlyList<int> ms)
    {
        Id = id;
        File = file;
        Ms = ms;
        int total = 0;
        foreach (var m in ms) total += m;
        TotalMs = total;
    }

    public string Id { get; }
    public string File { get; }
    /// <summary>How long each frame is shown, in milliseconds.</summary>
    public IReadOnlyList<int> Ms { get; }
    public int TotalMs { get; }

    /// <summary>
    /// Frame index at time tMs. The animation loops: after one full cycle it
    /// starts over. Negative, NaN and infinite times give frame 0.
    /// </summary>
    public int FrameAt(double tMs)
    {
        if (double.IsNaN(tMs) || double.IsInfinity(tMs) || tMs < 0 || TotalMs <= 0) return 0;
        double t = tMs % TotalMs;
        double edge = 0;
        for (int i = 0; i < Ms.Count; i++)
        {
            edge += Ms[i];
            if (t < edge) return i;
        }
        return 0;
    }
}

/// <summary>The clickable box of a pet, in dots from the frame's top-left corner.</summary>
public readonly record struct PetHit(int X, int Y, int W, int H);

/// <summary>The pet sheet (app/assets/pet/pet.json): frame size, ground line, hit box, animations.</summary>
public sealed class PetSheet
{
    public static readonly string[] RequiredAnims = { "typing", "walk", "done", "ask", "idle" };

    public PetSheet(int frame, int ground, PetHit hit, IReadOnlyDictionary<string, PetAnimInfo> anims)
    {
        Frame = frame;
        Ground = ground;
        Hit = hit;
        Anims = anims;
    }

    /// <summary>Width and height of one frame, in dots.</summary>
    public int Frame { get; }
    /// <summary>The row just below the feet (the outline sits on this row), counted from the top.</summary>
    public int Ground { get; }
    /// <summary>The clickable box, in dots from the frame's top-left corner.</summary>
    public PetHit Hit { get; }
    public IReadOnlyDictionary<string, PetAnimInfo> Anims { get; }

    public static PetSheet Load(string path)
    {
        string json;
        try { json = File.ReadAllText(path); }
        catch (Exception ex)
        {
            throw new InvalidDataException("pet.json を読めません (" + path + "): " + ex.Message, ex);
        }
        try { return Parse(json); }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException(ex.Message + " (" + path + ")", ex);
        }
    }

    public static PetSheet Parse(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            throw new InvalidDataException("pet.json が壊れています: " + ex.Message, ex);
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("pet.json の最上位がオブジェクトではありません");

            int frame = PositiveInt(root, "frame", "frame");
            int ground = Int(root, "ground", "ground");
            if (ground < 0 || ground >= frame)
                throw new InvalidDataException("ground は 0 以上 frame 未満の整数が要ります: " + ground);

            if (!root.TryGetProperty("hit", out var hit) || hit.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("hit (押せる箱) がありません");
            int hx = Int(hit, "x", "hit.x");
            int hy = Int(hit, "y", "hit.y");
            int hw = PositiveInt(hit, "w", "hit.w");
            int hh = PositiveInt(hit, "h", "hit.h");
            if (hx < 0 || hy < 0 || hx + hw > frame || hy + hh > frame)
                throw new InvalidDataException("hit の箱が frame (" + frame + ") からはみ出しています");

            if (!root.TryGetProperty("anims", out var anims) || anims.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("anims がありません");

            var result = new Dictionary<string, PetAnimInfo>();
            foreach (var name in RequiredAnims)
            {
                if (!anims.TryGetProperty(name, out var a) || a.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("アニメ '" + name + "' がありません");

                if (!a.TryGetProperty("file", out var f) || f.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(f.GetString()))
                    throw new InvalidDataException("アニメ '" + name + "' の file がありません");
                var file = f.GetString()!;
                if (file.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || file == ".." || file == ".")
                    throw new InvalidDataException("アニメ '" + name + "' の file はフォルダを含まないファイル名にしてください: " + file);

                if (!a.TryGetProperty("ms", out var m) || m.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("アニメ '" + name + "' の ms (コマごとの長さ) がありません");
                var list = new List<int>();
                foreach (var e in m.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Number || !e.TryGetInt32(out var v) || v <= 0)
                        throw new InvalidDataException("アニメ '" + name + "' の ms は正の整数だけにしてください: " + e);
                    list.Add(v);
                }
                if (list.Count == 0)
                    throw new InvalidDataException("アニメ '" + name + "' の ms が空です");

                result[name] = new PetAnimInfo(name, file, list);
            }
            return new PetSheet(frame, ground, new PetHit(hx, hy, hw, hh), result);
        }
    }

    private static int Int(JsonElement obj, string key, string label)
    {
        if (!obj.TryGetProperty(key, out var p) || p.ValueKind != JsonValueKind.Number || !p.TryGetInt32(out var v))
            throw new InvalidDataException(label + " が整数ではありません");
        return v;
    }

    private static int PositiveInt(JsonElement obj, string key, string label)
    {
        int v = Int(obj, key, label);
        if (v <= 0) throw new InvalidDataException(label + " は正の整数が要ります: " + v);
        return v;
    }
}
