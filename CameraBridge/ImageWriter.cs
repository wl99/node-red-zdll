using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using DrawingPixelFormat = System.Drawing.Imaging.PixelFormat;
using ImageFormat = System.Drawing.Imaging.ImageFormat;
using ImageLockMode = System.Drawing.Imaging.ImageLockMode;

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
        else if (extension == ".jpg" || extension == ".jpeg")
        {
            SaveAsJpeg(path, buffer, width, height, format);
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

    private static void SaveAsJpeg(string path, IntPtr buffer, int width, int height, PixelFormat format)
    {
        using var bitmap = CreateBitmap(buffer, width, height, format);
        bitmap.Save(path, ImageFormat.Jpeg);
    }

    private static Bitmap CreateBitmap(IntPtr buffer, int width, int height, PixelFormat format)
    {
        return format switch
        {
            PixelFormat.Gray8 => CreateGrayBitmap(buffer, width, height),
            PixelFormat.Bgr24 => CreateBgrBitmap(buffer, width, height),
            PixelFormat.Rgb24 => CreateRgbBitmap(buffer, width, height),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "未知像素格式")
        };
    }

    private static Bitmap CreateGrayBitmap(IntPtr buffer, int width, int height)
    {
        var bitmap = new Bitmap(width, height, DrawingPixelFormat.Format24bppRgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, DrawingPixelFormat.Format24bppRgb);

        try
        {
            int rowBytes = width;
            int stride = data.Stride;
            byte[] srcRow = new byte[rowBytes];
            byte[] destRow = new byte[stride];

            for (int y = 0; y < height; y++)
            {
                int sourceIndex = height - 1 - y;
                IntPtr srcPtr = IntPtr.Add(buffer, sourceIndex * rowBytes);
                Marshal.Copy(srcPtr, srcRow, 0, rowBytes);

                for (int x = 0; x < width; x++)
                {
                    byte value = srcRow[x];
                    int offset = x * 3;
                    destRow[offset] = value;
                    destRow[offset + 1] = value;
                    destRow[offset + 2] = value;
                }

                if (stride > width * 3)
                {
                    Array.Clear(destRow, width * 3, stride - width * 3);
                }

                IntPtr destPtr = IntPtr.Add(data.Scan0, y * stride);
                Marshal.Copy(destRow, 0, destPtr, stride);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return bitmap;
    }

    private static Bitmap CreateBgrBitmap(IntPtr buffer, int width, int height)
    {
        var bitmap = new Bitmap(width, height, DrawingPixelFormat.Format24bppRgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, DrawingPixelFormat.Format24bppRgb);

        try
        {
            int rowBytes = width * 3;
            int stride = data.Stride;
            byte[] srcRow = new byte[rowBytes];
            byte[] destRow = new byte[stride];

            for (int y = 0; y < height; y++)
            {
                int sourceIndex = height - 1 - y;
                IntPtr srcPtr = IntPtr.Add(buffer, sourceIndex * rowBytes);
                Marshal.Copy(srcPtr, srcRow, 0, rowBytes);

                Buffer.BlockCopy(srcRow, 0, destRow, 0, rowBytes);
                if (stride > rowBytes)
                {
                    Array.Clear(destRow, rowBytes, stride - rowBytes);
                }

                IntPtr destPtr = IntPtr.Add(data.Scan0, y * stride);
                Marshal.Copy(destRow, 0, destPtr, stride);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return bitmap;
    }

    private static Bitmap CreateRgbBitmap(IntPtr buffer, int width, int height)
    {
        var bitmap = new Bitmap(width, height, DrawingPixelFormat.Format24bppRgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, DrawingPixelFormat.Format24bppRgb);

        try
        {
            int rowBytes = width * 3;
            int stride = data.Stride;
            byte[] srcRow = new byte[rowBytes];
            byte[] destRow = new byte[stride];

            for (int y = 0; y < height; y++)
            {
                int sourceIndex = height - 1 - y;
                IntPtr srcPtr = IntPtr.Add(buffer, sourceIndex * rowBytes);
                Marshal.Copy(srcPtr, srcRow, 0, rowBytes);

                for (int x = 0; x < width; x++)
                {
                    int offset = x * 3;
                    destRow[offset] = srcRow[offset + 2];
                    destRow[offset + 1] = srcRow[offset + 1];
                    destRow[offset + 2] = srcRow[offset];
                }

                if (stride > rowBytes)
                {
                    Array.Clear(destRow, rowBytes, stride - rowBytes);
                }

                IntPtr destPtr = IntPtr.Add(data.Scan0, y * stride);
                Marshal.Copy(destRow, 0, destPtr, stride);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return bitmap;
    }
}
