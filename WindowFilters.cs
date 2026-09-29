using System.Diagnostics;

namespace DesktopSessionManager;

// Window kinds the scan skips by default. Each one is a menu toggle that lets it through.
[Flags]
internal enum WindowFilter
{
    None = 0,
    ToolWindows = 1,     // WS_EX_TOOLWINDOW
    LayeredWindows = 2,  // WS_EX_LAYERED (transparency, overlays)
    HiddenOwner = 4,     // owned by an invisible window, as HUDs often are
    NoDesktop = 8,       // not assigned to any virtual desktop; restored without a desktop move
    SystemWindows = 16,  // desktop-less shell, cloaked and Windows-folder windows
}

internal static class WindowFilters
{
    public static readonly (WindowFilter Filter, string Label)[] Toggles =
    [
        (WindowFilter.ToolWindows, "도구 창"),
        (WindowFilter.LayeredWindows, "레이어드(투명) 창"),
        (WindowFilter.HiddenOwner, "숨겨진 창에 딸린 창"),
        (WindowFilter.NoDesktop, "가상 데스크톱 밖의 창"),
        (WindowFilter.SystemWindows, "데스크톱 밖의 Windows 시스템 창"),
    ];
    private static string file = "";

    public static WindowFilter Included { get; private set; }

    public static bool Includes(WindowFilter f) => (Included & f) == f;

    public static void Load(string path)
    {
        file = path;
        try
        {
            if (File.Exists(file) && Enum.TryParse(File.ReadAllText(file).Trim(), out WindowFilter f)) Included = f;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Trace.WriteLine($"창 필터 설정을 읽지 못했습니다: {ex.Message}"); }
    }

    public static void Set(WindowFilter f, bool include)
    {
        Included = include ? Included | f : Included & ~f;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, Included.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Trace.WriteLine($"창 필터 설정을 저장하지 못했습니다: {ex.Message}"); }
    }

    public static bool AllowsStyle(long style) =>
        ((style & 0x80) == 0 || Includes(WindowFilter.ToolWindows))
        && ((style & 0x00080000) == 0 || Includes(WindowFilter.LayeredWindows));
}
