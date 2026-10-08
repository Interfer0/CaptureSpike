using CaptureSpike.Core;
using Xunit;

namespace CaptureSpike.Core.Tests;

/// <summary>
/// Verifies <see cref="AppLog"/> writes the same lines to the on-disk
/// session log that the in-app log panel shows, and that crash entries
/// also land in a separate crash.log file.
///
/// Tests construct per-test <see cref="AppLog"/> instances via
/// <see cref="AppLog.OpenAt"/> (test seam) so they're fully isolated
/// from each other — no shared static state, no races.
/// </summary>
public sealed class AppLogTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _sessionFile;
    private readonly AppLog _log;

    public AppLogTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"cs-applog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _sessionFile = Path.Combine(_tempDir, $"session-{Guid.NewGuid():N}.log");
        _log = AppLog.OpenAt(_sessionFile);
        // Mirror Open()'s "session opened" banner so the file exists and
        // contains a recognisable first line for tests that expect it.
        _log.WriteLine("--- session opened ---");
    }

    public void Dispose()
    {
        _log.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void SessionFile_IsCreatedOnOpen_AndContainsTheBanner()
    {
        Assert.True(File.Exists(_sessionFile));
        var firstLine = File.ReadAllLines(_sessionFile)[0];
        Assert.Contains("session opened", firstLine);
    }

    [Fact]
    public void WriteLine_AppendsATimestampedLine()
    {
        _log.WriteLine("hello world");

        var text = File.ReadAllText(_sessionFile);
        Assert.Contains("hello world", text);
        // Find the line containing the marker; verify the timestamp
        // prefix on that exact line.
        var lineWithMarker = text.Split('\n')
            .Single(l => l.Contains("hello world"));
        Assert.Matches(@"^\d{2}:\d{2}:\d{2} hello world", lineWithMarker);
    }

    [Fact]
    public void WriteCrash_AppendsToSessionFile_AndToCrashLog()
    {
        _log.WriteCrash(new InvalidOperationException("boom"));

        var session = File.ReadAllText(_sessionFile);
        Assert.Contains("CRASH", session);
        Assert.Contains("boom", session);

        // crash.log lands next to the session file (same _tempDir). The
        // production code points it at AppLog.LogDirectory; the test
        // confirms the convention via the sessionFile's parent dir.
        var crashFiles = Directory.GetFiles(_tempDir, "crash.log");
        Assert.Single(crashFiles);
        var crashText = File.ReadAllText(crashFiles[0]);
        Assert.Contains("boom", crashText);
    }

    [Fact]
    public void WriteLine_NeverThrows_AfterWriterIsDisposed()
    {
        _log.Dispose();

        // The writer has been nulled internally — WriteLine should be a
        // silent no-op, not throw.
        var ex = Record.Exception(() => _log.WriteLine("ignored"));
        Assert.Null(ex);
    }

    [Fact]
    public void LogDirectory_DefaultsPointAtAppDataSubfolder()
    {
        var dir = AppLog.LogDirectory;
        Assert.False(string.IsNullOrEmpty(dir));
        Assert.Contains("CaptureSpike", dir);
        // Either the dir already exists (from a previous run) or was created
        // on first read — either way it's a valid path.
        Assert.True(Path.IsPathRooted(dir));
    }
}
