using System.Runtime.InteropServices;
using System.Text;

namespace CameraBridge;

internal static class CK
{
    private const string DllName = "CKGenCapture.dll";

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, ExactSpelling = true)]
    internal static extern int PhotoCaptureInit(StringBuilder mfrName);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, ExactSpelling = true, EntryPoint = "PhotoCaptureInit")]
    internal static extern int PhotoCaptureInitWithInfo(ref int width, ref int height, ref int mtrCount, StringBuilder mfrName);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int PhotoCaptureExit();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int PhotoCaptureGet(ref int width, ref int height, ref int mtrCount);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int PhotoCapture([In] int[] photoZone, [In, Out] IntPtr[] photo);
}
