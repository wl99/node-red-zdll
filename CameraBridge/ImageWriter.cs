using System;
using System.IO;
using System.Runtime.InteropServices;

namespace CameraBridge;

internal static class ImageWriter
{
    public static void Write(string path, IntPtr buffer, int width, int height, PixelFormat format)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension == ".bmp")
        {
            SaveAsBmp(path, buffer, width, height, format);
        }
        else
        {
            SaveAsRaw(path, buffer, width, height, format);
        }
    }

    private static void SaveAsRaw(string path, IntPtr buffer, int width, int height, PixelFormat format)
    {
        int size = checked(width * height * BytesPerPixel(format));
        byte[] data = new byte[size];
        Marshal.Copy(buffer, data, 0, size);
        File.WriteAllBytes(path, data);
    }

    private static void SaveAsBmp(string path, IntPtr buffer, int width, int height, PixelFormat format)
    {
        int bytesPerPixel = BytesPerPixel(format);
        int rowBytes = checked(width * bytesPerPixel);
        int stride = AlignTo4(rowBytes);
        int imageSize = checked(stride * height);
        int paletteSize = format == PixelFormat.Gray8 ? 256 * 4 : 0;
        int headerSize = 14 + 40 + paletteSize;
        int fileSize = headerSize + imageSize;

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream);

        // BITMAPFILEHEADER
        writer.Write((ushort)0x4D42); // 'BM'
        writer.Write(fileSize);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write(headerSize);

        // BITMAPINFOHEADER
        writer.Write(40);
        writer.Write(width);
        writer.Write(height);
        writer.Write((ushort)1); // planes
        writer.Write((ushort)(bytesPerPixel * 8));
        writer.Write(0); // compression BI_RGB
        writer.Write(imageSize);
        writer.Write(2835); // 72 DPI
        writer.Write(2835);
        writer.Write(format == PixelFormat.Gray8 ? 256 : 0);
        writer.Write(0);

        if (format == PixelFormat.Gray8)
        {
            for (int i = 0; i < 256; i++)
            {
                writer.Write(i | (i << 8) | (i << 16));
            }
        }

        byte[] row = new byte[stride];
        for (int y = 0; y < height; y++)
        {
            Array.Clear(row, 0, row.Length);
            int sourceIndex = height - 1 - y; // BMP 要倒序
            IntPtr srcPtr = IntPtr.Add(buffer, sourceIndex * rowBytes);
            Marshal.Copy(srcPtr, row, 0, rowBytes);

            if (format == PixelFormat.Rgb24)
            {
                for (int i = 0; i < rowBytes; i += 3)
                {
                    (row[i], row[i + 2]) = (row[i + 2], row[i]);
                }
            }

            writer.Write(row);
        }
    }

    private static int BytesPerPixel(PixelFormat format) => format switch
    {
        PixelFormat.Gray8 => 1,
        PixelFormat.Bgr24 or PixelFormat.Rgb24 => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "未知像素格式")
    };

    private static int AlignTo4(int value) => (value + 3) & ~3;
}
