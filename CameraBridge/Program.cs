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
                _ => Fail($"Unknown command: {args[0]}")
            };
        }
        catch (CameraBridgeException ex)
        {
            return Fail($"{ex.Message} (driver code: {ex.ReturnCode})");
        }
        catch (DllNotFoundException ex)
        {
            return Fail($"DLL not found: {ex.Message}");
        }
        catch (BadImageFormatException)
        {
            return Fail("Architecture mismatch: ensure CameraBridge.exe and CKGenCapture.dll are both 32-bit.");
        }
        catch (Exception ex)
        {
            return Fail($"Unhandled exception: {ex.Message}");
        }
    }

    private static int RunCapture(string[] args)
    {
        if (args.Length < 2)
        {
            return Fail("Missing output file path: --capture <path>");
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
                        return Fail("--format requires a value (gray8/bgr24/rgb24)");
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
                        return Fail("--zone requires at least one integer value");
                    }
                    zone = zoneValues.ToArray();
                    break;
                case "--meter-index":
                    if (++i >= args.Length)
                    {
                        return Fail("--meter-index requires a positive integer");
                    }
                    meterIndex = ParseInt(args[i], "meter-index");
                    if (meterIndex <= 0)
                    {
                        return Fail("--meter-index must be greater than zero");
                    }
                    break;
                case "--ck-dll":
                    if (++i >= args.Length)
                    {
                        return Fail("--ck-dll requires a path to CKGenCapture.dll");
                    }
                    ckDllPath = args[i];
                    break;
                default:
                    return Fail($"Unknown option: {token}");
            }
        }

        PrepareNativeDependencies(ckDllPath);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)) ?? ".");

        var options = new CaptureOptions(output, format, zone, meterIndex);

        using var session = new CameraSession();
        session.Initialize();

        var result = session.Capture(options);

        Console.WriteLine($"Manufacturer: {result.Manufacturer}");
        Console.WriteLine($"Resolution: {result.Width}x{result.Height}");
        Console.WriteLine($"Meter count: {result.MeterCount}");
        Console.WriteLine($"Driver return code: {result.DriverCode}");
        Console.WriteLine($"Actual meter index: {result.SelectedMeterIndex} (requested {meterIndex})");

        if (result.Saved)
        {
            Console.WriteLine($"Output file: {result.OutputPath}");
        }
        else
        {
            Console.WriteLine("Image not saved; check driver return code for details.");
        }

        return result.ReturnCode;
    }

    private static int RunInit(string[] args)
    {
        PrepareNativeDependencies(ExtractCkDllPath(args));

        using var session = new CameraSession();
        session.Initialize();
        Console.WriteLine("Initialization succeeded.");
        Console.WriteLine($"Manufacturer: {session.Manufacturer}");
        Console.WriteLine($"Resolution: {session.Width}x{session.Height}");
        Console.WriteLine($"Meter count: {session.MeterCount}");
        return 0;
    }

    private static int RunInfo(string[] args)
    {
        PrepareNativeDependencies(ExtractCkDllPath(args));

        using var session = new CameraSession();
        session.Initialize();
        Console.WriteLine($"Manufacturer: {session.Manufacturer}");
        Console.WriteLine($"Resolution: {session.Width}x{session.Height}");
        Console.WriteLine($"Meter count: {session.MeterCount}");
        return 0;
    }

    private static int RunRelease(string[] args)
    {
        PrepareNativeDependencies(ExtractCkDllPath(args));

        int code = CK.PhotoCaptureExit();
        if (code == 0)
        {
            Console.WriteLine("PhotoCaptureExit called successfully.");
        }
        else
        {
            Console.WriteLine($"PhotoCaptureExit returned code: {code}");
        }
        return code;
    }

    private static PixelFormat ParseFormat(string value) => value.ToLowerInvariant() switch
    {
        "gray8" or "greyscale" or "grayscale" => PixelFormat.Gray8,
        "bgr24" => PixelFormat.Bgr24,
        "rgb24" => PixelFormat.Rgb24,
        _ => throw new ArgumentException($"Unsupported image format: {value}")
    };

    private static int ParseInt(string value, string name)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result))
        {
            throw new ArgumentException($"Unable to parse {name}: {value}");
        }
        return result;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("CameraBridge command line usage:");
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
