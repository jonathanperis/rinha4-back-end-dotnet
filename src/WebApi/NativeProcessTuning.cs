using System.Runtime.InteropServices;

internal static class NativeProcessTuning
{
    private const int MclCurrent = 1;
    private const int MclFuture = 2;

    public static void TryLockMemoryFromEnvironment()
    {
        if (!Enabled("MLOCKALL"))
            return;

        if (!OperatingSystem.IsLinux())
            return;

        int result = mlockall(MclCurrent | MclFuture);
        Console.WriteLine(result == 0
            ? "mlockall enabled"
            : $"mlockall skipped errno={Marshal.GetLastPInvokeError()}");
    }

    private static bool Enabled(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return value is "1" or "true" or "TRUE" or "yes" or "YES";
    }

    [DllImport("*", EntryPoint = "mlockall", SetLastError = true)]
    private static extern int mlockall(int flags);
}
