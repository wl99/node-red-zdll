using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

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
                "--init" => RunInit(args),
                "--info" => RunInfo(args),
                "--release" => RunRelease(args),
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
        int meterIndex = 1;
        string? ckDllPath = null;

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
                    var zoneValues = new System.Collections.Generic.List<int>();
                    while (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        zoneValues.Add(ParseInt(args[++i], $"zone[{zoneValues.Count}]"));
                    }
                    if (zoneValues.Count == 0)
                    {
                        return Fail("--zone 需要至少 1 个整数参数");
                    }
                    zone = zoneValues.ToArray();
                    break;
                case "--meter-index":
                    if (++i >= args.Length)
                    {
                        return Fail("--meter-index 需要一个大于 0 的整数");
                    }
                    meterIndex = ParseInt(args[i], "meter-index");
                    if (meterIndex <= 0)
                    {
                        return Fail("--meter-index 必须为正整数");
                    }
                    break;
                case "--ck-dll":
                    if (++i >= args.Length)
                    {
                        return Fail("--ck-dll 需要一个指向 CKGenCapture.dll 的路径");
                    }
                    ckDllPath = args[i];
                    break;
                default:
                    return Fail($"未知参数: {token}");
            }
        }

        PrepareNativeDependencies(ckDllPath);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)) ?? ".");

        var options = new CaptureOptions(output, format, zone, meterIndex);

        using var session = new CameraSession();
        session.Initialize();

        var result = session.Capture(options);

        Console.WriteLine($"厂商: {result.Manufacturer}");
        Console.WriteLine($"分辨率: {result.Width}x{result.Height}");
        Console.WriteLine($"测点数量: {result.MeterCount}");
        Console.WriteLine($"驱动返回码: {result.DriverCode}");
        Console.WriteLine($"实际使用测点索引: {result.SelectedMeterIndex} (请求值: {meterIndex})");

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

    private static int RunInit(string[] args)
    {
        PrepareNativeDependencies(ExtractCkDllPath(args));

        using var session = new CameraSession();
        session.Initialize();
        Console.WriteLine("初始化成功。");
        Console.WriteLine($"厂商: {session.Manufacturer}");
        Console.WriteLine($"分辨率: {session.Width}x{session.Height}");
        Console.WriteLine($"测点数量: {session.MeterCount}");
        return 0;
    }

    private static int RunInfo(string[] args)
    {
        PrepareNativeDependencies(ExtractCkDllPath(args));

        using var session = new CameraSession();
        session.Initialize();
        Console.WriteLine($"厂商: {session.Manufacturer}");
        Console.WriteLine($"分辨率: {session.Width}x{session.Height}");
        Console.WriteLine($"测点数量: {session.MeterCount}");
        return 0;
    }

    private static int RunRelease(string[] args)
    {
        PrepareNativeDependencies(ExtractCkDllPath(args));

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
        Console.WriteLine("  --capture <outputPath> [--format gray8|bgr24|rgb24] [--zone <n1> <n2> ...] [--meter-index <n>] [--ck-dll <path>]");
        Console.WriteLine("  --init [--ck-dll <path>]");
        Console.WriteLine("  --info [--ck-dll <path>]");
        Console.WriteLine("  --release [--ck-dll <path>]");
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return -1;
    }

    private static string? ExtractCkDllPath(string[] args)
    {
        if (args.Length <= 1)
        {
            return null;
        }

        for (int i = 1; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--ck-dll", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException("--ck-dll 需要一个指向 CKGenCapture.dll 的路径");
                }
                return args[i + 1];
            }
        }
        return null;
    }

    private static void PrepareNativeDependencies(string? ckDllPath)
    {
        string fullPath;
        if (string.IsNullOrWhiteSpace(ckDllPath))
        {
            fullPath = Path.Combine(AppContext.BaseDirectory, "CKGenCapture.dll");
            if (!File.Exists(fullPath))
            {
                // 没有显式配置且默认文件不存在，保持现有搜索路径让系统自行解析
                return;
            }
        }
        else
        {
            fullPath = Path.GetFullPath(ckDllPath);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException($"指定的 CKGenCapture.dll 不存在: {fullPath}");
            }
        }

        string directory = Path.GetDirectoryName(fullPath) ?? Environment.CurrentDirectory;
        if (!NativeMethods.SetDllDirectory(directory))
        {
            int error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"无法设置 DLL 搜索路径: {directory} (错误码 {error})");
        }

        NativeLibrary.Load(fullPath);
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern bool SetDllDirectory(string? lpPathName);
    }
}
