namespace CameraBridge;

internal sealed class CaptureResult
{
    public CaptureResult(int driverCode, bool success, string outputPath, string manufacturer, int width, int height, int meterCount, bool saved, int selectedMeterIndex)
    {
        DriverCode = driverCode;
        Success = success;
        ReturnCode = success ? 0 : (driverCode == 0 ? -1 : driverCode);
        OutputPath = outputPath;
        Manufacturer = manufacturer;
        Width = width;
        Height = height;
        MeterCount = meterCount;
        Saved = saved;
        SelectedMeterIndex = selectedMeterIndex;
    }

    public int DriverCode { get; }
    public bool Success { get; }
    public int ReturnCode { get; }
    public string OutputPath { get; }
    public string Manufacturer { get; }
    public int Width { get; }
    public int Height { get; }
    public int MeterCount { get; }
    public bool Saved { get; }
    public int SelectedMeterIndex { get; }
}
