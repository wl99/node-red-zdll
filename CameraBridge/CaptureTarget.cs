namespace CameraBridge;

internal sealed class CaptureTarget
{
    public CaptureTarget(string outputPath, int meterIndex)
    {
        OutputPath = outputPath;
        MeterIndex = meterIndex;
    }

    public string OutputPath { get; }

    public int MeterIndex { get; }
}
