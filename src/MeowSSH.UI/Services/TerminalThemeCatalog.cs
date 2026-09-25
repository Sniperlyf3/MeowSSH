namespace MeowSSH.UI.Services;

/// <summary>
/// The terminal themes the UI can offer, mirroring the palettes (and the
/// premium set) in wwwroot/js/terminal.js. The JS side is authoritative for
/// what renders -- it applies the Pro gate -- so this list only drives
/// previews and pickers; a theme id added in one place must be added in both.
/// </summary>
public static class TerminalThemeCatalog
{
    public const string DefaultId = "meow-dark";
    public const string CustomId = "custom";

    public static IReadOnlyList<TerminalThemeOption> All { get; } =
    [
        new("meow-dark", "Meow Dark", "Deep graphite with MeowSSH coral accents.", false, "#0b0e13", "#e7ebf2", "#ff8a5b", "#f2555a", "#3fbf8f", "#e3b341", "#59a9ff", "#4fc4cf"),
        new("solarized-dark", "Solarized Dark", "Low-contrast classic for long terminal sessions.", false, "#002b36", "#839496", "#93a1a1", "#dc322f", "#859900", "#b58900", "#268bd2", "#2aa198"),
        new("solarized-light", "Solarized Light", "Warm light background with balanced contrast.", false, "#fdf6e3", "#657b83", "#586e75", "#dc322f", "#859900", "#b58900", "#268bd2", "#2aa198"),
        new("dracula", "Dracula", "High-contrast purple and cyan developer palette.", false, "#282a36", "#f8f8f2", "#f8f8f2", "#ff5555", "#50fa7b", "#f1fa8c", "#bd93f9", "#8be9fd"),
        new("nord", "Nord", "Cool arctic blues with soft contrast.", true, "#2e3440", "#d8dee9", "#d8dee9", "#bf616a", "#a3be8c", "#ebcb8b", "#81a1c1", "#88c0d0"),
        new("gruvbox-dark", "Gruvbox Dark", "Retro warm earth tones.", true, "#282828", "#ebdbb2", "#ebdbb2", "#cc241d", "#98971a", "#d79921", "#458588", "#689d6a"),
        new("one-dark", "One Dark", "The familiar editor palette, tuned for shells.", true, "#282c34", "#abb2bf", "#528bff", "#e06c75", "#98c379", "#e5c07b", "#61afef", "#56b6c2"),
        new("tokyo-night", "Tokyo Night", "Deep indigo with neon highlights.", true, "#1a1b26", "#c0caf5", "#c0caf5", "#f7768e", "#9ece6a", "#e0af68", "#7aa2f7", "#7dcfff"),
        new("production-red", "Production Red", "Unmistakable at a glance — made for per-host use on live systems.", true, "#2a0f12", "#f5e1e3", "#ff5c5c", "#ff5c5c", "#7fd18b", "#f2c66d", "#7aa9ff", "#6fd6d6"),
    ];

    public static TerminalThemeOption? Find(string? id) =>
        All.FirstOrDefault(option => string.Equals(option.Id, id, StringComparison.Ordinal));

    /// <summary>The custom theme is premium too, but has no fixed palette to list.</summary>
    public static bool IsPremium(string? id) =>
        string.Equals(id, CustomId, StringComparison.Ordinal) || Find(id)?.IsPremium == true;

    /// <summary>Label for a stored id, including "Custom" and unknown ids from a newer build.</summary>
    public static string NameOf(string? id) =>
        string.Equals(id, CustomId, StringComparison.Ordinal) ? "Custom" : Find(id)?.Name ?? "Meow Dark";
}

public sealed record TerminalThemeOption(
    string Id, string Name, string Description, bool IsPremium,
    string Background, string Foreground, string Cursor,
    string Red, string Green, string Yellow, string Blue, string Cyan);
