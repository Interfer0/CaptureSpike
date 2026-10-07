namespace CaptureSpike.Core;

/// <summary>
/// Atomic file writes: write to a unique temp file in the same directory,
/// rename onto the destination with retry on transient locks (Windows AV,
/// OneDrive / Dropbox), and clean up the temp on failure.
///
/// Used by ProjectStore for project JSON, and by AppSettings for the user
/// settings JSON. Centralized so the retry policy stays consistent.
/// </summary>
public static class AtomicFile
{
    /// <summary>Writes <paramref name="content"/> to <paramref name="path"/> atomically.</summary>
    public static void WriteAllText(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // Unique temp name so concurrent writers (or a stale temp from a
        // crashed run) can never collide on the same path.
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(tmp, content);
        try
        {
            // Retry the rename: on Windows, antivirus and cloud sync
            // (OneDrive/Dropbox, common on the Documents folder) can
            // briefly lock the destination file.
            const int attempts = 6;
            for (var i = 0; ; i++)
            {
                try
                {
                    File.Move(tmp, path, overwrite: true);
                    return;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    if (i == attempts - 1) throw;
                    Thread.Sleep(100 * (i + 1));
                }
            }
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }
}