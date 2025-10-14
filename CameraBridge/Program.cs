using System;
using System.Globalization;
using System.IO;

namespace CameraBridge;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
        {
            PrintHelp();
            return 0;
        }

        try
        {
            return args[0] switch
            {
                "--capture" => RunCapture(args),
                "--init" => RunInit(),
                "--info" => RunInfo(),
                "--release" => RunRelease(),
                _ => Fail($"未知命令: {args[0]}")
            };
        }
        catch (CameraBridgeException ex)
        {
            return Fail($"{ex.Message} (返回码: {ex.ReturnCode})");
        }
        catch (DllNotFoundException ex)
        {
            return Fail($"未找到 DLL: {ex.Message}");
        }
        catch (BadImageFormatException)
        {
            return Fail("DLL 与 CameraBridge.exe 位数不匹配，请确保均为 32 位。");
        }
        catch (Exception ex)
        {
            return Fail($"执行异常: {ex.Message}");
        }
    }

    private static int RunCapture(string[] args)
    {
        if (args.Length < 2)
        {
            return Fail("缺少输出文件路径: --capture <path>");
        }

        string output = args[1];
        PixelFormat format = PixelFormat.Gray8;
        int[]? zone = null;

        for (int i = 2; i < args.Length; i++)
        {
            string token = args[i];
            switch (token)
            {
                case "--format":
                    if (++i >= args.Length)
                    {
                        return Fail("--format 需要一个取值 (gray8/bgr24/rgb24)");
                    }
                    format = ParseFormat(args[i]);
                    break;
                case "--zone":
                    const int zoneLength = 4;
                    if (i + zoneLength >= args.Length)
                    {
                        return Fail("--zone 需要 4 个整数参数: left top right bottom");
                    }
                    zone = new int[zoneLength];
                    for (int z = 0; z < zoneLength; z++)
                    {
                        zone[z] = ParseInt(args[++i], $"zone[{z}]");
                    }
                    break;
                default:
                    return Fail($"未知参数: {token}");
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)) ?? ".");

        var options = new CaptureOptions(output, format, zone);

        using var session = new CameraSession();
        session.Initialize();

        var result = session.Capture(options);

        Console.WriteLine($"厂商: {result.Manufacturer}");
        Console.WriteLine($"分辨率: {result.Width}x{result.Height}");
        Console.WriteLine($"测点数量: {result.MeterCount}");

        if (result.Saved)
        {
            Console.WriteLine($"输出文件: {result.OutputPath}");
        }
        else
        {
            Console.WriteLine("未生成图像文件，参见返回码确认原因。");
        }

        return result.ReturnCode;
    }

    private static int RunInit()
    {
        using var session = new CameraSession();
        session.Initialize();
        Console.WriteLine("初始化成功。");
        Console.WriteLine($"厂商: {session.Manufacturer}");
        Console.WriteLine($"分辨率: {session.Width}x{session.Height}");
        Console.WriteLine($"测点数量: {session.MeterCount}");
        return 0;
    }

    private static int RunInfo()
    {
        using var session = new CameraSession();
        session.Initialize();
        Console.WriteLine($"厂商: {session.Manufacturer}");
        Console.WriteLine($"分辨率: {session.Width}x{session.Height}");
        Console.WriteLine($"测点数量: {session.MeterCount}");
        return 0;
    }

    private static int RunRelease()
    {
        int code = CK.PhotoCaptureExit();
        if (code == 0)
        {
            Console.WriteLine("已调用 PhotoCaptureExit。");
        }
        else
        {
            Console.WriteLine($"PhotoCaptureExit 返回码: {code}");
        }
        return code;
    }

    private static PixelFormat ParseFormat(string value) => value.ToLowerInvariant() switch
    {
        "gray8" or "greyscale" or "grayscale" => PixelFormat.Gray8,
        "bgr24" => PixelFormat.Bgr24,
        "rgb24" => PixelFormat.Rgb24,
        _ => throw new ArgumentException($"不支持的图像格式: {value}")
    };

    private static int ParseInt(string value, string name)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result))
        {
            throw new ArgumentException($"无法解析 {name}: {value}");
        }
        return result;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("CameraBridge 命令行用法:");
        Console.WriteLine("  --capture <outputPath> [--format gray8|bgr24|rgb24] [--zone left top right bottom]");
        Console.WriteLine("  --init");
        Console.WriteLine("  --info");
        Console.WriteLine("  --release");
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return -1;
    }
}
