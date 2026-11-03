using System.Diagnostics;

namespace DesktopSessionManager;

internal static class AppLaunch
{
    private const string ElectronNodeMode = "ELECTRON_RUN_AS_NODE";

    public static ProcessStartInfo Create(string executable, string directory)
    {
        // VS Code extensions and their terminals can set ELECTRON_RUN_AS_NODE for
        // extension-host children. Inheriting it makes Electron EXEs run as Node instead
        // of apps (Code.exe rejects --new-window and exits with code 9).
        // Removing it requires launching without the shell, which cannot elevate or open
        // non-EXE targets (.lnk, .bat), so do that only when the variable is present.
        bool inherited = Environment.GetEnvironmentVariable(ElectronNodeMode) != null;
        bool isExe = string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase);
        var start = new ProcessStartInfo(executable) { UseShellExecute = !(inherited && isExe), WorkingDirectory = directory };
        if (!start.UseShellExecute) start.Environment.Remove(ElectronNodeMode);
        return start;
    }

    public static Process? Start(ProcessStartInfo start)
    {
        try { return Process.Start(start); }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 740 && !start.UseShellExecute)
        {
            // ERROR_ELEVATION_REQUIRED: only the shell can show the UAC prompt.
            start.UseShellExecute = true;
            return Process.Start(start);
        }
    }
}
