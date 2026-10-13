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

    private static readonly Guid IID_IDirect3DDxgiInterfaceAccess =
        new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetInterfaceDelegate(
        IntPtr self,
        in Guid iid,
        out IntPtr result);

    // ID3D11Device vtable. Unused entries are placeholders so the
    // methods we call land at their native vtable indices.
    [ComImport]
    [Guid("db6f6ddb-ac77-4e88-8253-819df9bbf140")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ID3D11Device
    {
        void Reserved0();
        void Reserved1();
        [PreserveSig]
        int CreateTexture2D(
            ref D3D11_TEXTURE2D_DESC desc,
            IntPtr initialData,
            out IntPtr texture);
    }

    // ID3D11DeviceContext vtable. Its COM methods begin after IUnknown;
    // Map and CopyResource are at method indices 11 and 44 respectively.
    [ComImport]
    [Guid("c0bfa96c-e089-44fb-8eaf-26f8796190da")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ID3D11DeviceContext
    {
        void Reserved0(); void Reserved1(); void Reserved2();
        void Reserved3(); void Reserved4(); void Reserved5();
        void Reserved6(); void Reserved7(); void Reserved8();
        void Reserved9(); void Reserved10();
        [PreserveSig]
        int Map(
            IntPtr resource,
            uint subresource,
            uint mapType,
            uint mapFlags,
            ref MAPPED_SUBRESOURCE mapped);
        void Unmap(IntPtr resource, uint subresource);
        void Reserved13(); void Reserved14(); void Reserved15();
        void Reserved16(); void Reserved17(); void Reserved18();
        void Reserved19(); void Reserved20(); void Reserved21();
        void Reserved22(); void Reserved23(); void Reserved24();
        void Reserved25(); void Reserved26(); void Reserved27();
        void Reserved28(); void Reserved29(); void Reserved30();
        void Reserved31(); void Reserved32(); void Reserved33();
        void Reserved34(); void Reserved35(); void Reserved36();
        void Reserved37(); void Reserved38(); void Reserved39();
        void Reserved40(); void Reserved41(); void Reserved42();
        void Reserved43();
        void CopyResource(IntPtr dst, IntPtr src);
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
        public uint CPUAccessFlags;   // 0x20000 = D3D11_CPU_ACCESS_READ
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
    private const uint D3D11_CPU_ACCESS_READ = 0x20000;
    private const uint DXGI_FORMAT_B8G8R8A8_UNORM = 87;
    private const uint D3D11_MAP_READ = 1;
    private const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;

    // IID for the IDXGIDevice COM interface. Used to
    // bridge D3D11CreateDevice's output to the WinRT
    // IDirect3DDevice via CreateDirect3D11DeviceFromDXGIDevice.
    private static readonly Guid IID_IDXGIDevice =
        new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");

    // D3D11CreateDevice from d3d11.dll. The two optional native
    // outputs after ppDevice must still be present in this signature.
    [DllImport("d3d11.dll", EntryPoint = "D3D11CreateDevice", CallingConvention = CallingConvention.StdCall)]
    private static extern int D3D11CreateDevice(
        IntPtr adapter,
        uint driverType,
        IntPtr software,
        uint flags,
        IntPtr featureLevels,
        uint featureLevelCount,
        uint sdkVersion,
        out IntPtr deviceOut,
        IntPtr featureLevelOut,
        out IntPtr immediateContextOut);

    // d3d11.dll's CreateDirect3D11DeviceFromDXGIDevice.
    // Returns an IInspectable* (which is then
    // Marshal.GetObjectForIUnknown'd to IDirect3DDevice).
    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", CallingConvention = CallingConvention.StdCall)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(
        IntPtr dxgiDevice,
        out IntPtr graphicsDevice);

    private IntPtr _device;
    private IntPtr _context;
    private IntPtr _stagingTexture;
    private int _stagingWidth;
    private int _stagingHeight;

    public D3D11FrameReader(IntPtr device, IntPtr context)
    {
        _device = device;
        _context = context;
    }

    /// <summary>
    /// Create a WinRT <see cref="IDirect3DDevice"/> that
    /// wraps a fresh D3D11 hardware device. Used to
    /// create the device that the WGC frame pool needs;
    /// the underlying ID3D11Device* is also kept by the
    /// <see cref="D3D11FrameReader"/> for surface copies.
    /// </summary>
    public static IDirect3DDevice CreateWinRtDevice(
        out IntPtr nativeDevice,
        out IntPtr immediateContext)
    {
        nativeDevice = IntPtr.Zero;
        immediateContext = IntPtr.Zero;
        // D3D11CreateDevice wants a D3D_FEATURE_LEVEL[]. We
        // use 11.0 (the only one we need).
        var featureLevels = new uint[] { 0xb000 /* D3D_FEATURE_LEVEL_11_0 = 0xb000 */ };
        var flPtr = Marshal.AllocHGlobal(sizeof(uint) * featureLevels.Length);
        var d3dDevicePtr = IntPtr.Zero;
        var contextPtr = IntPtr.Zero;
        try
        {
            // Marshal.Copy only takes byte[] / short[] /
            // int[] for primitive overloads, not uint[].
            // Use MemoryMarshal to reinterpret the span.
            var flSpan = System.Runtime.InteropServices.MemoryMarshal
                .AsBytes(featureLevels.AsSpan());
            for (var i = 0; i < flSpan.Length; i++)
                Marshal.WriteByte(flPtr, i, flSpan[i]);
            var hr = D3D11CreateDevice(
                IntPtr.Zero,    // default adapter
                1,              // D3D_DRIVER_TYPE_HARDWARE
                IntPtr.Zero,    // no software fallback module
                D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                flPtr,
                (uint)featureLevels.Length,
                7,              // D3D11_SDK_VERSION
                out d3dDevicePtr,
                IntPtr.Zero,    // optional selected feature level
                out contextPtr);
            if (hr < 0) throw new InvalidOperationException(
                $"D3D11CreateDevice failed (hr=0x{hr:X8}).");

            // Query IDXGIDevice from the ID3D11Device.
            var iid = IID_IDXGIDevice;
            var queryHr = Marshal.QueryInterface(d3dDevicePtr, in iid, out var dxgiPtr);
            try
            {
                if (queryHr < 0)
                    throw new InvalidOperationException(
                        $"ID3D11Device::QueryInterface(IDXGIDevice) failed (hr=0x{queryHr:X8}).");
                if (dxgiPtr == IntPtr.Zero) throw new InvalidOperationException(
                    "ID3D11Device::QueryInterface(IDXGIDevice) succeeded but returned a null pointer.");

                // Wrap the IDXGIDevice in a WinRT IDirect3DDevice.
                var hr2 = CreateDirect3D11DeviceFromDXGIDevice(dxgiPtr, out var inspectablePtr);
                if (hr2 < 0) throw new InvalidOperationException(
                    $"CreateDirect3D11DeviceFromDXGIDevice failed (hr=0x{hr2:X8}).");

                try
                {
                    var device =
                        WinRT.MarshalInterface<IDirect3DDevice>
                            .FromAbi(inspectablePtr);
                    nativeDevice = d3dDevicePtr;
                    d3dDevicePtr = IntPtr.Zero;
                    immediateContext = contextPtr;
                    contextPtr = IntPtr.Zero;
                    return device;
                }
                finally
                {
                    Marshal.Release(inspectablePtr);
                }
            }
            finally
            {
                if (dxgiPtr != IntPtr.Zero) Marshal.Release(dxgiPtr);
            }
        }
        finally
        {
            if (contextPtr != IntPtr.Zero) Marshal.Release(contextPtr);
            if (d3dDevicePtr != IntPtr.Zero) Marshal.Release(d3dDevicePtr);
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
        var surfacePtr = GetDXGIInterface(frame.Surface, IID_ID3D11Texture2D);
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
        var hr = dev.CreateTexture2D(ref desc, IntPtr.Zero, out tex);
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
    private static IntPtr GetDXGIInterface(IDirect3DSurface winRtObject, Guid iid)
    {
        var marshaler =
            WinRT.MarshalInterface<IDirect3DSurface>.CreateMarshaler2(winRtObject);
        var abiPtr = WinRT.MarshalInterface<IDirect3DSurface>.GetAbi(marshaler);
        if (abiPtr == IntPtr.Zero)
            throw new InvalidOperationException(
                "The WinRT surface projected to a null ABI pointer.");

        var accessHr = Marshal.QueryInterface(
            abiPtr, in IID_IDirect3DDxgiInterfaceAccess, out var accessPtr);
        if (accessHr < 0)
            throw new InvalidOperationException(
                $"QueryInterface(IDirect3DDxgiInterfaceAccess) failed (hr=0x{accessHr:X8}).");

        try
        {
            var vtable = Marshal.ReadIntPtr(accessPtr);
            var getInterfacePtr = Marshal.ReadIntPtr(
                vtable, 3 * IntPtr.Size);
            var getInterface =
                Marshal.GetDelegateForFunctionPointer<GetInterfaceDelegate>(
                    getInterfacePtr);
            var hr = getInterface(accessPtr, in iid, out var result);
            if (hr < 0)
            {
                if (result != IntPtr.Zero) Marshal.Release(result);
                throw new InvalidOperationException(
                    $"IDirect3DDxgiInterfaceAccess::GetInterface failed (hr=0x{hr:X8}).");
            }
            if (result == IntPtr.Zero)
                throw new InvalidOperationException(
                    "IDirect3DDxgiInterfaceAccess::GetInterface succeeded but returned a null pointer.");
            return result;
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
