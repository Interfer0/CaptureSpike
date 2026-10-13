# Windows window capture

## Status: skeleton, picker wired up, GPU surface copy pending

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
   - Create a D3D11 device, frame pool, and session.
4. **Frame copy** (`D3D11FrameReader`): each
   `FrameArrived` callback copies the GPU surface to
   a CPU buffer (via `ID3D11DeviceContext::CopyResource`
   + `Map`), writes the BGRA bytes to ffmpeg's stdin.
5. **ffmpeg** reads rawvideo from stdin and encodes
   with libx264.

### Migration from old gdigrab-based palette entries

If a palette has a Window source with a plain title
in `Device` (the pre-WGC format), `ResolveCaptureItem`
tries `FindWindowW(title)` to look up the current HWND.
If the window has been closed, it falls back to the
system `GraphicsCapturePicker` so the user can
re-select. The picker is a backstop, not the normal
flow.

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
  the capturer class, with the picker UI, frame pool
  setup, ffmpeg process management, and first-data
  detection all in place.
- `src/CaptureSpike.Core/Capture/WindowHandleProvider.cs` —
  static class that the UI layer uses to publish the
  main window's HWND. The capturer reads it to
  initialize the picker.
- `src/CaptureSpike.App/App.axaml.cs` — registers the
  main window's HWND with `WindowHandleProvider` on
  load, clears it on close.
- `src/CaptureSpike.Core/Capture/CaptureFactory.cs` —
  the `SourceKind.Window` case now constructs a
  `WindowsGraphicsCapturer` instead of building
  gdigrab ffmpeg args.
- Project file: no new PackageReference needed; the
  .NET 10 SDK's Windows targeting pack includes
  `Microsoft.Windows.SDK.NET.Ref` 10.0.19041.57 which
  contains the `Windows.Graphics.Capture` projection.

## What's left

The picker UI is fully wired up. The remaining piece
is the GPU-to-CPU surface copy that runs in the
`FrameArrived` callback. This needs:

1. **Create a Direct3D 11 device.** The standard
   WinRT call is `Direct3D11Helpers.CreateDevice()`
   from the `Microsoft.Graphics.Canvas` or
   `Win2D` packages, but the .NET 10 projection
   doesn't include it. The cleanest options are:
   - **Add `Silk.NET.Direct3D11`** (a small NuGet
     package, ~200 KB) and use it to call
     `D3D11CreateDevice` + `Direct3D11CreateDevice`
   - **Hand-roll a P/Invoke** of `D3D11CreateDevice`
     (about 30 lines of C#)

2. **CopyResource + Map.** The frame's surface is a
   GPU texture. We need:
   - `IDXGISurface.QueryInterface` for `ID3D11Texture2D`
   - `ID3D11Device.CreateTexture2D` with
     `D3D11_USAGE_STAGING` and
     `D3D11_CPU_ACCESS_READ` flags
   - `ID3D11DeviceContext.CopyResource(staging, gpuTex)`
   - `ID3D11DeviceContext.Map(staging, ...)` to get
     a CPU pointer to the pixels
   - Copy row-by-row (the GPU row pitch may differ
     from `width * 4` due to alignment) to a managed
     `byte[]`
   - Unmap, write to ffmpeg's stdin

This is ~50-100 lines of C# and P/Invoke. The
struct layouts and COM signatures are well-documented
in the Direct3D 11 headers and several open-source
samples (e.g. `leadedge/Win32CaptureSample` on GitHub).

## Integration test plan (run on Windows)

1. **Build:** `dotnet build src/CaptureSpike.App
   -f net10.0-windows10.0.19041` succeeds without
   errors.
2. **Run:** launch the app. Open a project, add a
   Window source, hit Record.
3. **Picker appears:** the system WGC picker should
   appear. Confirm it's modal to the app window.
4. **Click a window:** pick e.g. a browser window.
   Capture should start.
5. **Verify output:** the take folder should have a
   new .mkv file. Open it in VLC or Resolve.
   - Should show the picked window's content
   - Audio track (if any) should be in sync with the
     video (within ~100ms; the `zerolatency` ffmpeg
     tune and the 0.1s stats_period give us this)
6. **Edge cases to test:**
   - Minimize the window during recording: WGC
     should keep capturing; gdigrab would stop.
   - UWP / Edge window: should work; gdigrab would
     fail.
   - Cancel the picker: the capturer should throw
     `CaptureException` with a clear message and the
     take shouldn't be created.
   - Multiple windows of the same app: pick one, see
     that only that window is captured (not all).

## References

- [Microsoft Learn: Screen capture (WinUI 3 /
  Windows App SDK)](https://learn.microsoft.com/en-us/windows/apps/develop/media-authoring-processing/screen-capture)
- [`leadedge/Win32CaptureSample`](https://github.com/leadedge/Win32CaptureSample) — minimal
  C++ sample using WGC from a Win32 app; the closest
  reference for the GPU-to-CPU copy we're missing.
- [`dotnet/WPF/ScreenCapture`](https://github.com/microsoft/Windows.UI.Composition-Win32-Samples/blob/master/dotnet/WPF/ScreenCapture/) — older
  SharpDX-based sample; useful for the C# side of
  the picker / frame pool / session lifecycle.
