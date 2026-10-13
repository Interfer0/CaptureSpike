# Windows window capture

## Status: implemented; Windows integration testing pending

This document describes the Windows-specific window
capture path using `Windows.Graphics.Capture` (WGC),
the modern Microsoft-recommended replacement for
gdigrab.

## Why WGC, not gdigrab

`gdigrab` (the old ffmpeg-based path) reads from GDI,
which has several limitations:

- **Can't see UWP / Edge / DirectX-only surfaces.**
  Apps that use composition surfaces or render via
  DirectX without GDI interop are invisible to
  gdigrab.
- **Fails on minimized windows.** The window has to be
  visible on screen for GDI to capture it.
- **Title matching is fragile.** If the user renames
  the window, the capture silently fails or captures
  the wrong window.

WGC reads the actual composited GPU output, works on
minimized windows, and uses a system picker UI to
let the user pick the window (no title guessing).

## Architecture

The user flow is the same as the old gdigrab path:
the user adds a `SourceKind.Window` source, picks a
window from the device dropdown (which is already
populated by `DeviceEnumeration.GetWindows()` with
all top-level visible windows), and hits Record. No
dialog appears at record time.

The internal flow:

1. **Selection** (in `DeviceEnumeration.GetWindows`):
   `EnumWindows` lists all top-level visible windows;
   for each, we record the HWND (in `Id`, prefixed
   `hwnd:0x...`) and the title (in `Label`, the
   dropdown's display text). Duplicate titles are
   disambiguated by appending the window class.
2. **Source stores the HWND** (via the existing
   `Source.Device` field, format `"hwnd:0xABCD"`).
3. **At record time** (`WindowsGraphicsCapturer`):
   - Parse the HWND from `source.Device`.
   - Call `GraphicsCaptureItem.CreateFromWindowHandle(hwnd)`
     via the `IGraphicsCaptureItemInterop` COM interface
     (the .NET WinRT projection doesn't expose this
     as a managed method).
   - Create a D3D11 device, free-threaded frame pool,
     and session. The free-threaded pool delivers frame
     events without requiring a WinRT dispatcher on the
     recording thread.
4. **Frame copy** (`D3D11FrameReader`): each
   `FrameArrived` callback copies the GPU surface to
   a CPU buffer (via `ID3D11DeviceContext::CopyResource`
   + `Map`). A fixed-rate writer feeds the latest BGRA
   frame to ffmpeg's stdin, repeating it when WGC hasn't
   delivered a newer frame. This keeps the video timeline
   aligned when frame delivery pauses, though the image
   will hold until WGC provides another frame.
5. **ffmpeg** reads rawvideo from stdin and encodes
   with libx264. Odd-sized windows are padded by at most one pixel on
   the right or bottom edge so the `yuv420p` output has even dimensions.

### Migration from old gdigrab-based palette entries

If a palette has a Window source with a plain title
in `Device` (the pre-WGC format), `ResolveCaptureItem`
tries `FindWindowW(title)` to look up the current HWND.
If the window has been closed, capture fails with an
instruction to re-select a window from the source
dropdown.

### Why not the system picker as the primary UX?

It interrupts the recording flow with a modal dialog
every time, even when the user knows exactly which
window they want. The dropdown keeps the window
selection persisted in the project (you don't have
to re-pick it next time), and the picker is opaque
about what's currently selected. The dropdown is
the same UX the app already uses for screens, mics,
and webcams.

## What's done in this commit

- `src/CaptureSpike.Core/Capture/Windows/WindowsGraphicsCapturer.cs` —
  the capturer class, with HWND resolution, frame pool
  setup, ffmpeg process management, and first-data
  detection all in place.
- `src/CaptureSpike.Core/Capture/CaptureFactory.cs` —
  the `SourceKind.Window` case now constructs a
  `WindowsGraphicsCapturer` instead of building
  gdigrab ffmpeg args.
- Project file: no new PackageReference needed; the
  .NET 10 SDK's Windows targeting pack includes
  `Microsoft.Windows.SDK.NET.Ref` 10.0.19041.57 which
  contains the `Windows.Graphics.Capture` projection.

## What's left

The D3D11 device setup and GPU-to-CPU frame copy are
implemented in `D3D11FrameReader`. Build and exercise
the capture path on Windows to validate driver/runtime
interop and verify the resulting video.

## Integration test plan (run on Windows)

1. **Build:** `dotnet build src/CaptureSpike.App
   -f net10.0-windows10.0.19041` succeeds without
   errors.
2. **Run:** launch the app. Open a project, add a
   Window source, hit Record.
3. **Start capture:** choose a window in the source
   device dropdown and start recording.
4. **Verify output:** the take folder should have a
   new .mkv file. Open it in VLC or Resolve.
   - Should show the picked window's content
   - Audio track (if any) should be in sync with the
     video (within ~100ms; the `zerolatency` ffmpeg
     tune and the 0.1s stats_period give us this)
5. **Edge cases to test:**
   - Unfocus, cover, and minimize the window during recording:
     verify output duration stays aligned. If WGC pauses updates,
     the output should hold the latest image until capture resumes.
   - UWP / Edge window: should work; gdigrab would
     fail.
   - Close a legacy title-based window source: the
     capturer should report that the window needs to
     be re-selected.
   - Multiple windows of the same app: pick one, see
     that only that window is captured (not all).

## References

- [Microsoft Learn: Screen capture (WinUI 3 /
  Windows App SDK)](https://learn.microsoft.com/en-us/windows/apps/develop/media-authoring-processing/screen-capture)
- [`leadedge/Win32CaptureSample`](https://github.com/leadedge/Win32CaptureSample) — minimal
  C++ sample using WGC from a Win32 app; the closest
  example of WGC capture from a Win32 app.
- [`dotnet/WPF/ScreenCapture`](https://github.com/microsoft/Windows.UI.Composition-Win32-Samples/blob/master/dotnet/WPF/ScreenCapture/) — older
  SharpDX-based sample; useful for the C# side of
  the   frame pool / session lifecycle.
