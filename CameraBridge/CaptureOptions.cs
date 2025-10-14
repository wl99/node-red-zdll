using System;

namespace CameraBridge;

internal sealed class CaptureOptions
{
    public CaptureOptions(string outputPath, PixelFormat format, int[]? zone)
    {
        OutputPath = outputPath ?? throw new ArgumentNullException(nameof(outputPath));
        Format = format;
        Zone = zone is null ? null : (int[])zone.Clone();
    }

    public string OutputPath { get; }

    public PixelFormat Format { get; }

    public int[]? Zone { get; }

    public int BytesPerPixel => Format switch
    {
        PixelFormat.Gray8 => 1,
        PixelFormat.Bgr24 or PixelFormat.Rgb24 => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(Format), Format, "未知像素格式")
    };
}
