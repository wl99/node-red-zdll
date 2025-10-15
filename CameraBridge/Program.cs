using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CameraBridge;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

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

        string outputTemplate = args[1];
        PixelFormat format = PixelFormat.Gray8;
        int[]? zone = null;
        int meterIndex = 1;
        string? ckDllPath = null;
        List<int>? meterIndexesOverride = null;

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
                    var zoneValues = new List<int>();
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
                case "--meter-indexes":
                    if (++i >= args.Length)
                    {
                        return Fail("--meter-indexes requires a comma separated list of integers");
                    }
                    meterIndexesOverride = ParseMeterIndexList(args[i]);
                    if (meterIndexesOverride.Count == 0)
                    {
                        return Fail("--meter-indexes did not contain any valid meter index");
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

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputTemplate)) ?? ".");

        var meterIndexes = meterIndexesOverride ?? new List<int> { meterIndex };
        var options = new CaptureOptions(outputTemplate, format, zone, meterIndexes[0]);

        using var session = new CameraSession();
        session.Initialize();

        var targets = BuildTargets(outputTemplate, meterIndexes);
        var captureResults = session.CaptureMany(options, targets);

        PrintCaptureSummary(captureResults);

        var firstFailure = captureResults.FirstOrDefault(r => !r.Success);
        return firstFailure?.ReturnCode ?? 0;
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

    private static List<int> ParseMeterIndexList(string value)
    {
        var indexes = new List<int>();
        foreach (var token in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(token.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) && index > 0)
            {
                if (!indexes.Contains(index))
                {
                    indexes.Add(index);
                }
            }
        }
        return indexes;
    }

    private static List<CaptureTarget> BuildTargets(string template, IReadOnlyCollection<int> meterIndexes)
    {
        bool multiple = meterIndexes.Count > 1;
        var targets = new List<CaptureTarget>(meterIndexes.Count);
        foreach (int index in meterIndexes)
        {
            string outputPath = ResolveOutputPath(template, index, multiple);
            targets.Add(new CaptureTarget(outputPath, index));
        }
        return targets;
    }

    private static string ResolveOutputPath(string template, int meterIndex, bool multiple)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            throw new ArgumentException("Output path template cannot be empty.", nameof(template));
        }

        string meterString = meterIndex.ToString(CultureInfo.InvariantCulture);
        string replaced = Regex.Replace(template, @"\{\{\s*meter\s*\}\}", meterString, RegexOptions.IgnoreCase);

        if (!string.Equals(template, replaced, StringComparison.Ordinal))
        {
            return replaced;
        }

        string directory = Path.GetDirectoryName(template) ?? string.Empty;
        string fileName = Path.GetFileName(template);
        string extension = Path.GetExtension(fileName);
        string nameWithoutExt = extension.Length > 0 ? fileName.Substring(0, fileName.Length - extension.Length) : fileName;
        string suffixed = $"{nameWithoutExt}_meter{meterString}{extension}";
        return Path.Combine(directory, suffixed);
    }

    private static void PrintCaptureSummary(IReadOnlyList<CaptureResult> results)
    {
        if (results.Count == 0)
        {
            Console.WriteLine("No capture results to report.");
            return;
        }

        var first = results[0];
        Console.WriteLine($"Manufacturer: {first.Manufacturer}");
        Console.WriteLine($"Resolution: {first.Width}x{first.Height}");
        Console.WriteLine($"Meter count: {first.MeterCount}");

        foreach (var result in results)
        {
            Console.WriteLine($"Meter {result.SelectedMeterIndex}: {(result.Saved ? "saved" : "not saved")} -> {result.OutputPath} (driver code {result.DriverCode})");
        }

        var jsonSummary = new
        {
            manufacturer = first.Manufacturer,
            resolution = new { width = first.Width, height = first.Height },
            meterCount = first.MeterCount,
            results = results.Select(r => new
            {
                meterIndex = r.SelectedMeterIndex,
                output = r.OutputPath,
                saved = r.Saved,
                driverCode = r.DriverCode,
                success = r.Success
            }).ToList()
        };

        Console.WriteLine("JSON:" + JsonSerializer.Serialize(jsonSummary, JsonOptions));
    }

    private static void PrintHelp()
    {
        Console.WriteLine("CameraBridge command line usage:");
        Console.WriteLine("  --capture <outputPath> [--format gray8|bgr24|rgb24] [--zone <n1> <n2> ...] [--meter-index <n>] [--meter-indexes n1,n2,...] [--ck-dll <path>]");
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
                    throw new ArgumentException("--ck-dll requires a path to CKGenCapture.dll");
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
                // allow default search order to resolve the DLL
                return;
            }
        }
        else
        {
            fullPath = Path.GetFullPath(ckDllPath);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException($"Specified CKGenCapture.dll not found: {fullPath}");
            }
        }

        string directory = Path.GetDirectoryName(fullPath) ?? Environment.CurrentDirectory;
        if (!NativeMethods.SetDllDirectory(directory))
        {
            int error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"Unable to set DLL search path: {directory} (error code {error})");
        }

        NativeLibrary.Load(fullPath);
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern bool SetDllDirectory(string? lpPathName);
    }
}
