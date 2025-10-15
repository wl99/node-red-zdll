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

    private static bool IsSuccess(int code) => code == 0 || code == 1;

    public void Initialize()
    {
        ThrowIfDisposed();
        if (_initialized)
        {
            return;
        }

        var manufacturerBuffer = new StringBuilder(260);
        int initCode = CK.PhotoCaptureInit(manufacturerBuffer);
        if (!IsSuccess(initCode))
        {
            throw new CameraBridgeException("PhotoCaptureInit 调用失败", initCode);
        }

        RefreshSessionInfo(manufacturerBuffer);
        _initialized = true;
    }

    public CaptureResult Capture(CaptureOptions options)
    {
        ThrowIfDisposed();
        if (!_initialized)
        {
            throw new InvalidOperationException("请先调用 Initialize()");
        }

        RefreshSessionInfo();

        var zone = BuildZone(options.Zone);
        int bufferSize = checked(Width * Height * options.BytesPerPixel);
        var buffers = new IntPtr[MeterCount];
        byte[] zeroBuffer = new byte[bufferSize];

        int selectedIndex = Math.Clamp(options.MeterIndex - 1, 0, MeterCount - 1);

        try
        {
            for (int i = 0; i < buffers.Length; i++)
            {
                buffers[i] = Marshal.AllocHGlobal(bufferSize);
                Marshal.Copy(zeroBuffer, 0, buffers[i], bufferSize);
            }

            int rc = CK.PhotoCapture(zone, buffers);
            bool success = IsSuccess(rc);
            if (!success)
            {
                return new CaptureResult(rc, success, options.OutputPath, Manufacturer, Width, Height, MeterCount, false, selectedIndex + 1);
            }

            var outputPath = Path.GetFullPath(options.OutputPath);
            ImageWriter.Write(outputPath, buffers[selectedIndex], Width, Height, options.Format);

            return new CaptureResult(rc, success, outputPath, Manufacturer, Width, Height, MeterCount, true, selectedIndex + 1);
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
        }
    }

    private void RefreshSessionInfo(StringBuilder? manufacturerBuffer = null)
    {
        int width = 0;
        int height = 0;
        int meterCount = 0;

        int infoCode = CK.PhotoCaptureGet(ref width, ref height, ref meterCount);
        if (!IsSuccess(infoCode))
        {
            throw new CameraBridgeException("PhotoCaptureGet 调用失败", infoCode);
        }

        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException($"DLL 返回的分辨率非法: {width}x{height}");
        }

        MeterCount = meterCount > 0 ? meterCount : 1;
        Width = width;
        Height = height;

        if (manufacturerBuffer is not null)
        {
            Manufacturer = manufacturerBuffer.ToString();
        }
    }

    private int[] BuildZone(int[]? requested)
    {
        int segments = MeterCount > 0 ? MeterCount : 1;
        if (requested is null)
        {
            var zone = new int[segments];
            for (int i = 0; i < segments; i++)
            {
                zone[i] = 1;
            }
            return zone;
        }

        if (requested.Length == 1 && segments > 1)
        {
            var zone = new int[segments];
            for (int i = 0; i < segments; i++)
            {
                zone[i] = requested[0];
            }
            return zone;
        }

        if (requested.Length == segments)
        {
            return (int[])requested.Clone();
        }

        if (requested.Length == 4 && segments == 1)
        {
            return (int[])requested.Clone();
        }

        throw new ArgumentException($"--zone 参数长度必须为 {segments}，当前为 {requested.Length}");
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
