// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2024-2026 Daniel Bigelow

namespace CaptureSpike.App;

/// <summary>
/// Holds an exclusive lock for as long as one CaptureSpike process runs.
///
/// Two instances pointed at the same project folder overwrite each
/// other's writes. ProjectStore.WriteJsonAtomic retries File.Move six
/// times to survive transient AV/OneDrive locks, but that is for a
/// second process reading the file, not for two instances both
/// rewriting it from stale in-memory state - the last writer wins and
/// the other's edits vanish. palette.json is worse: it is shared across
/// projects and rewritten on every device change.
///
/// A second window is also actively confusing. A stale build left over
/// from earlier still looked identical and was the one being looked at,
/// which is exactly how time got lost once already.
///
/// The lock is an advisory fcntl lock on a file in the app config dir,
/// held by keeping the stream open for the process lifetime. .NET
/// implements FileStream.Lock with flock/fcntl on Unix, so it is visible
/// across processes, not just within this one.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private FileStream? _lockFile;
    private readonly string _path;

    private SingleInstance(FileStream lockFile, string path)
    {
        _lockFile = lockFile;
        _path = path;
    }

    /// <summary>
    /// Tries to become the only instance. Returns null when another one
    /// already holds the lock, which the caller should treat as
    /// "already running, do nothing".
    /// </summary>
    public static SingleInstance? TryAcquire()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CaptureSpike");
        string path;
        try
        {
            Directory.CreateDirectory(dir);
            path = Path.Combine(dir, "instance.lock");
        }
        catch
        {
            // Cannot even find a place to put the lock. Not worth
            // refusing to start over.
            return null;
        }

        FileStream fs;
        try
        {
            fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                                FileShare.ReadWrite);
        }
        catch
        {
            return null;
        }

        try
        {
            // Throws IOException when another process holds it.
            fs.Lock(0, 1);
        }
        catch (IOException)
        {
            fs.Dispose();
            return null;
        }
        catch
        {
            // Locking unsupported on this filesystem. Carry on rather
            // than blocking startup over a best-effort guard.
            return null;
        }

        // Record the pid purely as a hint for a human reading the file.
        try
        {
            fs.SetLength(0);
            using var w = new StreamWriter(fs, leaveOpen: true);
            w.WriteLine(Environment.ProcessId);
        }
        catch { /* informational only */ }

        return new SingleInstance(fs, path);
    }

    public void Dispose()
    {
        if (_lockFile is null) return;
        try { _lockFile.Unlock(0, 1); } catch { }
        try { _lockFile.Dispose(); } catch { }
        _lockFile = null;
        try { File.Delete(_path); } catch { }
    }
}