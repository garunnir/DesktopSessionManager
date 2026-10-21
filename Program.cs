using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace DesktopSessionManager;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if(args.Length==2 && args[0]==Vda.ProbeArg) {Environment.Exit(Vda.RunProbe(args[1])); return;}
        ApplicationConfiguration.Initialize();
        Updates.CleanupOldFiles();
        Vda.Initialize(MainForm.DataRoot);
        Application.Run(new MainForm());
    }
}

public sealed class SavedWindow
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Path { get; set; } = "";
    public string Title { get; set; } = "";
    public int DesktopIndex { get; set; }
    public string DesktopName { get; set; } = "";
    public int Left { get; set; }
    public int Top { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Maximized { get; set; }
    public bool Minimized { get; set; }
    public bool Pinned { get; set; }
    public bool AllDesktops { get; set; } // HUD/overlay with no virtual desktop of its own
    public string ProjectPath { get; set; } = ""; // VS Code folder/.code-workspace or Unity project root
    public string ProjectKind { get; set; } = ""; // legacy v2 compatibility
    public string PluginId { get; set; } = ""; // data-driven v3 extension ID
    public List<string> OpenFiles { get; set; } = []; // plugins that reopen files instead of a project (Aseprite)
}
public sealed class Snapshot
{
    public int SchemaVersion { get; set; } = 1;
    public string Name { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public int DesktopCount { get; set; }
    public List<SavedWindow> Windows { get; set; } = [];
    public List<MonitorLayout> Layouts { get; set; } = [];
}
internal sealed class LiveWindow
{
    public required IntPtr Handle { get; init; }
    public required uint ProcessId { get; init; }
    public required SavedWindow Data { get; init; }
}

internal static class Native
{
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetShellWindow();
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out int value, int size);
    public static bool IsCloaked(IntPtr h) => DwmGetWindowAttribute(h,14,out int c,sizeof(int))==0 && c!=0; // DWMWA_CLOAKED
    [DllImport("user32.dll")] public static extern int GetWindowTextLengthW(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    [DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] public static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT placement);
    [DllImport("user32.dll")] public static extern bool SetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT placement);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
    [DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] public static extern bool QueryFullProcessImageNameW(IntPtr process, int flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
    [DllImport("VirtualDesktopAccessor.dll", EntryPoint="GetDesktopCount", CallingConvention=CallingConvention.Cdecl)] public static extern int GetDesktopCount();
    [DllImport("VirtualDesktopAccessor.dll", EntryPoint="GetWindowDesktopNumber", CallingConvention=CallingConvention.Cdecl)] public static extern int GetWindowDesktopNumber(IntPtr hwnd);
    [DllImport("VirtualDesktopAccessor.dll", EntryPoint="MoveWindowToDesktopNumber", CallingConvention=CallingConvention.Cdecl)] public static extern int MoveWindowToDesktopNumber(IntPtr hwnd, int desktop);
    [DllImport("VirtualDesktopAccessor.dll", EntryPoint="CreateDesktop", CallingConvention=CallingConvention.Cdecl)] public static extern int CreateDesktop();
    [DllImport("VirtualDesktopAccessor.dll", EntryPoint="GetDesktopName", CallingConvention=CallingConvention.Cdecl)] public static extern int GetDesktopName(int desktop, IntPtr buffer, UIntPtr length);
    [DllImport("VirtualDesktopAccessor.dll", EntryPoint="IsPinnedWindow", CallingConvention=CallingConvention.Cdecl)] public static extern int IsPinnedWindow(IntPtr hwnd);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct WINDOWPLACEMENT
    {
        public int length, flags, showCmd;
        public POINT ptMinPosition, ptMaxPosition;
        public RECT rcNormalPosition, rcDevice;
        public static WINDOWPLACEMENT Init() => new() { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
    }
    public static string GetDesktopLabel(int i)
    {
        IntPtr buf = Marshal.AllocHGlobal(512);
        try
        {
            for (int b=0;b<512;b++) Marshal.WriteByte(buf,b,0);
            return GetDesktopName(i,buf,(UIntPtr)512) == -1 ? "" : Marshal.PtrToStringUTF8(buf) ?? "";
        }
        catch (EntryPointNotFoundException) { return ""; }
        finally { Marshal.FreeHGlobal(buf); }
    }
    public static string GetPath(uint pid)
    {
        IntPtr p = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if (p == IntPtr.Zero) return "";
        try
        {
            var s = new StringBuilder(32768); int len = s.Capacity;
            return QueryFullProcessImageNameW(p,0,s,ref len) ? s.ToString() : "";
        }
        finally { CloseHandle(p); }
    }
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, IntPtr info, int length, out int returnLength);
    [DllImport("shell32.dll", CharSet=CharSet.Unicode)] private static extern IntPtr CommandLineToArgvW(string commandLine, out int argc);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr h);
    public static string[] GetCommandLine(uint pid)
    {
        IntPtr p = OpenProcess(0x1000, false, pid);
        if (p == IntPtr.Zero) return [];
        try
        {
            NtQueryInformationProcess(p,60,IntPtr.Zero,0,out int need); // ProcessCommandLineInformation
            if (need <= 0 || need > 1<<20) return [];
            IntPtr buf = Marshal.AllocHGlobal(need);
            try
            {
                if (NtQueryInformationProcess(p,60,buf,need,out _) != 0) return [];
                int bytes = (ushort)Marshal.ReadInt16(buf); // UNICODE_STRING.Length
                string cmd = Marshal.PtrToStringUni(Marshal.ReadIntPtr(buf,IntPtr.Size), bytes/2);
                IntPtr argv = CommandLineToArgvW(cmd, out int argc);
                if (argv == IntPtr.Zero) return [];
                try { return Enumerable.Range(0,argc).Select(i=>Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv,i*IntPtr.Size)) ?? "").ToArray(); }
                finally { LocalFree(argv); }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        finally { CloseHandle(p); }
    }
}

