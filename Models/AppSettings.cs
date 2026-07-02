using System.Drawing;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WordLinkSolver.Models;

public enum WordPosition { Top, Middle, Bottom }

public enum WordSize { Small, Medium, Large }

public enum RefreshSpeed { Fast, Normal, Slow, Manual }

/// <summary>Persisted window placement (kept JSON-friendly on purpose).</summary>
public sealed class SavedBounds
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    public Rectangle ToRectangle() => new(X, Y, Width, Height);

    public static SavedBounds From(Rectangle r) =>
        new() { X = r.X, Y = r.Y, Width = r.Width, Height = r.Height };
}

/// <summary>
/// User settings, persisted as human-readable JSON in
/// %AppData%\WordLinkSolver\settings.json.
/// </summary>
public sealed class AppSettings
{
    // ----- Appearance -----
    public string LineGradientStart { get; set; } = "#00FFB0";
    public string LineGradientEnd { get; set; } = "#7B61FF";
    public string WordTextColor { get; set; } = "#00FFB0";
    public WordPosition WordPosition { get; set; } = WordPosition.Middle;
    public WordSize WordSize { get; set; } = WordSize.Medium;
    public bool ShowTextBackground { get; set; } = true;
    public bool ShowWordText { get; set; } = true;

    // ----- Performance -----
    public RefreshSpeed RefreshSpeed { get; set; } = RefreshSpeed.Normal;

    // ----- Solver -----
    public int MinWordLength { get; set; } = 3;

    // ----- Window state -----
    public SavedBounds? MainWindowBounds { get; set; }

    /// <summary>Refresh interval in milliseconds; 0 means Manual (scan on F only).</summary>
    [JsonIgnore]
    public int RefreshMs => RefreshSpeed switch
    {
        RefreshSpeed.Fast => 50,
        RefreshSpeed.Normal => 100,
        RefreshSpeed.Slow => 250,
        _ => 0,
    };

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    // ----- Persistence -----

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string SettingsPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WordLinkSolver", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (loaded is not null)
                    return loaded;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"Settings load failed, using defaults: {ex.Message}");
        }
        return new AppSettings();
    }

    public void Save()
    {
        var dir = System.IO.Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
    }
}

/// <summary>Hex color helpers shared by the renderer and the settings UI.</summary>
public static class ColorUtil
{
    /// <summary>Parses "#RRGGBB" (or "#AARRGGBB"); falls back to <paramref name="fallback"/> on bad input.</summary>
    public static Color Parse(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return fallback;
        var s = hex.Trim().TrimStart('#');
        try
        {
            if (s.Length == 6)
            {
                return Color.FromArgb(
                    255,
                    Convert.ToInt32(s[..2], 16),
                    Convert.ToInt32(s[2..4], 16),
                    Convert.ToInt32(s[4..6], 16));
            }
            if (s.Length == 8)
            {
                return Color.FromArgb(
                    Convert.ToInt32(s[..2], 16),
                    Convert.ToInt32(s[2..4], 16),
                    Convert.ToInt32(s[4..6], 16),
                    Convert.ToInt32(s[6..8], 16));
            }
        }
        catch
        {
            // fall through
        }
        return fallback;
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>Linear interpolation between two colors, t in [0,1].</summary>
    public static Color Lerp(Color a, Color b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return Color.FromArgb(
            (int)(a.A + (b.A - a.A) * t),
            (int)(a.R + (b.R - a.R) * t),
            (int)(a.G + (b.G - a.G) * t),
            (int)(a.B + (b.B - a.B) * t));
    }
}
