namespace CameraBridge;

internal sealed class CaptureResult
{
    public CaptureResult(int returnCode, string outputPath, string manufacturer, int width, int height, int meterCount, bool saved)
    {
        ReturnCode = returnCode;
        OutputPath = outputPath;
        Manufacturer = manufacturer;
        Width = width;
        Height = height;
        MeterCount = meterCount;
        Saved = saved;
    }

    public int ReturnCode { get; }
    public string OutputPath { get; }
    public string Manufacturer { get; }
    public int Width { get; }
    public int Height { get; }
    public int MeterCount { get; }
    public bool Saved { get; }
}