internal static class Scanner
{
    public static List<LiveWindow> Scan()
    {
        List<LiveWindow> found=[];
        Native.EnumWindows((h, _) =>
        {
            try
            {
                if (!Native.IsWindowVisible(h)) return true;
                long style = Native.GetWindowLongPtr(h,-20).ToInt64(); // GWL_EXSTYLE
                IntPtr owner = Native.GetWindow(h,4); // GW_OWNER
                // Dialogs have a visible owner and stay excluded; HUDs often hang off a hidden one.
                if (owner != IntPtr.Zero && (!WindowFilters.Includes(WindowFilter.HiddenOwner) || Native.IsWindowVisible(owner))) return true;
                int len = Native.GetWindowTextLengthW(h);
                if (len == 0) return true;
                var title=new StringBuilder(len+1); Native.GetWindowTextW(h,title,title.Capacity);
                Native.GetWindowThreadProcessId(h,out uint pid);
                if (pid == (uint)Environment.ProcessId) return true;
                string path=Native.GetPath(pid);
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return true;
                if (!WindowFilters.AllowsStyle(style)) return true;
                int desktop=Native.GetWindowDesktopNumber(h);
                bool allDesktops = desktop < 0;
                if (allDesktops && !WindowFilters.Includes(WindowFilter.NoDesktop)) return true;
                // Most desktop-less windows are the shell or Windows' own (often cloaked) UI.
                if (allDesktops && !WindowFilters.Includes(WindowFilter.SystemWindows) && (h == Native.GetShellWindow() || Native.IsCloaked(h)
                    || path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows)+"\\",StringComparison.OrdinalIgnoreCase))) return true;
                var wp=Native.WINDOWPLACEMENT.Init();
                if (!Native.GetWindowPlacement(h,ref wp)) return true;
                var r=wp.rcNormalPosition;
                if (r.Right<=r.Left || r.Bottom<=r.Top) return true;
                var data=new SavedWindow
                {
                    Path=path, Title=title.ToString(), DesktopIndex=desktop,
                    DesktopName=Native.GetDesktopLabel(desktop), Left=r.Left, Top=r.Top,
                    Width=r.Right-r.Left, Height=r.Bottom-r.Top,
                    Maximized=wp.showCmd==3, Minimized=wp.showCmd is 2 or 6 or 7,
                    Pinned=!allDesktops && Native.IsPinnedWindow(h)==1, AllDesktops=allDesktops
                };
                found.Add(new LiveWindow{Handle=h,ProcessId=pid,Data=data});
            }
            catch { /* ignore windows that vanished while scanning */ }
            return true;
        },IntPtr.Zero);
        return found;
    }
}

