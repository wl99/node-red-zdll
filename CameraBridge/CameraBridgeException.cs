using System;

namespace CameraBridge;

internal sealed class CameraBridgeException : Exception
{
    public CameraBridgeException(string message, int returnCode)
        : base(message)
    {
        ReturnCode = returnCode;
    }

    public int ReturnCode { get; }
}
