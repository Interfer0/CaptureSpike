using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace CaptureSpike.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        RestoreWindowBounds();
        // App icon (optional: only if the asset was synced).
        try
        {
            Icon = new WindowIcon(
                Avalonia.Platform.AssetLoader.Open(new Uri("avares://CaptureSpike.App/Assets/capturespike-icon.png")));
        }
        catch { /* default icon */ }

        // Live-update the markdown preview as the user edits. We use
        // TextChanged (not a binding) so we can re-parse the source
        // and assign the resulting block list to the preview without
        // round-tripping through the binding system.
        //
        // NotesBox / NotesPreview are x:Name-generated fields; they're
        // null until Avalonia resolves them (post-Opened). Don't touch
        // them from the constructor — wire in Opened.

        // Keyboard shortcuts for the markdown toolbar: Ctrl+B / Ctrl+I.
        // Bound at the Window level so they fire when the editor has focus.
        KeyBindings.Add(new KeyBinding
        {
            Gesture = new KeyGesture(Key.B, KeyModifiers.Control),
            Command = new RelayCommand(() => OnNotesBold(null, null!)),
        });
        KeyBindings.Add(new KeyBinding
        {
            Gesture = new KeyGesture(Key.I, KeyModifiers.Control),
            Command = new RelayCommand(() => OnNotesItalic(null, null!)),
        });

        Opened += (_, _) =>
        {
            if (NotesBox is { } notesBox)
            {
                notesBox.TextChanged += (_, _) => UpdateNotesPreview();
                UpdateNotesPreview();
            }
            EnsureOnScreen();
            if (DataContext is MainViewModel vm)
            {
                vm.StartupAsync();
                // Keep the log pinned to the newest line.
                var logBox = this.FindControl<ListBox>("LogListBox");
                if (logBox is not null)
                {
                    vm.LogLines.CollectionChanged += (_, e) =>
                    {
                        if (e.NewItems?.Count > 0)
                            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                                logBox.ScrollIntoView(e.NewItems[^1]!));
                    };
                }
            }
        };

        // Don't lose the take if the window is closed mid-recording:
        // stop gracefully (writes take.json, finalizes files) before closing.
        Closing += async (_, e) =>
        {
            if (DataContext is MainViewModel vm && vm.IsRecording)
            {
                e.Cancel = true;
                try { await vm.StopRecordingAsync(); }
                catch { /* Status already reports the failure */ }
                finally { Close(); }
            }
        };

        // Remember size, position, and maximized state for next launch.
        Closing += (_, _) =>
        {
            AppSettings.SaveWindowBounds(Position.X, Position.Y, Width, Height,
                WindowState == WindowState.Maximized);
        };
    }

    /// <summary>
    /// Restores the last window bounds. First run (nothing saved) starts
    /// maximized, as before.
    /// </summary>
    private void RestoreWindowBounds()
    {
        var b = AppSettings.LoadWindowBounds();
        if (b is null || b.Maximized)
        {
            WindowState = WindowState.Maximized;
            return;
        }
        if (b.Width > 100 && b.Height > 100)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Width = b.Width;
            Height = b.Height;
            Position = new PixelPoint((int)b.X, (int)b.Y);
        }
        else
        {
            WindowState = WindowState.Maximized;
        }
    }

    /// <summary>
    /// If the saved position is on a monitor that's no longer connected,
    /// center the window on the primary screen instead of opening off-screen.
    /// </summary>
    private void EnsureOnScreen()
    {
        if (WindowState != WindowState.Normal) return;
        var screen = Screens.ScreenFromPoint(Position);
        if (screen is not null) return;
        var primary = Screens.Primary;
        if (primary is null) return;
        var area = primary.WorkingArea;
        Position = new PixelPoint(
            area.X + Math.Max(0, (area.Width - (int)Width) / 2),
            area.Y + Math.Max(0, (area.Height - (int)Height) / 2));
    }

    private void OnSeekPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Seek lives in the tiled playback window's PlayerTransportBar
        // now; this handler stays here so the XAML compiles cleanly if
        // someone re-adds a slider to the main window.
        if (DataContext is MainViewModel vm) vm.BeginScrub();
    }

    private void OnSeekPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (DataContext is MainViewModel vm && sender is Slider slider)
            vm.EndScrub(slider.Value);
    }

    private void OnSeekGotFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.BeginScrub();
    }

    private void OnSeekLostFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && sender is Slider slider)
            vm.EndScrub(slider.Value);
    }

    // ----- Markdown toolbar for Script & notes -----

    /// <summary>Keyboard shortcuts: Ctrl+B (bold), Ctrl+I (italic).
    /// Wired in the constructor via KeyBindings.</summary>
    private void OnNotesBold(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        WrapSelectionInNotes("**", "**");
    private void OnNotesItalic(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        WrapSelectionInNotes("*", "*");
    private void OnNotesH1(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        PrefixEachLineInNotes("# ");
    private void OnNotesH2(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        PrefixEachLineInNotes("## ");
    private void OnNotesH3(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        PrefixEachLineInNotes("### ");
    private void OnNotesBullet(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        PrefixEachLineInNotes("- ");
    private void OnNotesNumbered(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        PrefixEachLineInNotes("1. ");

    /// <summary>
    /// Re-parse the editor's source string and assign the resulting
    /// blocks to the markdown preview. Called on every keystroke so
    /// the preview stays in sync with the source.
    /// </summary>
    private void UpdateNotesPreview()
    {
        var source = NotesBox.Text ?? string.Empty;
        var blocks = CaptureSpike.Core.Markdown.MarkdownParser.Parse(source);
        NotesPreview.Blocks = blocks;
    }

    /// <summary>
    /// Inserts <paramref name="prefix"/> before and <paramref name="suffix"/>
    /// after the current selection in the notes editor. With no selection,
    /// inserts just the markers and leaves the caret between them so the
    /// user can type the wrapped text. After the change, the entire wrapped
    /// run is selected so the user can keep applying formatting or type to
    /// replace it.
    /// </summary>
    private void WrapSelectionInNotes(string prefix, string suffix)
    {
        var text = NotesBox.Text ?? string.Empty;
        var start = Math.Clamp(NotesBox.SelectionStart, 0, text.Length);
        var end = Math.Clamp(NotesBox.SelectionEnd, 0, text.Length);
        if (end < start) (start, end) = (end, start);
        var selected = text.Substring(start, end - start);
        var inserted = prefix + selected + suffix;
        var newText = text.Remove(start, end - start).Insert(start, inserted);
        NotesBox.Text = newText;
        // Selection covers the inserted run; caret lands at the end of it.
        NotesBox.SelectionStart = start;
        NotesBox.SelectionEnd = start + inserted.Length;
        NotesBox.CaretIndex = start + inserted.Length;
        NotesBox.Focus();
    }

    /// <summary>
    /// Prepends <paramref name="prefix"/> at the start of every line in the
    /// current selection (or at the cursor's line if nothing is selected).
    /// Multi-line selections are common ("select three paragraphs, click
    /// H2 — all three become headings"). If the start of the selection sits
    /// mid-line, the prefix goes at the start of that line; if the end sits
    /// mid-line, the prefix is added up to and including that line.
    /// </summary>
    private void PrefixEachLineInNotes(string prefix)
    {
        var text = NotesBox.Text ?? string.Empty;
        var start = Math.Clamp(NotesBox.SelectionStart, 0, text.Length);
        var end = Math.Clamp(NotesBox.SelectionEnd, 0, text.Length);
        if (end < start) (start, end) = (end, start);

        // Expand to whole lines. Walk back from start to the previous \n
        // (exclusive) and forward from end to the next \n (inclusive).
        var lineStart = start;
        while (lineStart > 0 && text[lineStart - 1] != '\n') lineStart--;
        var lineEnd = end;
        while (lineEnd < text.Length && text[lineEnd] != '\n') lineEnd++;

        var block = text.Substring(lineStart, lineEnd - lineStart);
        var prefixedLines = block.Split('\n')
            .Select(line => line.Length == 0 ? line : prefix + line);
        var newBlock = string.Join('\n', prefixedLines);
        var newText = text.Remove(lineStart, lineEnd - lineStart).Insert(lineStart, newBlock);

        NotesBox.Text = newText;
        // Restore the selection over the prefixed block so the user can see
        // what changed and chain more edits.
        NotesBox.SelectionStart = lineStart;
        NotesBox.SelectionEnd = lineStart + newBlock.Length;
        NotesBox.CaretIndex = lineStart + newBlock.Length;
        NotesBox.Focus();
    }
}