public sealed partial class MainForm : Form
{
    private readonly ListView windows = new() { Dock=DockStyle.Fill, View=View.Details, CheckBoxes=true, FullRowSelect=true, GridLines=true };
    private readonly ComboBox profiles = new() { DropDownStyle=ComboBoxStyle.DropDownList, Width=225 };
    private readonly TextBox name = new() { Width=150, PlaceholderText="프로필 이름" };
    private readonly CheckBox createDesktops = new() { Text="부족한 가상 데스크톱 생성 (선택)", AutoSize=true };
    private readonly TextBox log = new() { Multiline=true, ReadOnly=true, ScrollBars=ScrollBars.Vertical, Dock=DockStyle.Fill };
    private readonly Button restore = new() { Text="선택한 프로필 복원", Width=150 };
    private readonly Button setProject = new() { Text="선택 창 프로젝트 지정", Width=160 };
    private readonly TextBox find = new() { Width=260, PlaceholderText="찾기 (Ctrl+F) — 프로그램·제목·경로" };
    private readonly Label checkCount = new() { AutoSize=true, Margin=new Padding(8,7,3,0) };
    private static readonly string[] ColumnNames=["프로그램","창 제목","데스크톱","창 위치/크기","프로젝트 경로","EXE 경로","복원 연동 상태"];
    private List<LiveWindow> live=[];
    // All scanned rows; the list shows only those matching the search box.
    private List<ListViewItem> allItems=[];
    // Checked state of rows hidden by the search; visible rows are read from the list itself.
    private readonly Dictionary<ListViewItem,bool> checks=[];
    private bool countPending;
#if DEBUG
    // Keep test profiles apart from real ones.
    private const string AppDataName="DesktopSessionManager-Dev";
#else
    private const string AppDataName="DesktopSessionManager";
#endif
    internal static readonly string DataRoot=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),AppDataName);
    private readonly string folder=Path.Combine(DataRoot,"Profiles");
    private readonly ProjectCache projectCache=new(Path.Combine(DataRoot,"project-cache.json"));
    private bool apiReady;
    private readonly PluginCatalog pluginsCatalog = new();
    // Right half: the profile being edited. Windows dragged from the list are copied into it.
    private readonly ListView editor = new() { Dock=DockStyle.Fill, View=View.Details, FullRowSelect=true, GridLines=true, HideSelection=false };
    private readonly Label editorHeader = new() { AutoSize=true, Margin=new Padding(8,10,3,0) };
    // The editor shows the profile either as desktop cards (default) or as the detailed list.
    private readonly DesktopBoard board = new() { Dock=DockStyle.Fill };
    private readonly TabControl views = new() { Dock=DockStyle.Fill };
    private readonly TabPage boardPage = new("화면"), listPage = new("목록");
    private SplitContainer livePanel = null!;
    private readonly Button liveToggle = new() { AutoSize=true };
    private readonly Button saveProfile = new() { Text="프로필 저장", Width=100 };
    private readonly List<SavedWindow> editing=[];
    private string? loadedProfile;
    private bool editorDirty, suppressProfileLoad;
    private readonly LinkLabel updateLink = new() { AutoSize=true, Visible=false, Margin=new Padding(12,7,3,0) };
    private Updates.Release? latest;
    private bool updating;
    // Version the user declined at the startup prompt; they are not asked again for it.
    private static readonly string UpdateSkipFile=Path.Combine(DataRoot,"update-skip.txt");

    public MainForm()
    {
        Text="Desktop Session Manager — 가상 데스크톱별 저장/복원";
        if(Updates.Enabled) Text+=$" v{Updates.Current}";
#if DEBUG
        Text+=" [DEV]";
#endif
        Width=1400; Height=760; MinimumSize=new System.Drawing.Size(1000,560); StartPosition=FormStartPosition.CenterScreen;
        Font=new System.Drawing.Font("Malgun Gothic",9);
        int[] widths=[120,240,110,150,200,280,230];
        for(int i=0;i<ColumnNames.Length;i++) windows.Columns.Add(ColumnNames[i],widths[i]);
        var scan=new Button{Text="현재 창 새로고침",Width=130};
        var addChecked=new Button{Text="◀ 체크된 창 프로필에 추가",Width=190};
        var liveTitle=new Label{Text="현재 열린 창",AutoSize=true,Font=new System.Drawing.Font(Font,System.Drawing.FontStyle.Bold),Margin=new Padding(8,10,3,0)};
        var row1=new FlowLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,WrapContents=true,Padding=new Padding(4,0,4,0)};
        row1.Controls.AddRange([addChecked,scan,setProject]);
        var checkAll=new Button{Text="모두 체크",Width=90};
        var uncheckAll=new Button{Text="모두 해제",Width=90};
        var rowFind=new FlowLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,WrapContents=true,Padding=new Padding(8,0,8,4)};
        rowFind.Controls.AddRange([find,checkAll,uncheckAll,checkCount]);
        // Main area: the profile (desktop cards). Right side panel: currently open windows to drag in, shown on demand.
        var livePane=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=4};
        livePane.RowStyles.Add(new RowStyle(SizeType.AutoSize)); livePane.RowStyles.Add(new RowStyle(SizeType.AutoSize)); livePane.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        livePane.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        livePane.Controls.Add(liveTitle,0,0); livePane.Controls.Add(row1,0,1); livePane.Controls.Add(rowFind,0,2); livePane.Controls.Add(windows,0,3);
        // FixedPanel.Panel2 keeps the side panel's width when the window is resized; the profile takes the rest.
        var split=new SplitContainer{Dock=DockStyle.Fill,Orientation=Orientation.Vertical,FixedPanel=FixedPanel.Panel2};
        split.Panel1.Controls.Add(BuildProfileEditor()); split.Panel2.Controls.Add(livePane);
        livePanel=split;
        Load+=(_,_)=>{split.Panel1MinSize=420;split.Panel2MinSize=320;split.SplitterDistance=split.Width*58/100;
            // An empty profile has nothing to show yet, so open the source of windows.
            ShowLiveWindows(settings.ShowLiveWindows || editing.Count==0);};
        var layout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=3};
        layout.RowStyles.Add(new RowStyle(SizeType.Percent,72)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute,25));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent,28));
        layout.Controls.Add(split,0,0);
        layout.Controls.Add(new Label{Text="플러그인(JSON) 설정은 재빌드 없이 새로고침 가능합니다. 프로젝트 경로는 창을 선택해 지정하며 미저장 변경사항은 복원하지 않습니다.",Dock=DockStyle.Fill,Padding=new Padding(8,4,0,0)},0,1);
        layout.Controls.Add(log,0,2);
        WindowFilters.Load(Path.Combine(DataRoot,"window-filters.txt"));
        var menu=BuildMenu();
        Controls.Add(layout); Controls.Add(menu); MainMenuStrip=menu;
        scan.Click+=(_,_)=>RefreshWindows(); setProject.Click+=(_,_)=>SetProject(); addChecked.Click+=(_,_)=>AddChecked();
        FormClosing+=OnClosing;
        restore.Click+=async (_,_)=>await RestoreProfile();
        checkAll.Click+=(_,_)=>SetVisibleChecked(true); uncheckAll.Click+=(_,_)=>SetVisibleChecked(false);
        find.TextChanged+=(_,_)=>ApplyFilter();
        find.KeyDown+=(_,e)=>{if(e.KeyCode==Keys.Escape){find.Clear();e.SuppressKeyPress=true;}};
        KeyPreview=true;
        KeyDown+=(_,e)=>{if(e.Control && e.KeyCode==Keys.F){find.Focus();find.SelectAll();e.SuppressKeyPress=true;}};
        windows.ColumnClick+=(_,e)=>SortBy(e.Column);
        // ItemChecked also fires while the list creates its handle (e.Item can be null then), so only schedule a recount.
        windows.ItemChecked+=(_,_)=>{if(IsHandleCreated && !countPending){countPending=true;BeginInvoke(UpdateCheckCount);}};
        Directory.CreateDirectory(folder); RefreshProfiles(); LoadProjectCache();
        if(profiles.Items.Count>0) profiles.SelectedIndex=0; else UpdateEditorHeader();
        foreach(var error in pluginsCatalog.Reload()) Write("확장 설정 오류: "+error);
        Write($"프로젝트 복원 확장 {pluginsCatalog.All.Count}개 로드됨. 설정 폴더: {pluginsCatalog.Folder}");
        Write(Vda.Status);
        if(Vda.Loaded)
        {
            try
            {
                int count=Native.GetDesktopCount();
                apiReady=count>0;
                Write(apiReady ? $"가상 데스크톱 연결 성공: {count}개" : "가상 데스크톱 조회 실패(-1). 이 Windows 빌드와 DLL이 맞지 않을 수 있습니다. 앱을 업데이트하세요.");
            }
            catch (EntryPointNotFoundException ex)
            {
                Write("VirtualDesktopAccessor 함수 누락: " + ex.Message);
            }
        }
        scan.Enabled=restore.Enabled=addChecked.Enabled=apiReady;
        saveProfile.Enabled=true;
        InitializeUsability();
        if(apiReady) RefreshWindows();
        updateLink.LinkClicked+=async (_,_)=>{if(latest!=null) await InstallUpdate(latest);};
        Shown+=async (_,_)=>await CheckForUpdate(false);
    }
    // At startup (manual=false) failures only go to the log and a declined version is not offered again.
    private async Task CheckForUpdate(bool manual)
    {
        if(!Updates.Enabled)
        {
            if(manual) MessageBox.Show(this,"개발 빌드(0.0.0)는 업데이트를 확인하지 않습니다.","업데이트");
            else if(!Vda.Loaded) Write("최신 버전: "+Updates.ReleasesUrl);
            return;
        }
        try {latest=await Updates.CheckAsync();}
        catch(Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Write("업데이트 확인 실패: "+ex.Message);
            if(!Vda.Loaded) Write("최신 버전: "+Updates.ReleasesUrl);
            if(manual) MessageBox.Show(this,"업데이트 확인 실패: "+ex.Message,"업데이트",MessageBoxButtons.OK,MessageBoxIcon.Warning);
            return;
        }
        if(latest==null)
        {
            if(!Vda.Loaded) Write("최신 버전도 이 Windows 빌드를 지원하지 않습니다. VirtualDesktopAccessor 새 릴리즈 이후 업데이트가 나옵니다.");
            if(manual) MessageBox.Show(this,$"최신 버전(v{Updates.Current})을 사용 중입니다.","업데이트");
            return;
        }
        var version=latest.Version;
        Write($"새 버전 v{version}: {latest.PageUrl}");
        updateLink.Text=$"새 버전 v{version} 설치"; updateLink.Visible=true;
        if(!manual && Vda.Loaded && ReadSkippedVersion()==version) return;
        string question=!Vda.Loaded
            ? $"이 Windows 빌드용 VirtualDesktopAccessor가 없습니다.\n새 버전 v{version}에서 지원될 수 있습니다. 지금 업데이트할까요?"
            : $"새 버전 v{version}이(가) 있습니다. (현재 v{Updates.Current})\n지금 업데이트하고 다시 시작할까요?";
        if(MessageBox.Show(this,question,"Desktop Session Manager",MessageBoxButtons.YesNo,Vda.Loaded?MessageBoxIcon.Information:MessageBoxIcon.Warning)==DialogResult.Yes)
            await InstallUpdate(latest);
        else if(!manual) SaveSkippedVersion(version);
    }
    private async Task InstallUpdate(Updates.Release release)
    {
        if(updating) return;
        if(Updates.CannotInstallReason(release) is {} reason)
        {
            Write($"자동 업데이트 불가: {reason}. 다운로드 페이지를 엽니다.");
            OpenUrl(release.PageUrl);
            return;
        }
        updating=true; updateLink.Enabled=false;
        try
        {
            Write($"v{release.Version} 다운로드 중...");
            string files=await Updates.DownloadAsync(release);
            Write("SHA256 확인 완료.");
            if(!ConfirmDiscard()) {Write("업데이트 취소. 메뉴의 [도움말] → [업데이트 확인]으로 다시 설치할 수 있습니다.");return;}
            Updates.Apply(files);
        }
        catch(Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Write("업데이트 실패: "+ex.Message);
            if(MessageBox.Show(this,$"업데이트 실패: {ex.Message}\n다운로드 페이지를 열까요?","업데이트",MessageBoxButtons.YesNo,MessageBoxIcon.Warning)==DialogResult.Yes)
                OpenUrl(release.PageUrl);
            return;
        }
        finally {updating=false; updateLink.Enabled=true;}
        Write($"v{release.Version} 설치 완료. 다시 시작합니다.");
        Process.Start(new ProcessStartInfo(Updates.ExePath){UseShellExecute=false,WorkingDirectory=Path.GetDirectoryName(Updates.ExePath)!});
        // Unsaved edits were already confirmed above.
        editorDirty=false;
        exitRequested=true;
        Close();
    }
    private static Version? ReadSkippedVersion()
    {
        try {return File.Exists(UpdateSkipFile) && Version.TryParse(File.ReadAllText(UpdateSkipFile).Trim(),out var v) ? v : null;}
        catch(Exception ex) when (ex is IOException or UnauthorizedAccessException) {return null;}
    }
    private void SaveSkippedVersion(Version v)
    {
        try {Directory.CreateDirectory(DataRoot); File.WriteAllText(UpdateSkipFile,v.ToString());}
        catch(Exception ex) when (ex is IOException or UnauthorizedAccessException) {Write("업데이트 설정 저장 실패: "+ex.Message);}
    }
    private static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url){UseShellExecute=true});
    private void Write(string s) => log.AppendText($"[{DateTime.Now:HH:mm:ss}] {s}{Environment.NewLine}");
    private static string PluginIdOf(SavedWindow w) =>
        w.PluginId!="" ? w.PluginId : w.ProjectKind switch {"vscode"=>"vscode", "unity"=>"unity",_=>""};
    private void LoadProjectCache()
    {
        bool seed=!projectCache.Exists;
        projectCache.Load();
        if(!seed) return;
        // First run with the cache: pick up the projects already assigned in saved profiles.
        foreach(var f in Directory.GetFiles(folder,"*.json"))
        {
            try
            {
                var data=JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(f,Encoding.UTF8));
                foreach(var w in data?.Windows ?? []) projectCache.Remember(PluginIdOf(w),w.ProjectPath);
            }
            catch(Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        }
        SaveProjectCache();
    }
    private void SaveProjectCache()
    {
        try { projectCache.Save(); }
        catch(Exception ex) when (ex is IOException or UnauthorizedAccessException) { Write("프로젝트 경로 캐시 저장 실패: "+ex.Message); }
    }
    private void RefreshWindows()
    {
        // Detect current projects first: VS Code can switch folders without replacing its window.
        var previous=live.Where(x=>x.Data.ProjectPath!="" || x.Data.OpenFiles.Count>0).ToDictionary(x=>x.Handle);
        live=Scanner.Scan(); windows.Items.Clear(); allItems=[]; checks.Clear();
        var detector=new ProjectDetector(projectCache); int detected=0,noBridge=0;
        foreach (var w in live)
        {
            var d=w.Data;
            var plugin=pluginsCatalog.ForExe(d.Path);
            // Open tabs change while the app runs, so a fresh list wins over an earlier assignment.
            var files=plugin?.OpensFiles==true ? ProjectDetector.AsepriteOpenFiles(w) : null;
            if(plugin?.OpensFiles==true && files==null) noBridge++;
            if(files!=null)
            {d.PluginId=plugin!.Id;d.OpenFiles=files;detected++;}
            else if(plugin is {OpensFiles:false} && detector.Detect(w,plugin) is {} found)
            {d.PluginId=plugin.Id;d.ProjectPath=found;detected++;}
            // Retain manual assignments only while the same process/window title remains.
            // If detection fails after switching projects, leave the path empty rather than stale.
            else if(plugin?.OpensFiles!=true && previous.TryGetValue(w.Handle,out var old) && old.ProcessId==w.ProcessId
                && old.Data.Path==d.Path && old.Data.Title==d.Title)
            {d.PluginId=old.Data.PluginId;d.ProjectPath=old.Data.ProjectPath;d.OpenFiles=old.Data.OpenFiles;}
            var item=new ListViewItem(System.IO.Path.GetFileNameWithoutExtension(d.Path)) {Checked=!d.Pinned, Tag=w};
            item.SubItems.Add(d.Title);
            item.SubItems.Add(DesktopLabel(d));
            item.SubItems.Add(RectLabel(d)); item.SubItems.Add(d.ProjectPath); item.SubItems.Add(d.Path);
            item.SubItems.Add(plugin==null ? "프로그램 실행만 지원" : plugin.OpensFiles
                ? files==null ? "연결 미확인: 정보 없음 / 5분 이상 지남" : $"열린 파일 정보 수신: {files.Count}개"
                : d.ProjectPath=="" ? "프로젝트 감지 실패: 직접 지정 필요" : "프로젝트 경로 확인됨");
            item.UseItemStyleForSubItems=false; ShowProject(item);
            allItems.Add(item); checks[item]=item.Checked;
        }
        ApplyFilter();
        Write($"일반 창 {live.Count}개 검색 (프로젝트 경로 자동 감지 {detected}개). 다른 데스크톱에 있는 최소화 창도 포함할 수 있습니다. 저장 전 목록을 확인하세요.");
        if(noBridge>0) Write($"Aseprite 창 {noBridge}개의 연동 정보가 없거나 5분 이상 지났습니다. Aseprite에서 탭 전환 후 새로고침하세요. 계속 확인되지 않으면 integrations\\aseprite 확장을 확인하거나 파일을 직접 지정하세요.");
    }
    private void ApplyFilter()
    {
        // Every space-separated term must appear in some column.
        string[] terms=find.Text.Split(' ',StringSplitOptions.RemoveEmptyEntries);
        CaptureChecks(); windows.BeginUpdate();
        try
        {
            windows.Items.Clear();
            windows.Items.AddRange(allItems.Where(x=>terms.All(t=>x.SubItems.Cast<ListViewItem.ListViewSubItem>()
                .Any(s=>s.Text.Contains(t,StringComparison.CurrentCultureIgnoreCase)))).ToArray());
            foreach(ListViewItem x in windows.Items) x.Checked=checks[x];
        }
        finally { windows.EndUpdate(); }
        UpdateCheckCount();
    }
    private void CaptureChecks()
    {
        foreach(ListViewItem x in windows.Items) checks[x]=x.Checked;
    }
    private void SetVisibleChecked(bool on)
    {
        windows.BeginUpdate();
        try { foreach(ListViewItem x in windows.Items) x.Checked=on; }
        finally { windows.EndUpdate(); }
        UpdateCheckCount();
    }
    private void UpdateCheckCount()
    {
        countPending=false; CaptureChecks();
        int all=checks.Values.Count(c=>c), hidden=all-windows.CheckedItems.Count;
        checkCount.Text=$"표시 {windows.Items.Count}/{allItems.Count} · 체크 {all}"+(hidden>0?$" (검색에 가려진 체크 {hidden}개도 저장됨)":"");
    }
    private void SortBy(int column)
    {
        // Clicking the sorted column again flips the direction.
        bool descending=windows.ListViewItemSorter is WindowSorter s && s.Column==column && !s.Descending;
        windows.ListViewItemSorter=new WindowSorter(column,descending);
        for(int i=0;i<ColumnNames.Length;i++) windows.Columns[i].Text=ColumnNames[i]+(i==column ? descending?" ▼":" ▲" : "");
    }
    private sealed class WindowSorter(int column,bool descending) : System.Collections.IComparer
    {
        public int Column=>column; public bool Descending=>descending;
        public int Compare(object? a,object? b)
        {
            var x=(ListViewItem)a!; var y=(ListViewItem)b!;
            var dx=((LiveWindow)x.Tag!).Data; var dy=((LiveWindow)y.Tag!).Data;
            int r=column switch
            {
                2=>(dx.Pinned||dx.AllDesktops?int.MaxValue:dx.DesktopIndex).CompareTo(dy.Pinned||dy.AllDesktops?int.MaxValue:dy.DesktopIndex), // pinned last
                3=>dx.Left!=dy.Left ? dx.Left.CompareTo(dy.Left) : dx.Top.CompareTo(dy.Top),
                _=>string.Compare(x.SubItems[column].Text,y.SubItems[column].Text,StringComparison.CurrentCultureIgnoreCase)
            };
            if(r==0) r=string.Compare(dx.Title,dy.Title,StringComparison.CurrentCultureIgnoreCase);
            return descending?-r:r;
        }
    }
    private const string UnassignedProject="(미지정 — 복원 시 프로젝트 안 열림)";
    // Windows of a plugin-covered app without a project would restore as a bare EXE launch.
    // File-based apps with nothing open are fine to restore bare.
    private bool MissingProject(SavedWindow d) => d.ProjectPath=="" && pluginsCatalog.ForExe(d.Path) is {OpensFiles:false};
    private static string FilesLabel(List<string> files) =>
        $"파일 {files.Count}개: "+string.Join(", ",files.Select(x=>Path.GetFileName(x)));
    private static string DesktopLabel(SavedWindow d) => d.AllDesktops ? "모든 데스크톱" : $"{d.DesktopIndex+1}: {d.DesktopName}" + (d.Pinned?" [고정]":"");
    private static string RectLabel(SavedWindow d) => $"{d.Left},{d.Top} / {d.Width}×{d.Height}";
    private string ProjectLabel(SavedWindow d) =>
        MissingProject(d) ? UnassignedProject : d.OpenFiles.Count>0 ? FilesLabel(d.OpenFiles) : d.ProjectPath;
    private void ShowProject(ListViewItem item)
    {
        var d=((LiveWindow)item.Tag!).Data; var cell=item.SubItems[4];
        cell.Text=ProjectLabel(d);
        cell.ForeColor=MissingProject(d) ? System.Drawing.Color.Firebrick : windows.ForeColor;
    }
    private void ReloadPlugins()
    {
        var errors=pluginsCatalog.Reload();
        Write($"확장 설정 새로고침: {pluginsCatalog.All.Count}개, 오류 {errors.Count}개");
        foreach(var error in errors) Write("확장 설정 오류: "+error);
        if(apiReady) RefreshWindows();
        FillEditor(); // project warnings depend on the plugin rules
    }
    private void ManagePlugins()
    {
        using var dialog=new Form{Text="확장 기능 관리 — JSON", Width=690,Height=445,StartPosition=FormStartPosition.CenterParent,Font=Font};
        var list=new ListBox{Dock=DockStyle.Fill};
        var bottom=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=46,Padding=new Padding(5)};
        var add=new Button{Text="추가",Width=85}; var edit=new Button{Text="선택 편집",Width=105};
        var reload=new Button{Text="새로고침",Width=100};var folderButton=new Button{Text="플러그인 폴더 열기",Width=160};
        bottom.Controls.AddRange([add,edit,reload,folderButton]);dialog.Controls.Add(list);dialog.Controls.Add(bottom);
        void Fill(){list.Items.Clear();foreach(var x in pluginsCatalog.All)list.Items.Add(new PluginItem(x));}
        Fill();
        add.Click+=(_,_)=>{using var ed=new PluginEditor();if(ed.ShowDialog(dialog)!=DialogResult.OK || ed.Result==null)return;
            try{pluginsCatalog.Save(ed.Result, "");Fill();Write("확장 추가: "+ed.Result.Name);}catch(Exception ex){MessageBox.Show(ex.Message,"저장 실패");}};
        edit.Click+=(_,_)=>{if(list.SelectedItem is not PluginItem selected)return;using var ed=new PluginEditor(selected.Plugin);
            if(ed.ShowDialog(dialog)!=DialogResult.OK || ed.Result==null)return;
            try{pluginsCatalog.Save(ed.Result,selected.Plugin.Id);Fill();Write("확장 편집: "+ed.Result.Name);}catch(Exception ex){MessageBox.Show(ex.Message,"저장 실패");}};
        reload.Click+=(_,_)=>{ReloadPlugins();Fill();};
        folderButton.Click+=(_,_)=>OpenFolder(pluginsCatalog.Folder);
        dialog.FormClosed+=(_,_)=>ReloadPlugins();dialog.ShowDialog(this);
    }
    private sealed class PluginItem(AppPlugin p)
    {
        public AppPlugin Plugin {get;}=p;
        public override string ToString()=> $"{Plugin.Name} [{Plugin.Id}] — {string.Join(", ",Plugin.ExecutableNames)}";
    }
    private MenuStrip BuildMenu()
    {
        static ToolStripMenuItem Item(string text,Action click,Keys keys=Keys.None)
        {
            var item=new ToolStripMenuItem(text){ShortcutKeys=keys};
            item.Click+=(_,_)=>click();
            return item;
        }
        var file=new ToolStripMenuItem("파일(&F)");
        file.DropDownItems.AddRange([
            Item("새 프로필",NewProfile,Keys.Control|Keys.N),
            Item("프로필 저장",SaveEditedProfile,Keys.Control|Keys.S),
            new ToolStripSeparator(),
            Item("편집 중인 프로필 JSON 위치 열기",OpenProfileJson),
            Item("프로필 폴더 열기",()=>OpenFolder(folder)),
            Item("확장(플러그인) JSON 폴더 열기",()=>OpenFolder(pluginsCatalog.Folder)),
            Item("프로젝트 경로 캐시 JSON 위치 열기",()=>ShowInExplorer(Path.Combine(DataRoot,"project-cache.json"))),
            Item("데이터 폴더 열기",()=>OpenFolder(DataRoot)),
            new ToolStripSeparator(),
            Item("트레이 / 자동 저장 설정",UsabilitySettings),
            Item("백업 / 자동 저장 복구",ShowHistory),
            Item("즐겨찾기 전환",()=>{if(loadedProfile is {} p){if(!settings.Favorites.Remove(p))settings.Favorites.Add(p);SaveSettings();}}),
            Item("종료",ExitApp)]);
        var plugins=new ToolStripMenuItem("확장(&E)");
        plugins.DropDownItems.AddRange([
            Item("확장 기능 관리...",ManagePlugins),
            Item("확장 새로고침",ReloadPlugins)]);
        var view=new ToolStripMenuItem("보기(&V)");
        view.DropDownItems.Add(new ToolStripLabel("창 목록에 포함"){ForeColor=System.Drawing.SystemColors.GrayText});
        foreach(var (filter,label) in WindowFilters.Toggles)
        {
            var toggle=new ToolStripMenuItem(label){CheckOnClick=true,Checked=WindowFilters.Includes(filter)};
            toggle.CheckedChanged+=(_,_)=>{WindowFilters.Set(filter,toggle.Checked);if(apiReady)RefreshWindows();};
            view.DropDownItems.Add(toggle);
        }
        // Keep the menu open so several filters can be toggled in a row.
        view.DropDown.Closing+=(_,e)=>{if(e.CloseReason==ToolStripDropDownCloseReason.ItemClicked)e.Cancel=true;};
        var help=new ToolStripMenuItem("도움말(&H)");
        help.DropDownItems.AddRange([
            Item("업데이트 확인",async ()=>await CheckForUpdate(true)),
            Item("릴리즈 페이지 열기",()=>OpenUrl(Updates.ReleasesUrl))]);
        var menu=new MenuStrip{Dock=DockStyle.Top};
        menu.Items.AddRange([file,view,plugins,help]);
        return menu;
    }
    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(path){UseShellExecute=true});
    }
    // Opens Explorer with the file selected, or the containing folder when the file does not exist yet.
    private static void ShowInExplorer(string file)
    {
        if(File.Exists(file)) Process.Start(new ProcessStartInfo("explorer.exe",$"/select,\"{file}\""));
        else OpenFolder(Path.GetDirectoryName(file)!);
    }
    private Control BuildProfileEditor()
    {
        editorHeader.Font=new System.Drawing.Font(Font,System.Drawing.FontStyle.Bold);
        var newProfile=new Button{Text="새 프로필",Width=90};
        var top=new FlowLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,WrapContents=true,Padding=new Padding(4)};
        top.Controls.AddRange([new Label{Text="프로필:",AutoSize=true,Margin=new Padding(3,7,3,0)},profiles,newProfile,restore,createDesktops,liveToggle,updateLink]);
        liveToggle.Click+=(_,_)=>{ShowLiveWindows(livePanel.Panel2Collapsed);settings.ShowLiveWindows=!livePanel.Panel2Collapsed;SaveSettings();};
        var hint=new Label{Text="[현재 창 추가]로 열린 창 목록을 펼쳐, 창을 데스크톱 카드의 화면 위에 끌어다 놓으세요. 놓은 데스크톱·모니터에 배치됩니다. 카드 제목을 끌면 데스크톱 순서가 바뀝니다.",
            AutoSize=true,ForeColor=System.Drawing.SystemColors.GrayText,Margin=new Padding(8,0,8,6),MaximumSize=new System.Drawing.Size(560,0)};
        string[] columns=["프로그램","창 제목","데스크톱","창 위치/크기","프로젝트 경로"]; int[] widths=[110,200,100,140,200];
        for(int i=0;i<columns.Length;i++) editor.Columns.Add(columns[i],widths[i]);
        editor.KeyDown+=(_,e)=>{if(e.KeyCode==Keys.Delete){RemoveFromEditor();e.Handled=true;}};
        var remove=new Button{Text="선택 제거",Width=90}; var openJson=new Button{Text="JSON 위치 열기",Width=115};
        var bottom=new FlowLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,WrapContents=true,Padding=new Padding(4)};
        var editSaved=new Button{Text="선택 편집",Width=90}; editSaved.Click+=(_,_)=>EditSelected();
        var refreshSaved=new Button{Text="현재 배치로 갱신",Width=135}; refreshSaved.Click+=(_,_)=>UpdateFromCurrent();
        editor.DoubleClick+=(_,_)=>EditSelected();
        bottom.Controls.AddRange([editSaved,refreshSaved,new Label{Text="이름:",AutoSize=true,Margin=new Padding(3,7,3,0)},name,saveProfile,remove,openJson]);
        var panel=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=5};
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); panel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent,100)); panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        board.Changed+=()=>{editorDirty=true;FillEditor();};
        board.EditRequested+=w=>EditWindow(w); board.RemoveRequested+=RemoveFromEditor;
        board.Dropped+=(dropped,place)=>AddToEditor(dropped,place); board.IssueOf=PathIssue;
        boardPage.Controls.Add(board); listPage.Controls.Add(editor);
        views.TabPages.AddRange([boardPage,listPage]);
        panel.Controls.Add(editorHeader,0,0); panel.Controls.Add(top,0,1); panel.Controls.Add(hint,0,2); panel.Controls.Add(views,0,3); panel.Controls.Add(bottom,0,4);
        panel.SizeChanged+=(_,_)=>hint.MaximumSize=new System.Drawing.Size(Math.Max(200,panel.ClientSize.Width-20),0);
        profiles.SelectedIndexChanged+=(_,_)=>OnProfileSelected();
        newProfile.Click+=(_,_)=>NewProfile(); remove.Click+=(_,_)=>RemoveFromEditor();
        saveProfile.Click+=(_,_)=>SaveEditedProfile(); openJson.Click+=(_,_)=>OpenProfileJson();
        name.TextChanged+=(_,_)=>UpdateEditorHeader();
        EnableWindowDrop(panel,panel);
        windows.ItemDrag+=(_,e)=>
        {
            if(e.Item is not ListViewItem dragged) return;
            // Dragging one of several selected rows carries all of them.
            IEnumerable<ListViewItem> items=dragged.Selected ? windows.SelectedItems.Cast<ListViewItem>() : new[]{dragged};
            windows.DoDragDrop(items.Select(x=>((LiveWindow)x.Tag!).Data).ToArray(),DragDropEffects.Copy);
        };
        return panel;
    }
    // Drag events do not bubble, so every child of the drop area needs its own handlers.
    private void EnableWindowDrop(Control area,Control target)
    {
        if(target is DesktopBoard) return; // places dropped windows itself, see board.Dropped
        target.AllowDrop=true;
        static SavedWindow[]? Dragged(DragEventArgs e) => e.Data?.GetData(typeof(SavedWindow[])) as SavedWindow[];
        target.DragEnter+=(_,e)=>
        {
            bool ok=Dragged(e) is {Length:>0};
            e.Effect=ok ? DragDropEffects.Copy : DragDropEffects.None;
            area.BackColor=ok ? System.Drawing.SystemColors.Info : System.Drawing.SystemColors.Control;
        };
        target.DragLeave+=(_,_)=>area.BackColor=System.Drawing.SystemColors.Control;
        target.DragDrop+=(_,e)=>
        {
            area.BackColor=System.Drawing.SystemColors.Control;
            if(Dragged(e) is {Length:>0} dropped) AddToEditor(dropped);
        };
        foreach(Control child in target.Controls) EnableWindowDrop(area,child);
    }
    private static bool SameWindow(SavedWindow a,SavedWindow b) =>
        a.Path.Equals(b.Path,StringComparison.OrdinalIgnoreCase) && a.Title==b.Title && a.DesktopIndex==b.DesktopIndex
        && a.Left==b.Left && a.Top==b.Top && a.Width==b.Width && a.Height==b.Height;
    // Stores copies: the scanned objects keep changing (project assignment, rescans).
    // `place` adjusts each copy before it goes in, e.g. the desktop and monitor it was dropped on.
    private void AddToEditor(IEnumerable<SavedWindow> source,Action<SavedWindow>? place=null)
    {
        int added=0,updated=0;
        foreach(var d in source)
        {
            var copy=JsonSerializer.Deserialize<SavedWindow>(JsonSerializer.Serialize(d))!;
            place?.Invoke(copy);
            // The Id catches a window dropped again after its copy was placed on another desktop or monitor.
            int at=editing.FindIndex(x=>x.Id==d.Id || SameWindow(x,d));
            if(at>=0) {editing[at]=copy;updated++;}
            else {editing.Add(copy);added++;}
        }
        if(added+updated==0) return;
        editorDirty=true; FillEditor();
        Write($"프로필 편집: 창 {added}개 추가"+(updated>0?$", {updated}개 최신 상태로 갱신":"")+" (프로필 저장을 눌러 반영하세요)");
    }
    private void AddChecked()
    {
        CaptureChecks();
        var picked=allItems.Where(x=>checks[x]).Select(x=>((LiveWindow)x.Tag!).Data).ToList();
        if(picked.Count==0) {MessageBox.Show("프로필에 추가할 창을 하나 이상 체크하세요.");return;}
        AddToEditor(picked);
    }
    private void RemoveFromEditor()
    {
        var gone=SelectedEditorWindows();
        if(gone.Count==0) return;
        editing.RemoveAll(gone.Contains);
        editorDirty=true; FillEditor();
    }
    private void FillEditor()
    {
        editor.BeginUpdate();
        try
        {
            editor.Items.Clear();
            foreach(var d in editing)
            {
                var item=new ListViewItem(Path.GetFileNameWithoutExtension(d.Path)){Tag=d,UseItemStyleForSubItems=false};
                item.SubItems.Add(d.Title); item.SubItems.Add(DesktopLabel(d)); item.SubItems.Add(RectLabel(d));
                var issue=PathIssue(d);
                var cell=item.SubItems.Add(issue.Length>0 ? "["+issue+"] "+ProjectLabel(d) : ProjectLabel(d));
                cell.ForeColor=issue.Length>0 || MissingProject(d) ? System.Drawing.Color.Firebrick : editor.ForeColor;
                editor.Items.Add(item);
            }
        }
        finally { editor.EndUpdate(); }
        board.SetWindows(editing);
        UpdateEditorHeader();
    }
    private List<SavedWindow> SelectedEditorWindows() => views.SelectedTab==boardPage ? board.Selected
        : editor.SelectedItems.Cast<ListViewItem>().Select(x=>(SavedWindow)x.Tag!).ToList();
    private void UpdateEditorHeader()
    {
        string title=loadedProfile ?? "새 프로필";
        editorHeader.Text=$"프로필 편집 — {title} · 창 {editing.Count}개"+(editorDirty || name.Text.Trim()!=(loadedProfile ?? "") ? " (저장 안 됨)" : "");
    }
    private bool ConfirmDiscard() => !(editorDirty || name.Text.Trim()!=(loadedProfile ?? ""))
        || MessageBox.Show(this,"편집 중인 프로필에 저장하지 않은 변경이 있습니다. 버리고 계속할까요?","프로필 편집",MessageBoxButtons.YesNo,MessageBoxIcon.Warning)==DialogResult.Yes;
    private void OnProfileSelected()
    {
        if(suppressProfileLoad) return;
        string? p=profiles.SelectedItem as string;
        if(p==loadedProfile) return;
        if(!ConfirmDiscard()) {SelectProfileQuietly(loadedProfile);return;}
        LoadIntoEditor(p);
    }
    private void SelectProfileQuietly(string? p)
    {
        suppressProfileLoad=true;
        try { if(p!=null && profiles.Items.Contains(p)) profiles.SelectedItem=p; else profiles.SelectedIndex=-1; }
        finally { suppressProfileLoad=false; }
    }
    private void LoadIntoEditor(string? p)
    {
        Snapshot? data=p==null ? null : ReadProfile(p);
        if(p!=null && data==null) {SelectProfileQuietly(loadedProfile);return;}
        editing.Clear();
        layouts=data?.Layouts ?? [];
        if(data!=null) editing.AddRange(ResolveLayout(data));
        loadedProfile=p; editorDirty=false; name.Text=p ?? "";
        FillEditor();
    }
    private void NewProfile()
    {
        if(!ConfirmDiscard()) return;
        SelectProfileQuietly(null); LoadIntoEditor(null); ShowLiveWindows(true); name.Focus();
    }
    private void ShowLiveWindows(bool show)
    {
        livePanel.Panel2Collapsed=!show;
        liveToggle.Text=show ? "현재 창 목록 닫기 ▶" : "◀ 현재 창 추가";
    }
    private void OpenProfileJson()
    {
        if(loadedProfile!=null) ShowInExplorer(ProfilePath(loadedProfile)); else OpenFolder(folder);
    }
    private void SetProject()
    {
        if(windows.SelectedItems.Count!=1 || windows.SelectedItems[0].Tag is not LiveWindow selected)
        {MessageBox.Show("프로젝트를 지정할 창을 하나 선택하세요. (체크가 아닌 행 선택)");return;}
        SavedWindow w=selected.Data;
        var plugin=pluginsCatalog.ForExe(w.Path);
        if(plugin==null){MessageBox.Show("해당 EXE의 확장 규칙이 없습니다. [확장 → 확장 기능 관리]에서 먼저 추가하세요.");return;}
        if(plugin.OpensFiles)
        {
            using var pick=new OpenFileDialog{Multiselect=true,Title="복원할 때 열 파일 선택 (여러 개 가능)",
                Filter="Aseprite / 이미지|*.aseprite;*.ase;*.png;*.gif;*.jpg;*.jpeg;*.bmp;*.webp;*.tga|모든 파일|*.*"};
            if(pick.ShowDialog()!=DialogResult.OK)return;
            w.PluginId=plugin.Id; w.ProjectKind=""; w.ProjectPath=""; w.OpenFiles=pick.FileNames.ToList();
            ShowProject(windows.SelectedItems[0]);
            Write($"{plugin.Name} {FilesLabel(w.OpenFiles)} (이 창을 왼쪽 프로필에 끌어다 놓고 저장하세요)");
            return;
        }
        string chosen="";
        if(plugin.ProjectPicker=="workspace-or-folder")
        {
            var choice=MessageBox.Show(".code-workspace 파일을 지정하려면 [예], 프로젝트 폴더를 지정하려면 [아니오]를 선택하세요.","작업 영역 선택",MessageBoxButtons.YesNoCancel);
            if(choice==DialogResult.Cancel)return;
            if(choice==DialogResult.Yes)
            {
                using var file=new OpenFileDialog{Filter="VS Code workspace (*.code-workspace)|*.code-workspace",Title="작업 영역 파일 선택"};
                if(file.ShowDialog()!=DialogResult.OK)return;
                chosen=file.FileName;
            }
        }
        if(chosen=="")
        {
            using var dialog=new FolderBrowserDialog{Description="복원할 프로젝트 폴더를 선택하세요"};
            if(dialog.ShowDialog()!=DialogResult.OK)return;
            chosen=dialog.SelectedPath;
        }
        if(plugin.ProjectPicker=="unity-folder" && !ProjectDetector.IsValid(chosen,plugin))
        {MessageBox.Show("Unity 프로젝트 루트가 아닙니다. Assets 및 ProjectSettings 폴더가 필요합니다.");return;}
        if(plugin.ProjectPicker=="godot-folder" && !ProjectDetector.IsValid(chosen,plugin))
        {MessageBox.Show("Godot 프로젝트 루트가 아닙니다. project.godot 파일이 있는 폴더를 선택하세요.");return;}
        w.PluginId=plugin.Id; w.ProjectKind="";w.ProjectPath=chosen;
        projectCache.Remember(plugin.Id,chosen); SaveProjectCache();
        ShowProject(windows.SelectedItems[0]);
        Write($"{plugin.Name} 프로젝트 지정: {chosen} (이 창을 왼쪽 프로필에 끌어다 놓고 저장하세요)");
    }
    private string ProfilePath(string p) => Path.Combine(folder, p + ".json");
    // Repopulates the list without loading anything into the editor; keeps the edited profile selected.
    private void RefreshProfiles()
    {
        suppressProfileLoad=true;
        try
        {
            profiles.Items.Clear();
            foreach(var f in Directory.GetFiles(folder,"*.json")) profiles.Items.Add(Path.GetFileNameWithoutExtension(f));
            if(loadedProfile!=null && profiles.Items.Contains(loadedProfile)) profiles.SelectedItem=loadedProfile;
        }
        finally { suppressProfileLoad=false; }
    }
    private Snapshot? ReadProfile(string p)
    {
        Snapshot? data;
        try { data=ProfileStore.Read(ProfilePath(p)); }
        catch(Exception ex) {Write("읽기 실패: "+ex.Message);return null;}
        if(data==null || (data.SchemaVersion!=1 && data.SchemaVersion!=2 && data.SchemaVersion!=3) || data.Windows==null) {Write("지원하지 않는 프로필 형식.");return null;}
        return data;
    }
    private void SaveEditedProfile()
    {
        if(!saveProfile.Enabled) return; // Ctrl+S while the virtual desktop API is unavailable
        string p=name.Text.Trim();
        if (string.IsNullOrWhiteSpace(p) || p.Length>60 || p.IndexOfAny(Path.GetInvalidFileNameChars())>=0 || p.EndsWith('.') || p.EndsWith(' '))
        { MessageBox.Show("프로필 이름(1~60자)을 입력하세요. 파일명에 사용할 수 없는 문자는 제외하세요."); name.Focus(); return; }
        var picked=editing.ToList();
        if(picked.Count==0) { MessageBox.Show("프로필에 창이 없습니다. [현재 창 추가]를 눌러 열린 창을 끌어다 놓으세요."); return; }
        var missing=picked.Where(MissingProject).ToList();
        if(missing.Count>0 && MessageBox.Show($"{missing.Count}개 창은 프로젝트 경로 없이 저장됩니다. 복원 시 프로그램만 실행되고 프로젝트는 열리지 않을 수 있습니다.\n\n"
            +string.Join("\n",missing.Take(10).Select(x=>"· "+x.Title))+(missing.Count>10?"\n…":"")
            +"\n\n그래도 저장할까요? ([아니오] 후 현재 창 목록에서 [선택 창 프로젝트 지정]을 하고 그 창을 다시 끌어다 놓으세요.)","프로젝트 미지정",MessageBoxButtons.YesNo,MessageBoxIcon.Warning)!=DialogResult.Yes) return;
        string path=ProfilePath(p);
        // Saving over the profile being edited is the normal case; any other existing name is a rename onto it.
        if(File.Exists(path) && !string.Equals(p,loadedProfile,StringComparison.OrdinalIgnoreCase)
            && MessageBox.Show("동일한 이름의 프로필을 덮어쓸까요?","확인",MessageBoxButtons.YesNo)!=DialogResult.Yes) return;
        try
        {
            var data=CurrentSnapshot(p);
            ProfileStore.Write(path,data,true);
            layouts=data.Layouts;
            RebuildTray();
            loadedProfile=p; editorDirty=false;
            RefreshProfiles(); UpdateEditorHeader();
            foreach(var w in picked) projectCache.Remember(PluginIdOf(w),w.ProjectPath);
            SaveProjectCache();
            Write($"프로필 '{p}' 저장: 창 {picked.Count}개. 경로: {path}");
        }
        catch(Exception ex) { Write("저장 실패: "+ex.Message); }
    }
    internal static System.Drawing.Rectangle ClampToScreens(SavedWindow w)
    {
        var area=Screen.FromRectangle(new System.Drawing.Rectangle(w.Left,w.Top,Math.Max(1,w.Width),Math.Max(1,w.Height))).WorkingArea;
        int width=Math.Clamp(w.Width,Math.Min(150,area.Width),area.Width);
        int height=Math.Clamp(w.Height,Math.Min(100,area.Height),area.Height);
        return new System.Drawing.Rectangle(Math.Clamp(w.Left,area.Left,area.Right-width),Math.Clamp(w.Top,area.Top,area.Bottom-height),width,height);
    }
}
