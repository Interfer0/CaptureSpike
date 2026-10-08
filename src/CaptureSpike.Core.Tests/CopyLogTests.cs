using CaptureSpike.Core;
using Xunit;

namespace CaptureSpike.Core.Tests;

/// <summary>
/// Tests for the pure helpers behind the Copy button. The button's job is:
///   1. snapshot the log into a string (LogText.Join)
///   2. hand the string to an IClipboardWriter
/// Each step is verified independently so the VM plumbing doesn't drag the
/// Avalonia dispatcher into the test process.
/// </summary>
public sealed class CopyLogTests
{
    [Fact]
    public void Join_EmptyInput_ReturnsEmptyString_NoLeadingNewline()
    {
        var text = LogText.Join(System.Array.Empty<string>());

        Assert.Equal(string.Empty, text);
        Assert.False(text.StartsWith(System.Environment.NewLine));
    }

    [Fact]
    public void Join_SingleLine_ReturnsThatLine_NoTrailingNewline()
    {
        var text = LogText.Join(new[] { "only" });

        Assert.Equal("only", text);
        Assert.False(text.EndsWith(System.Environment.NewLine));
    }

    [Fact]
    public void Join_MultipleLines_AllPresentInOrder_NoTrailingNewline()
    {
        var lines = new[] { "first", "second", "third" };
        var text = LogText.Join(lines);

        var back = text.Split(System.Environment.NewLine);
        Assert.Equal(3, back.Length);
        Assert.Equal("first", back[0]);
        Assert.Equal("second", back[1]);
        Assert.Equal("third", back[2]);
        Assert.False(text.EndsWith(System.Environment.NewLine));
    }

    [Fact]
    public void Join_TimestampedEntries_PassThroughUnchanged()
    {
        // Log() prepends "HH:mm:ss " to each entry; the joiner must not
        // touch that prefix.
        var ts = new[]
        {
            "12:34:56 alpha",
            "12:34:57 beta",
            "12:34:58 gamma",
        };
        var text = LogText.Join(ts);

        Assert.Equal(
            "12:34:56 alpha" + System.Environment.NewLine
          + "12:34:57 beta"  + System.Environment.NewLine
          + "12:34:58 gamma",
            text);
    }

    [Fact]
    public void Join_NullInput_Throws()
    {
        Assert.Throws<System.ArgumentNullException>(
            () => LogText.Join(null!));
    }

    [Fact]
    public async Task ClipboardWriter_Stub_ReceivesExactlyWhatItIsGiven()
    {
        // Confirms the IClipboardWriter contract is consumable by tests:
        // whatever we pass to SetTextAsync comes out the other side verbatim.
        var clip = new RecordingClipboard();

        await clip.SetTextAsync("hello\nworld");

        Assert.Single(clip.Calls);
        Assert.Equal("hello\nworld", clip.Calls[0]);

        await clip.SetTextAsync("");
        Assert.Equal(2, clip.Calls.Count);
        Assert.Equal(string.Empty, clip.Calls[1]);
    }

    private sealed class RecordingClipboard : IClipboardWriter
    {
        public System.Collections.Generic.List<string> Calls { get; } = new();
        public Task SetTextAsync(string text)
        {
            Calls.Add(text);
            return Task.CompletedTask;
        }
    }
}
