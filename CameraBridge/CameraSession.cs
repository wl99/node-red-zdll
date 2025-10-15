using System;
using System.Collections.Generic;
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
            throw new CameraBridgeException("PhotoCaptureInit failed", initCode);
        }

        RefreshSessionInfo(manufacturerBuffer);
        _initialized = true;
    }

    public CaptureResult Capture(CaptureOptions options)
    {
        var results = CaptureMany(options, new List<CaptureTarget>
        {
            new CaptureTarget(options.OutputPath, options.MeterIndex)
        });

        return results[0];
    }

    public IReadOnlyList<CaptureResult> CaptureMany(CaptureOptions options, IReadOnlyList<CaptureTarget> targets)
    {
        ThrowIfDisposed();
        if (!_initialized)
        {
            throw new InvalidOperationException("Initialize must be called before Capture");
        }

        if (targets.Count == 0)
        {
            throw new ArgumentException("At least one capture target is required", nameof(targets));
        }

        RefreshSessionInfo();

        var zone = BuildZone(options.Zone);
        int bufferSize = checked(Width * Height * options.BytesPerPixel);
        var buffers = new IntPtr[MeterCount];
        byte[] zeroBuffer = new byte[bufferSize];
        var results = new List<CaptureResult>(targets.Count);

        try
        {
            for (int i = 0; i < buffers.Length; i++)
            {
                buffers[i] = Marshal.AllocHGlobal(bufferSize);
                Marshal.Copy(zeroBuffer, 0, buffers[i], bufferSize);
            }

            int rc = CK.PhotoCapture(zone, buffers);
            bool success = IsSuccess(rc);

            foreach (var target in targets)
            {
                int selectedIndex = Math.Clamp(target.MeterIndex - 1, 0, MeterCount - 1);
                string outputPath = Path.GetFullPath(target.OutputPath);

                bool saved = false;
                if (success)
                {
                    ImageWriter.Write(outputPath, buffers[selectedIndex], Width, Height, options.Format);
                    saved = true;
                }

                results.Add(new CaptureResult(rc, success, outputPath, Manufacturer, Width, Height, MeterCount, saved, selectedIndex + 1));
            }

            return results;
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
            throw new CameraBridgeException("PhotoCaptureGet failed", infoCode);
        }

        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException($"Invalid resolution reported by driver: {width}x{height}");
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

        throw new ArgumentException($"--zone requires exactly {segments} values but received {requested.Length}");
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
