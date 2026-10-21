using System.Runtime.InteropServices;

namespace Clicky.Platform.Windows;

internal static partial class NativeMethods
{
    // LOGICAL_PROCESSOR_RELATIONSHIP
    internal const int RelationProcessorCore = 0;

    // PROCESS_INFORMATION_CLASS.ProcessPowerThrottling
    internal const int ProcessPowerThrottling = 4;
    internal const uint ProcessPowerThrottlingCurrentVersion = 1;
    internal const uint ProcessPowerThrottlingExecutionSpeed = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessPowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool GetLogicalProcessorInformationEx(int relationshipType, byte* buffer, ref uint returnedLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetProcessInformation(nint process, int processInformationClass, ref ProcessPowerThrottlingState processInformation, uint processInformationSize);

    [LibraryImport("kernel32.dll")]
    internal static partial nint GetCurrentProcess();

    // SetErrorMode flags.
    internal const uint SemFailCriticalErrors = 0x0001;
    internal const uint SemNoGpFaultErrorBox = 0x0002;

    [LibraryImport("kernel32.dll")]
    internal static partial uint SetErrorMode(uint mode);
}
