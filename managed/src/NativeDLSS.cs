using System;
using System.Runtime.InteropServices;
using System.Text;

namespace DuckovDLSS;

internal static class NativeDLSS
{
    private const string Library = "duckov-dlss-bridge";

    // Keep this layout byte-for-byte identical to ExecuteParams in the native bridge.
    [StructLayout(LayoutKind.Sequential)]
    internal struct ExecuteParams
    {
        internal IntPtr color;
        internal IntPtr depth;
        internal IntPtr motionVectors;
        internal IntPtr rawOutput;
        internal IntPtr sharpenedOutput;
        internal uint renderWidth;
        internal uint renderHeight;
        internal uint outputWidth;
        internal uint outputHeight;
        internal uint mode;
        internal byte hdr;
        internal byte reset;
        internal byte depthInverted;
        internal byte motionVectorsJittered;
        internal float jitterX;
        internal float jitterY;
        internal float motionScaleX;
        internal float motionScaleY;
        internal float sharpenStrength;
        internal ulong sequence;
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr LoadLibrary(string path);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr DLSSGetRenderEventFunc();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal static extern IntPtr DLSSCreateEventData(ref ExecuteParams parameters, string pluginPath);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void DLSSReleaseEventData(IntPtr eventData);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr DLSSCreateShutdownEventData();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern ulong DLSSGetCompletedSequence();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int DLSSGetLastSuccess();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern ulong DLSSGetFailureSequence();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    internal static extern void DLSSGetFailureStatus(StringBuilder buffer, uint capacity);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    internal static extern void DLSSGetStatus(StringBuilder buffer, uint capacity);

}
