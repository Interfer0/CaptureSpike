// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2024-2026 Daniel Bigelow

#if WINDOWS
using System;
using System.Runtime.InteropServices;
using global::Windows.Graphics.DirectX;
using global::Windows.Graphics.DirectX.Direct3D11;
using CaptureSpike.Core;
#endif

namespace CaptureSpike.Core.Capture.Windows;

#if WINDOWS
/// <summary>
/// Wraps the D3D11 surface-to-CPU copy that
/// <c>WindowsGraphicsCapturer</c> needs to read each
/// captured frame as BGRA bytes.
///
/// The .NET 10 WinRT projection for
/// <c>Windows.Graphics.DirectX.Direct3D11</c> gives us
/// the WinRT handle types (<c>IDirect3DDevice</c>,
/// <c>IDirect3DSurface</c>) but not the underlying D3D11
/// COM interfaces. To call D3D11 APIs (CreateTexture2D,
/// CopyResource, Map) we have to bridge WinRT → COM.
/// The standard pattern is the
/// <c>IDirect3DDxgiInterfaceAccess</c> interface,
/// which the WinRT types also implement.
///
/// Approach:
///   1. From the WinRT <c>IDirect3DDevice</c> handle we
///      get the underlying <c>ID3D11Device*</c>.
///   2. For each frame, get the underlying
///      <c>ID3D11Texture2D*</c> from the frame's surface.
///   3. Create a staging texture (CPU-readable) once
///      and reuse it across frames; re-allocate when
///      the source size changes.
///   4. <c>CopyResource</c> from the GPU texture to
///      the staging texture.
///   5. <c>Map</c> the staging texture, copy the rows
///      to a managed <c>byte[]</c> (the GPU row pitch
///      can be larger than width*4 due to alignment,
///      so we copy row-by-row), <c>Unmap</c>.
///
/// All D3D11 calls are raw P/Invoke — no Silk.NET or
/// other binding library. The COM GUIDs and vtable
/// layouts are stable (they're in d3d11.dll which has
/// had a stable ABI since Windows 7).
/// </summary>
internal sealed class D3D11FrameReader : IDisposable
{
    // IIDs for the D3D11 interfaces we QueryInterface for.
    // These are stable; defined in d3d11.h in the Windows SDK.
    private static readonly Guid IID_ID3D11Device =
        new("db6f6ddb-ac77-4e88-8253-819df9bbf140");
    private static readonly Guid IID_ID3D11Texture2D =
        new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    // The IDirect3DDxgiInterfaceAccess COM interface.
    // Implemented by IDirect3DDevice and IDirect3DSurface.
    // Its GetInterface(IID) returns the underlying DXGI
    // pointer (IDXGIDevice*, IDXGISurface*, etc.).
    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface(ref Guid iid);
    }

    // ID3D11Device vtable (only the methods we use).
    [ComImport]
    [Guid("db6f6ddb-ac77-4e88-8253-819df9bbf140")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ID3D11Device
    {
        // The full vtable is ~40 methods. We only declare
        // the ones we need; the COM runtime will treat
        // the rest as "not implemented" if called. To
        // call GetImmediateContext and CreateTexture2D
        // we need slots 7 and 24 respectively (zero-indexed
        // from IUnknown's 3 methods). The .NET COM
        // interop uses the vtable order from this
        // interface declaration, so we list methods in
        // vtable order up to what we need.
        void Reserved0(); void Reserved1(); void Reserved2();
        void Reserved3(); void Reserved4(); void Reserved5();
        void Reserved6();
        void GetImmediateContext(out IntPtr context);  // slot 7
        void Reserved8(); void Reserved9(); void Reserved10();
        void Reserved11(); void Reserved12(); void Reserved13();
        void Reserved14(); void Reserved15(); void Reserved16();
        void Reserved17(); void Reserved18(); void Reserved19();
        void Reserved20(); void Reserved21(); void Reserved22();
        void Reserved23();
        int CreateTexture2D(                                // slot 24; returns HRESULT
            ref D3D11_TEXTURE2D_DESC desc,
            IntPtr initialData,           // D3D11_SUBRESOURCE_DATA*
            IntPtr reserved,               // we don't use this
            out IntPtr texture);
    }

    [ComImport]
    [Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ID3D11Texture2D
    {
        void Reserved0(); void Reserved1(); void Reserved2();
        void Reserved3(); void Reserved4(); void Reserved5();
        void Reserved6(); void Reserved7(); void Reserved8();
        void Reserved9(); void Reserved10(); void Reserved11();
        void Reserved12();
        void GetDevice(out IntPtr device);  // slot 12
    }

    // ID3D11DeviceContext vtable. The full interface is
    // enormous (~60 methods). We declare the methods we
    // call; the COM runtime treats undeclared vtable
    // slots as "not callable from C#", which is what we
    // want. The slot numbers matter for vtable layout:
    // IUnknown is slots 0-2 (QueryInterface/AddRef/Release),
    // so the first device-context-specific method is
    // slot 3. Map is at slot 11, Unmap at 12, CopyResource
    // at slot 47 per d3d11.h.
    [ComImport]
    [Guid("c0bfa3ca-3ab3-48fc-b1f0-43fc1a3f03f3")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ID3D11DeviceContext
    {
        void Reserved0(); void Reserved1(); void Reserved2();
        void Reserved3(); void Reserved4(); void Reserved5();
        void Reserved6(); void Reserved7(); void Reserved8();
        void Reserved9(); void Reserved10();
        int Map(                                            // slot 11
            IntPtr resource,
            uint subresource,
            uint mapType,
            uint mapFlags,
            ref MAPPED_SUBRESOURCE mapped);
        void Unmap(IntPtr resource, uint subresource);   // slot 12
        // ... up to slot 46 reserved
        void CopyResource(IntPtr dst, IntPtr src);  // slot 47
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11_TEXTURE2D_DESC
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public uint Format;          // DXGI_FORMAT; 87 = B8G8R8A8_UNORM
        public uint SampleDesc_Count;
        public uint SampleDesc_Quality;
        public uint Usage;            // 3 = Staging
        public uint BindFlags;        // 0 for staging
        public uint CPUAccessFlags;   // 1 = Read
        public uint MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MAPPED_SUBRESOURCE
    {
        public IntPtr PData;          // void*
        public uint RowPitch;          // bytes per row, including padding
        public uint DepthPitch;        // bytes per slice (3D textures)
    }

    private const uint D3D11_USAGE_STAGING = 3;
    private const uint D3D11_CPU_ACCESS_READ = 1;
    private const uint DXGI_FORMAT_B8G8R8A8_UNORM = 87;
    private const uint D3D11_MAP_READ = 1;

    // IID for the IDXGIDevice COM interface. Used to
    // bridge D3D11CreateDevice's output to the WinRT
    // IDirect3DDevice via CreateDirect3D11DeviceFromDXGIDevice.
    private static readonly Guid IID_IDXGIDevice =
        new("54ec77fa-1377-44e6-8c92-79eb5ba7c092");

    // D3D11CreateDevice from d3d11.dll. DriverType=Hardware
    // (1), Flags=0, FeatureLevels=11_0, FeatureLevelsCount=1.
    // The returned ID3D11Device* is what we feed into
    // CreateDirect3D11DeviceFromDXGIDevice to get the
    // WinRT IDirect3DDevice the frame pool needs.
    [DllImport("d3d11.dll", EntryPoint = "D3D11CreateDevice", CallingConvention = CallingConvention.StdCall)]
    private static extern int D3D11CreateDevice(
        IntPtr adapter,         // null = default adapter
        uint driverType,        // 1 = Hardware
        IntPtr software,         // null
        uint flags,              // 0
        IntPtr featureLevels,    // IntPtr to D3D_FEATURE_LEVEL[]
        uint featureLevelCount,
        uint sdkVersion,
        out IntPtr deviceOut);

    // d3d11.dll's CreateDirect3D11DeviceFromDXGIDevice.
    // Returns an IInspectable* (which is then
    // Marshal.GetObjectForIUnknown'd to IDirect3DDevice).
    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", CallingConvention = CallingConvention.StdCall)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(
        IntPtr dxgiDevice,
        out IntPtr graphicsDevice);

    // IUnknown::QueryInterface (slot 0 of the IUnknown
    // vtable). We use this to get IDXGIDevice from the
    // ID3D11Device* returned by D3D11CreateDevice, then
    // feed that into CreateDirect3D11DeviceFromDXGIDevice.
    [ComImport]
    [Guid("00000000-0000-0000-c000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUnknown
    {
        IntPtr QueryInterface(ref Guid iid);
        IntPtr AddRef();
        IntPtr Release();
    }

    private IntPtr _device;
    private IntPtr _context;
    private IntPtr _stagingTexture;
    private int _stagingWidth;
    private int _stagingHeight;

    public D3D11FrameReader(IDirect3DDevice device)
    {
        _device = GetDXGIInterface(device, IID_ID3D11Device);
        var dev = (ID3D11Device)Marshal.GetObjectForIUnknown(_device);
        dev.GetImmediateContext(out _context);
    }

    /// <summary>
    /// Create a WinRT <see cref="IDirect3DDevice"/> that
    /// wraps a fresh D3D11 hardware device. Used to
    /// create the device that the WGC frame pool needs;
    /// the underlying ID3D11Device* is also kept by the
    /// <see cref="D3D11FrameReader"/> for surface copies.
    /// </summary>
    public static IDirect3DDevice CreateWinRtDevice(out IntPtr d3d11Device)
    {
        AppLog.Current?.WriteLine("[cs] WGC: D3D11FrameReader.CreateWinRtDevice begin");
        // D3D11CreateDevice wants a D3D_FEATURE_LEVEL[]. We
        // use 11.0 (the only one we need).
        var featureLevels = new uint[] { 0xb000 /* D3D_FEATURE_LEVEL_11_0 = 0xb000 */ };
        var flPtr = Marshal.AllocHGlobal(sizeof(uint) * featureLevels.Length);
        try
        {
            // Marshal.Copy only takes byte[] / short[] /
            // int[] for primitive overloads, not uint[].
            // Use MemoryMarshal to reinterpret the span.
            var flSpan = System.Runtime.InteropServices.MemoryMarshal
                .AsBytes(featureLevels.AsSpan());
            for (var i = 0; i < flSpan.Length; i++)
                Marshal.WriteByte(flPtr, i, flSpan[i]);
            AppLog.Current?.WriteLine(
                "[cs] WGC: D3D11FrameReader calling D3D11CreateDevice...");
            var hr = D3D11CreateDevice(
                IntPtr.Zero,    // default adapter
                1,              // D3D_DRIVER_TYPE_HARDWARE
                IntPtr.Zero,    // no software fallback module
                0,              // no flags
                flPtr,
                (uint)featureLevels.Length,
                7,              // D3D11_SDK_VERSION
                out var d3dDevicePtr);
            AppLog.Current?.WriteLine(
                $"[cs] WGC: D3D11CreateDevice returned hr=0x{hr:X8}, d3dDevicePtr={d3dDevicePtr:X}");
            if (hr < 0) throw new InvalidOperationException(
                $"D3D11CreateDevice failed (hr=0x{hr:X8}).");

            // Query IDXGIDevice from the ID3D11Device. The
            // IID is a struct (Guid) and we need a writable
            // local because QueryInterface takes `ref Guid`.
            var iid = IID_IDXGIDevice;
            var unknown = (IUnknown)Marshal.GetObjectForIUnknown(d3dDevicePtr);
            AppLog.Current?.WriteLine(
                $"[cs] WGC: ID3D11Device::QueryInterface(IDXGIDevice)...");
            var dxgiPtr = unknown.QueryInterface(ref iid);
            AppLog.Current?.WriteLine(
                $"[cs] WGC: IDXGIDevice pointer = {dxgiPtr:X}");
            try
            {
                if (dxgiPtr == IntPtr.Zero) throw new InvalidOperationException(
                    "ID3D11Device did not expose IDXGIDevice.");

                // Wrap the IDXGIDevice in a WinRT IDirect3DDevice.
                AppLog.Current?.WriteLine(
                    "[cs] WGC: CreateDirect3D11DeviceFromDXGIDevice...");
                var hr2 = CreateDirect3D11DeviceFromDXGIDevice(dxgiPtr, out var inspectablePtr);
                AppLog.Current?.WriteLine(
                    $"[cs] WGC: CreateDirect3D11DeviceFromDXGIDevice returned hr=0x{hr2:X8}, inspectablePtr={inspectablePtr:X}");
                if (hr2 < 0) throw new InvalidOperationException(
                    $"CreateDirect3D11DeviceFromDXGIDevice failed (hr=0x{hr2:X8}).");

                d3d11Device = d3dDevicePtr;
                AppLog.Current?.WriteLine(
                    "[cs] WGC: IDirect3DDevice wrapping succeeded");
                return (IDirect3DDevice)Marshal.GetObjectForIUnknown(inspectablePtr);
            }
            finally
            {
                if (dxgiPtr != IntPtr.Zero) Marshal.Release(dxgiPtr);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(flPtr);
        }
    }

    /// <summary>
    /// Read the given capture frame's surface as a BGRA
    /// byte array. Returns (pixels, contentWidth,
    /// contentHeight).
    /// </summary>
    public (byte[] Pixels, int Width, int Height) ReadFrame(
        global::Windows.Graphics.Capture.Direct3D11CaptureFrame frame)
    {
        var contentSize = frame.ContentSize;  // property, not method
        var w = contentSize.Width;
        var h = contentSize.Height;
        if (w <= 0 || h <= 0)
            throw new InvalidOperationException(
                $"WGC reported invalid content size {w}x{h}.");

        EnsureStagingTexture(w, h);

        // Get the underlying ID3D11Texture2D from the
        // frame's IDirect3DSurface.
        var surfacePtr = GetDXGIInterface(frame.Surface, IID_ID3D11Texture2D);  // property, not method
        try
        {
            // CopyResource(staging, gpuTexture). The
            // staging texture is a copy of the same size
            // so the copy is straightforward.
            var ctx = (ID3D11DeviceContext)Marshal.GetObjectForIUnknown(_context);
            ctx.CopyResource(_stagingTexture, surfacePtr);
        }
        finally
        {
            Marshal.Release(surfacePtr);
        }

        // Map the staging texture, copy rows, unmap.
        var bytesPerPixel = 4; // BGRA
        var tightStride = w * bytesPerPixel;
        var buffer = new byte[tightStride * h];
        var mapped = new MAPPED_SUBRESOURCE();
        var ctx2 = (ID3D11DeviceContext)Marshal.GetObjectForIUnknown(_context);
        var hr = ctx2.Map(_stagingTexture, 0, D3D11_MAP_READ, 0, ref mapped);
        if (hr != 0) throw new InvalidOperationException(
            $"ID3D11DeviceContext::Map failed (hr=0x{hr:X8}).");
        try
        {
            unsafe
            {
                var src = (byte*)mapped.PData;
                var srcStride = (int)mapped.RowPitch;
                for (var y = 0; y < h; y++)
                {
                    var dst = y * tightStride;
                    System.Runtime.InteropServices.Marshal.Copy(
                        (IntPtr)(src + (y * srcStride)), buffer, dst, tightStride);
                }
            }
        }
        finally
        {
            ctx2.Unmap(_stagingTexture, 0);
        }
        return (buffer, w, h);
    }

    private void EnsureStagingTexture(int width, int height)
    {
        if (_stagingTexture != IntPtr.Zero && _stagingWidth == width && _stagingHeight == height)
            return;

        if (_stagingTexture != IntPtr.Zero)
        {
            Marshal.Release(_stagingTexture);
            _stagingTexture = IntPtr.Zero;
        }

        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleDesc_Count = 1,
            SampleDesc_Quality = 0,
            Usage = D3D11_USAGE_STAGING,
            BindFlags = 0,
            CPUAccessFlags = D3D11_CPU_ACCESS_READ,
            MiscFlags = 0,
        };
        var dev = (ID3D11Device)Marshal.GetObjectForIUnknown(_device);
        IntPtr tex;
        var hr = dev.CreateTexture2D(ref desc, IntPtr.Zero, IntPtr.Zero, out tex);
        if (hr != 0) throw new InvalidOperationException(
            $"ID3D11Device::CreateTexture2D failed (hr=0x{hr:X8}).");
        _stagingTexture = tex;
        _stagingWidth = width;
        _stagingHeight = height;
    }

    /// <summary>
    /// Use IDirect3DDxgiInterfaceAccess to get the
    /// underlying COM pointer for the requested IID.
    /// The returned pointer has a +1 ref count;
    /// caller is responsible for Marshal.Release.
    /// </summary>
    private static IntPtr GetDXGIInterface(object winRtObject, Guid iid)
    {
        // The WinRT IDirect3DSurface / IDirect3DDevice
        // implements IDirect3DDxgiInterfaceAccess as a
        // separate COM interface. To get at it, we
        // need to QueryInterface from the underlying
        // IUnknown*. We get that via the
        // IObjectReference property on the .NET WinRT
        // projection (WinRT types implement
        // IWinRTObject, which exposes the raw pointer).
        //
        // This is the same pattern that worked for
        // IGraphicsCaptureItemInterop: use
        // WinRT.IObjectReference.As<T> or, if the
        // target interface isn't a CsWinRT-projected
        // type (like our IDirect3DDxgiInterfaceAccess),
        // use GetTypedObjectForIUnknown with the IID.
        var winRt = (WinRT.IWinRTObject)winRtObject;
        var iObjectRef = winRt.NativeObject;
        // QueryInterface for IDirect3DDxgiInterfaceAccess.
        var accessIid = typeof(IDirect3DDxgiInterfaceAccess).GUID;
        Marshal.QueryInterface(iObjectRef.ThisPtr, in accessIid, out var accessPtr);
        if (accessPtr == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                "IDirect3DDxgiInterfaceAccess not implemented on the WinRT object.");
        }
        try
        {
            // Call GetInterface(ref iid) which is the
            // IDirect3DDxgiInterfaceAccess method.
            // The returned IntPtr is a +1 ref on the
            // requested interface; caller releases.
            var access =
                (IDirect3DDxgiInterfaceAccess)Marshal.GetTypedObjectForIUnknown(
                    accessPtr, typeof(IDirect3DDxgiInterfaceAccess));
            return access.GetInterface(ref iid);
        }
        finally
        {
            Marshal.Release(accessPtr);
        }
    }

    public void Dispose()
    {
        if (_stagingTexture != IntPtr.Zero)
        {
            Marshal.Release(_stagingTexture);
            _stagingTexture = IntPtr.Zero;
        }
        if (_context != IntPtr.Zero)
        {
            Marshal.Release(_context);
            _context = IntPtr.Zero;
        }
        if (_device != IntPtr.Zero)
        {
            Marshal.Release(_device);
            _device = IntPtr.Zero;
        }
    }
}
#endif
