namespace Clicky.Platform.Windows.Power;

/// <summary>Error reporting for helper processes whose crash is an expected answer.</summary>
public static class ProcessErrorMode
{
    /// <summary>A crash ends the process quietly (exit code only), with no Windows Error Reporting dialog.</summary>
    public static void SuppressCrashDialogs() =>
        _ = NativeMethods.SetErrorMode(NativeMethods.SemFailCriticalErrors | NativeMethods.SemNoGpFaultErrorBox);
}
