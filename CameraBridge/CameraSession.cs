using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace CameraBridge;

internal sealed class CameraSession : IDisposable
{
    private bool _initialized;
    private bool _disposed;

    public string Manufacturer { get; private set; } = string.Empty;
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int MeterCount { get; private set; } = 1;

    public void Initialize()
    {
        ThrowIfDisposed();
        if (_initialized)
        {
            return;
        }

        var manufacturerBuffer = new StringBuilder(260);
        int width = 0;
        int height = 0;
        int meterCount = 0;

        int initCode = CK.PhotoCaptureInitWithInfo(ref width, ref height, ref meterCount, manufacturerBuffer);
        if (initCode != 0)
        {
            manufacturerBuffer.Clear();
            manufacturerBuffer.EnsureCapacity(260);
            initCode = CK.PhotoCaptureInit(manufacturerBuffer);
            if (initCode != 0)
            {
                throw new CameraBridgeException($"PhotoCaptureInit 调用失败", initCode);
            }

            int infoCode = CK.PhotoCaptureGet(ref width, ref height, ref meterCount);
            if (infoCode != 0)
            {
                throw new CameraBridgeException($"PhotoCaptureGet 调用失败", infoCode);
            }
        }

        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException($"DLL 返回的分辨率非法: {width}x{height}");
        }

        MeterCount = meterCount > 0 ? meterCount : 1;
        Manufacturer = manufacturerBuffer.ToString();
        Width = width;
        Height = height;
        _initialized = true;
    }

    public CaptureResult Capture(CaptureOptions options)
    {
        ThrowIfDisposed();
        if (!_initialized)
        {
            throw new InvalidOperationException("请先调用 Initialize()");
        }

        var zone = BuildZone(options.Zone);
        int bufferSize = checked(Width * Height * options.BytesPerPixel);
        var buffers = new IntPtr[MeterCount];

        try
        {
            for (int i = 0; i < buffers.Length; i++)
            {
                buffers[i] = Marshal.AllocHGlobal(bufferSize);
            }

            int rc = CK.PhotoCapture(zone, buffers);
            if (rc != 0)
            {
                return new CaptureResult(rc, options.OutputPath, Manufacturer, Width, Height, MeterCount, false);
            }

            var outputPath = Path.GetFullPath(options.OutputPath);
            ImageWriter.Write(outputPath, buffers[0], Width, Height, options.Format);

            return new CaptureResult(0, outputPath, Manufacturer, Width, Height, MeterCount, true);
        }
        finally
        {
            foreach (IntPtr ptr in buffers)
            {
                if (ptr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(ptr);
                }
            }

            // DLL 要求每次拍照后释放资源时调用 Exit
            CK.PhotoCaptureExit();
            _initialized = false;
        }
    }

    private int[] BuildZone(int[]? requested)
    {
        int segments = MeterCount > 0 ? MeterCount : 1;
        if (requested is null)
        {
            var zone = new int[segments * 4];
            for (int i = 0; i < segments; i++)
            {
                zone[i * 4 + 0] = 0;
                zone[i * 4 + 1] = 0;
                zone[i * 4 + 2] = Width;
                zone[i * 4 + 3] = Height;
            }
            return zone;
        }

        if (requested.Length == segments * 4)
        {
            return (int[])requested.Clone();
        }

        if (requested.Length == 4)
        {
            var zone = new int[segments * 4];
            for (int i = 0; i < segments; i++)
            {
                Array.Copy(requested, 0, zone, i * 4, 4);
            }
            return zone;
        }

        throw new ArgumentException($"--zone 参数长度必须为 4 或 {segments * 4}");
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(CameraSession));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_initialized)
        {
            CK.PhotoCaptureExit();
            _initialized = false;
        }

        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
