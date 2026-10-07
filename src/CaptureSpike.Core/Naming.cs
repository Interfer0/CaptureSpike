using System.Text;

namespace CaptureSpike.Core;

/// <summary>
/// Resolve-friendly file and folder naming: everything on disk is derived
/// from the names shown in the UI (project / scene / take / source), so the
/// project folder is browsable and imports cleanly.
/// </summary>
public static class Naming
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Makes a string safe as a file or folder name: invalid characters
    /// become '-', runs collapse, ends are trimmed, length is capped.
    /// Uses the Windows-incompatible set explicitly (not
    /// Path.GetInvalidFileNameChars, which is platform-dependent) so
    /// projects move cleanly between Windows and Linux.
    /// </summary>
    public static string SanitizeFileName(string name, int maxLength = 60)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Untitled";
        var sb = new StringBuilder(name.Trim());
        for (var i = 0; i < sb.Length; i++)
        {
            var c = sb[i];
            if (c < 32 || c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*')
                sb[i] = '-';
        }
        var s = sb.ToString();
        while (s.Contains("--", StringComparison.Ordinal)) s = s.Replace("--", "-");
        s = s.Trim('-', ' ', '.');
        if (s.Length > maxLength) s = s[..maxLength].TrimEnd('-', ' ', '.');
        if (string.IsNullOrEmpty(s) || ReservedNames.Contains(s)) return "Untitled";
        return s;
    }

    /// <summary>
    /// Returns <paramref name="want"/> when free, otherwise "want 2",
    /// "want 3", ... — so duplicate titles never collide on disk.
    /// </summary>
    public static string UniqueName(string want, Func<string, bool> exists)
    {
        if (!exists(want)) return want;
        for (var n = 2; ; n++)
        {
            var candidate = $"{want} {n}";
            if (!exists(candidate)) return candidate;
        }
    }
}
