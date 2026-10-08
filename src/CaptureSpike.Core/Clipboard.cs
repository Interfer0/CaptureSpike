namespace CaptureSpike.Core;

/// <summary>
/// Seam for clipboard access so Copy-style commands are unit-testable
/// without bringing in any UI framework. The production implementation
/// lives in CaptureSpike.App and forwards to the active Avalonia TopLevel's
/// clipboard; tests supply a stub that records the text.
/// </summary>
public interface IClipboardWriter
{
    /// <summary>Asynchronously copy the given text to the system clipboard.</summary>
    Task SetTextAsync(string text);
}

/// <summary>
/// Helpers for snapshotting a list of log lines into a single string the
/// way the Copy button does. Lives in Core so it can be unit-tested without
/// the Avalonia stack.
/// </summary>
public static class LogText
{
    /// <summary>
    /// Join the given lines with <see cref="Environment.NewLine"/>.
    /// Empty input returns an empty string (never a stray newline).
    /// </summary>
    public static string Join(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        return string.Join(Environment.NewLine, lines);
    }
}
