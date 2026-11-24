using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

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
        // Blender is a console-subsystem EXE. A shell launch creates a console whose
        // close button terminates Blender too; CREATE_NO_WINDOW preserves its GUI.
        if (Path.GetFileName(executable).Equals("blender.exe", StringComparison.OrdinalIgnoreCase))
        {
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
        }
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

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int AssocQueryString(uint flags, int str, string assoc, string? extra, StringBuilder? output, ref uint length);
    private const int AssocStrExecutable = 2;
    private const uint AssocInitIgnoreUnknown = 0x400; // no "Open with" dialog host for unregistered types

    // The EXE Windows opens this file's type with (e.g. .blend → the installed blender.exe), or null.
    public static string? AssociatedExecutable(string file)
    {
        string extension = Path.GetExtension(file);
        if (extension == "") return null;
        uint length = 0;
        if (AssocQueryString(AssocInitIgnoreUnknown, AssocStrExecutable, extension, null, null, ref length) != 1 || length == 0) return null;
        var buffer = new StringBuilder((int)length);
        if (AssocQueryString(AssocInitIgnoreUnknown, AssocStrExecutable, extension, null, buffer, ref length) != 0) return null;
        string exe = buffer.ToString();
        return exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(exe) ? exe : null;
    }
}
