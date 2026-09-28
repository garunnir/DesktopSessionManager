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
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

public sealed class SavedWindow
{
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
                if (!Native.IsWindowVisible(h) || Native.GetWindow(h,4) != IntPtr.Zero) return true; // GW_OWNER
                long style = Native.GetWindowLongPtr(h,-20).ToInt64(); // GWL_EXSTYLE
                if ((style & 0x80) != 0 || (style & 0x00080000) != 0) return true; // TOOLWINDOW / NOACTIVATE
                int len = Native.GetWindowTextLengthW(h);
                if (len == 0) return true;
                var title=new StringBuilder(len+1); Native.GetWindowTextW(h,title,title.Capacity);
                Native.GetWindowThreadProcessId(h,out uint pid);
                if (pid == (uint)Environment.ProcessId) return true;
                string path=Native.GetPath(pid);
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return true;
                int desktop=Native.GetWindowDesktopNumber(h);
                if (desktop < 0) return true;
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
                    Pinned=Native.IsPinnedWindow(h)==1
                };
                found.Add(new LiveWindow{Handle=h,ProcessId=pid,Data=data});
            }
            catch { /* ignore windows that vanished while scanning */ }
            return true;
        },IntPtr.Zero);
        return found;
    }
}

public sealed class MainForm : Form
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
    private static readonly string[] ColumnNames=["프로그램","창 제목","데스크톱","창 위치/크기","프로젝트 경로","EXE 경로"];
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
    private static readonly string DataRoot=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),AppDataName);
    private readonly string folder=Path.Combine(DataRoot,"Profiles");
    private readonly ProjectCache projectCache=new(Path.Combine(DataRoot,"project-cache.json"));
    private bool apiReady;
    private readonly PluginCatalog pluginsCatalog = new();
    private readonly Button managePlugins = new() { Text="확장 기능 관리", Width=130 };
    private readonly Button reloadPlugins = new() { Text="확장 새로고침", Width=120 };

    public MainForm()
    {
        Text="Desktop Session Manager — 가상 데스크톱별 저장/복원";
#if DEBUG
        Text+=" [DEV]";
#endif
        Width=1100; Height=720; MinimumSize=new System.Drawing.Size(820,510); StartPosition=FormStartPosition.CenterScreen;
        Font=new System.Drawing.Font("Malgun Gothic",9);
        int[] widths=[145,320,120,170,240,330];
        for(int i=0;i<ColumnNames.Length;i++) windows.Columns.Add(ColumnNames[i],widths[i]);
        var scan=new Button{Text="현재 창 새로고침",Width=130};
        var save=new Button{Text="체크된 창 저장",Width=125};
        var row1=new FlowLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,WrapContents=true,Padding=new Padding(8)};
        row1.Controls.AddRange([scan,setProject,managePlugins,reloadPlugins,new Label{Text="   프로필 이름:",AutoSize=true,TextAlign=System.Drawing.ContentAlignment.MiddleCenter,Margin=new Padding(8,7,3,0)},name,save]);
        var checkAll=new Button{Text="모두 체크",Width=90};
        var uncheckAll=new Button{Text="모두 해제",Width=90};
        var rowFind=new FlowLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,WrapContents=true,Padding=new Padding(8,0,8,4)};
        rowFind.Controls.AddRange([find,checkAll,uncheckAll,checkCount]);
        var row2=new FlowLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,WrapContents=true,Padding=new Padding(8)};
        row2.Controls.AddRange([new Label{Text="저장된 프로필:",AutoSize=true,Margin=new Padding(3,7,3,0)},profiles,restore,createDesktops]);
        var layout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=6};
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Percent,70));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute,25));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent,30));
        layout.Controls.Add(row1,0,0); layout.Controls.Add(rowFind,0,1); layout.Controls.Add(windows,0,2); layout.Controls.Add(row2,0,3);
        layout.Controls.Add(new Label{Text="플러그인(JSON) 설정은 재빌드 없이 새로고침 가능합니다. 프로젝트 경로는 창을 선택해 지정하며 미저장 변경사항은 복원하지 않습니다.",Dock=DockStyle.Fill,Padding=new Padding(8,4,0,0)},0,4);
        layout.Controls.Add(log,0,5); Controls.Add(layout);
        scan.Click+=(_,_)=>RefreshWindows(); setProject.Click+=(_,_)=>SetProject(); save.Click+=(_,_)=>SaveProfile();
        managePlugins.Click+=(_,_)=>ManagePlugins(); reloadPlugins.Click+=(_,_)=>ReloadPlugins();
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
        foreach(var error in pluginsCatalog.Reload()) Write("확장 설정 오류: "+error);
        Write($"프로젝트 복원 확장 {pluginsCatalog.All.Count}개 로드됨. 설정 폴더: {pluginsCatalog.Folder}");
        try
        {
            int count=Native.GetDesktopCount();
            apiReady=count>0;
            Write(apiReady ? $"가상 데스크톱 연결 성공: {count}개" : "가상 데스크톱 조회 실패(-1). DLL/Windows 빌드 호환성을 확인하세요.");
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            Write("VirtualDesktopAccessor.dll을 EXE 옆에 배치하세요. " + ex.Message);
        }
        scan.Enabled=save.Enabled=restore.Enabled=apiReady;
        if(apiReady) RefreshWindows();
    }
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
        // Keep manual assignments for windows that are still open; detect the rest.
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
            else if(previous.TryGetValue(w.Handle,out var old) && old.Data.Path==d.Path)
            {d.PluginId=old.Data.PluginId;d.ProjectPath=old.Data.ProjectPath;d.OpenFiles=old.Data.OpenFiles;}
            else if(plugin is {OpensFiles:false} && detector.Detect(w,plugin) is {} found)
            {d.PluginId=plugin.Id;d.ProjectPath=found;detected++;}
            var item=new ListViewItem(System.IO.Path.GetFileNameWithoutExtension(d.Path)) {Checked=!d.Pinned, Tag=w};
            item.SubItems.Add(d.Title);
            item.SubItems.Add($"{d.DesktopIndex+1}: {d.DesktopName}" + (d.Pinned?" [고정]":""));
            item.SubItems.Add($"{d.Left},{d.Top} / {d.Width}×{d.Height}"); item.SubItems.Add(d.ProjectPath); item.SubItems.Add(d.Path);
            item.UseItemStyleForSubItems=false; ShowProject(item);
            allItems.Add(item); checks[item]=item.Checked;
        }
        ApplyFilter();
        Write($"일반 창 {live.Count}개 검색 (프로젝트 경로 자동 감지 {detected}개). 다른 데스크톱에 있는 최소화 창도 포함할 수 있습니다. 저장 전 목록을 확인하세요.");
        if(noBridge>0) Write($"Aseprite 창 {noBridge}개의 열린 파일 목록을 읽지 못했습니다. integrations\\aseprite의 확장을 설치하고 Aseprite를 다시 시작하세요. (또는 [선택 창 프로젝트 지정]으로 파일 지정)");
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
                2=>(dx.Pinned?int.MaxValue:dx.DesktopIndex).CompareTo(dy.Pinned?int.MaxValue:dy.DesktopIndex), // pinned last
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
    private void ShowProject(ListViewItem item)
    {
        var d=((LiveWindow)item.Tag!).Data; var cell=item.SubItems[4];
        bool missing=MissingProject(d);
        cell.Text=missing ? UnassignedProject : d.OpenFiles.Count>0 ? FilesLabel(d.OpenFiles) : d.ProjectPath;
        cell.ForeColor=missing ? System.Drawing.Color.Firebrick : windows.ForeColor;
    }
    private void ReloadPlugins()
    {
        var errors=pluginsCatalog.Reload();
        Write($"확장 설정 새로고침: {pluginsCatalog.All.Count}개, 오류 {errors.Count}개");
        foreach(var error in errors) Write("확장 설정 오류: "+error);
        if(apiReady) RefreshWindows();
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
        folderButton.Click+=(_,_)=>Process.Start(new ProcessStartInfo(pluginsCatalog.Folder){UseShellExecute=true});
        dialog.FormClosed+=(_,_)=>ReloadPlugins();dialog.ShowDialog(this);
    }
    private sealed class PluginItem(AppPlugin p)
    {
        public AppPlugin Plugin {get;}=p;
        public override string ToString()=> $"{Plugin.Name} [{Plugin.Id}] — {string.Join(", ",Plugin.ExecutableNames)}";
    }
    private void SetProject()
    {
        if(windows.SelectedItems.Count!=1 || windows.SelectedItems[0].Tag is not LiveWindow selected)
        {MessageBox.Show("프로젝트를 지정할 창을 하나 선택하세요. (체크가 아닌 행 선택)");return;}
        SavedWindow w=selected.Data;
        var plugin=pluginsCatalog.ForExe(w.Path);
        if(plugin==null){MessageBox.Show("해당 EXE의 확장 규칙이 없습니다. [확장 기능 관리]에서 먼저 추가하세요.");return;}
        if(plugin.OpensFiles)
        {
            using var pick=new OpenFileDialog{Multiselect=true,Title="복원할 때 열 파일 선택 (여러 개 가능)",
                Filter="Aseprite / 이미지|*.aseprite;*.ase;*.png;*.gif;*.jpg;*.jpeg;*.bmp;*.webp;*.tga|모든 파일|*.*"};
            if(pick.ShowDialog()!=DialogResult.OK)return;
            w.PluginId=plugin.Id; w.ProjectKind=""; w.ProjectPath=""; w.OpenFiles=pick.FileNames.ToList();
            ShowProject(windows.SelectedItems[0]);
            Write($"{plugin.Name} {FilesLabel(w.OpenFiles)} (프로필 저장을 눌러 반영하세요)");
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
        Write($"{plugin.Name} 프로젝트 지정: {chosen} (프로필 저장을 눌러 반영하세요)");
    }
    private string ProfilePath(string p) => Path.Combine(folder, p + ".json");
    private void RefreshProfiles()
    {
        string? previous=profiles.SelectedItem?.ToString(); profiles.Items.Clear();
        foreach(var f in Directory.GetFiles(folder,"*.json")) profiles.Items.Add(Path.GetFileNameWithoutExtension(f));
        if(previous!=null && profiles.Items.Contains(previous)) profiles.SelectedItem=previous;
        else if(profiles.Items.Count>0) profiles.SelectedIndex=0;
    }
    private void SaveProfile()
    {
        string p=name.Text.Trim();
        if (string.IsNullOrWhiteSpace(p) || p.Length>60 || p.IndexOfAny(Path.GetInvalidFileNameChars())>=0 || p.EndsWith('.') || p.EndsWith(' '))
        { MessageBox.Show("프로필 이름(1~60자)을 입력하세요. 파일명에 사용할 수 없는 문자는 제외하세요."); return; }
        CaptureChecks();
        var picked=allItems.Where(x=>checks[x]).Select(x=>((LiveWindow)x.Tag!).Data).ToList();
        if(picked.Count==0) { MessageBox.Show("저장할 창을 하나 이상 체크하세요."); return; }
        var missing=picked.Where(MissingProject).ToList();
        if(missing.Count>0 && MessageBox.Show($"{missing.Count}개 창은 프로젝트 경로 없이 저장됩니다. 복원 시 프로그램만 실행되고 프로젝트는 열리지 않을 수 있습니다.\n\n"
            +string.Join("\n",missing.Take(10).Select(x=>"· "+x.Title))+(missing.Count>10?"\n…":"")
            +"\n\n그래도 저장할까요? ([아니오] 후 [선택 창 프로젝트 지정]으로 지정할 수 있습니다.)","프로젝트 미지정",MessageBoxButtons.YesNo,MessageBoxIcon.Warning)!=DialogResult.Yes) return;
        string path=ProfilePath(p);
        if(File.Exists(path) && MessageBox.Show("동일한 이름의 프로필을 덮어쓸까요?","확인",MessageBoxButtons.YesNo)!=DialogResult.Yes) return;
        try
        {
            var data=new Snapshot{SchemaVersion=3,Name=p,DesktopCount=Native.GetDesktopCount(),Windows=picked};
            File.WriteAllText(path,JsonSerializer.Serialize(data,new JsonSerializerOptions{WriteIndented=true}),new UTF8Encoding(false));
            RefreshProfiles(); profiles.SelectedItem=p;
            foreach(var w in picked) projectCache.Remember(PluginIdOf(w),w.ProjectPath);
            SaveProjectCache();
            Write($"프로필 '{p}' 저장: 창 {picked.Count}개. 경로: {path}");
        }
        catch(Exception ex) { Write("저장 실패: "+ex.Message); }
    }
    private async Task RestoreProfile()
    {
        if(profiles.SelectedItem is not string p) {MessageBox.Show("복원할 프로필을 선택하세요.");return;}
        Snapshot? data;
        try { data=JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(ProfilePath(p),Encoding.UTF8)); }
        catch(Exception ex) {Write("읽기 실패: "+ex.Message);return;}
        if(data==null || (data.SchemaVersion!=1 && data.SchemaVersion!=2 && data.SchemaVersion!=3) || data.Windows==null) {Write("지원하지 않는 프로필 형식.");return;}
        if(MessageBox.Show($"'{p}'의 {data.Windows.Count}개 창을 복원합니다.\n기존 프로그램은 종료하지 않습니다. 새 프로그램이 실행될 수 있습니다.\n계속할까요?","복원 확인",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;
        restore.Enabled=false;
        try {await DoRestore(data);}
        finally {restore.Enabled=true;}
    }
    private async Task DoRestore(Snapshot data)
    {
        int count=Native.GetDesktopCount();
        int required=data.Windows.Where(w=>!w.Pinned).Select(w=>w.DesktopIndex).DefaultIfEmpty(0).Max()+1;
        if(required>count && createDesktops.Checked)
        {
            // Limit unexpected desktop creation even when loading a manually edited file.
            if(required>20) {Write("필요 데스크톱 수가 20개를 초과하여 자동 생성을 건너뜁니다.");return;}
            for(int i=count;i<required;i++)
            {
                int r=Native.CreateDesktop();
                if(r==-1 || Native.GetDesktopCount()<=count) {Write("데스크톱 생성 실패. 더 이상 생성하지 않습니다.");break;}
                count=Native.GetDesktopCount(); Write($"가상 데스크톱 생성. 현재 {count}개");
            }
        }
        var reserved=new HashSet<IntPtr>(); int ok=0,failed=0;
        foreach(var target in data.Windows)
        {
            if(target.Pinned) {Write($"고정 창 제외: {target.Title}");continue;}
            if(target.DesktopIndex>=count || target.DesktopIndex<0) {Write($"데스크톱 {target.DesktopIndex+1} 없음: {target.Title}");failed++;continue;}
            if(!File.Exists(target.Path)) {Write($"실행 파일 없음: {target.Path}");failed++;continue;}
            string pluginId=PluginIdOf(target);
            AppPlugin? plugin=pluginId=="" ? null : pluginsCatalog.ById(pluginId);
            bool projectMode=!string.IsNullOrWhiteSpace(target.ProjectPath);
            bool filesMode=!projectMode && target.OpenFiles.Count>0;
            if((projectMode || filesMode) && plugin==null) {Write($"확장 규칙 없음: {pluginId} ({target.Title}). plugins 폴더 확인");failed++;continue;}
            if(projectMode && !ProjectDetector.IsValid(target.ProjectPath,plugin!))
            {Write($"프로젝트 경로가 없거나 유효하지 않음: {target.ProjectPath}");failed++;continue;}
            var files=target.OpenFiles.Where(File.Exists).ToList();
            foreach(var gone in target.OpenFiles.Except(files)) Write($"파일 없음, 건너뜀: {gone}");
            // A bare executable match can refer to a DIFFERENT Unity/VS Code project.
            // Use the project label in the title only as a conservative reuse hint.
            string projectLabel=projectMode ? ProjectDetector.Label(target.ProjectPath) : "";
            // Files cannot be handed to a running instance, so only reuse a window that is plainly the
            // same session (same active file in the title); otherwise start one with the files.
            LiveWindow? candidate=projectMode
                ? Scanner.Scan().Where(x=>!reserved.Contains(x.Handle) && x.Data.Path.Equals(target.Path,StringComparison.OrdinalIgnoreCase)
                    && x.Data.Title.Contains(projectLabel,StringComparison.OrdinalIgnoreCase)).FirstOrDefault()
                : filesMode
                ? Scanner.Scan().FirstOrDefault(x=>!reserved.Contains(x.Handle) && x.Data.Path.Equals(target.Path,StringComparison.OrdinalIgnoreCase)
                    && x.Data.Title.Equals(target.Title,StringComparison.OrdinalIgnoreCase))
                : BestMatch(Scanner.Scan().Where(x=>!reserved.Contains(x.Handle)),target);
            if(candidate==null)
            {
                try
                {
                    var psi=new ProcessStartInfo(target.Path){UseShellExecute=true,WorkingDirectory=Path.GetDirectoryName(target.Path)!};
                    if(projectMode)
                    {
                        foreach(var arg in plugin!.LaunchArguments) psi.ArgumentList.Add(arg.Replace("{projectPath}",target.ProjectPath));
                        Write($"프로젝트 열기 요청: {plugin.Name}: {target.ProjectPath}");
                    }
                    if(filesMode)
                    {
                        // An argument with {file} is repeated once per file.
                        foreach(var arg in plugin!.LaunchArguments)
                            if(arg.Contains("{file}")) foreach(var f in files) psi.ArgumentList.Add(arg.Replace("{file}",f));
                            else psi.ArgumentList.Add(arg);
                        Write($"{plugin.Name} 파일 열기 요청: {files.Count}개");
                    }
                    // Capture pre-existing windows to avoid moving an unrelated project window.
                    var before=Scanner.Scan().Select(x=>x.Handle).ToHashSet();
                    Process.Start(psi);
                    for(int retry=0;retry<(projectMode || filesMode ? plugin!.WaitSeconds * 2 : 60) && candidate==null;retry++)
                    {
                        await Task.Delay(500);
                        var matches=Scanner.Scan().Where(x=>!reserved.Contains(x.Handle) && x.Data.Path.Equals(target.Path,StringComparison.OrdinalIgnoreCase));
                        candidate=projectMode
                            ? matches.FirstOrDefault(x=>!before.Contains(x.Handle) && x.Data.Title.Contains(projectLabel,StringComparison.OrdinalIgnoreCase))
                            : filesMode ? matches.FirstOrDefault(x=>!before.Contains(x.Handle))
                            : BestMatch(matches,target);
                    }
                }
                catch(Exception ex){Write($"실행 실패 {target.Title}: {ex.Message}");}
            }
            if(candidate==null){Write($"창을 찾지 못함: {target.Title}");failed++;continue;}
            reserved.Add(candidate.Handle);
            try
            {
                int move=Native.MoveWindowToDesktopNumber(candidate.Handle,target.DesktopIndex);
                if(move==-1) {Write($"데스크톱 이동 실패: {target.Title}");failed++;continue;}
                // Normal rect is persisted independently of the window's current maximized state.
                var wp=Native.WINDOWPLACEMENT.Init();
                if(!Native.GetWindowPlacement(candidate.Handle,ref wp)) {Write($"창 위치 읽기 실패: {target.Title}");failed++;continue;}
                var rect=ClampToScreens(target);
                wp.rcNormalPosition=new Native.RECT{Left=rect.Left,Top=rect.Top,Right=rect.Right,Bottom=rect.Bottom};
                wp.showCmd=target.Maximized?3:target.Minimized?2:1;
                if(!Native.SetWindowPlacement(candidate.Handle,ref wp)) {Write($"위치 복원 실패: {target.Title}");failed++;continue;}
                ok++; Write($"완료: {target.Title} → 데스크톱 {target.DesktopIndex+1}");
            }
            catch(Exception ex){failed++;Write($"복원 오류 {target.Title}: {ex.Message}");}
        }
        Write($"복원 종료: 성공 {ok}, 실패 {failed}. 플러그인 프로젝트 실행은 지원하지만 미저장 내용은 복원하지 않습니다.");
        MessageBox.Show($"창 복원: 성공 {ok}, 실패 {failed}\n자세한 내용은 실행 로그를 확인하세요.","복원 결과");
        RefreshWindows();
    }
    private static LiveWindow? BestMatch(IEnumerable<LiveWindow> candidates,SavedWindow target)
    {
        var same=candidates.Where(x=>string.Equals(x.Data.Path,target.Path,StringComparison.OrdinalIgnoreCase)).ToList();
        if(same.Count==0)return null;
        return same.OrderByDescending(x=>string.Equals(x.Data.Title,target.Title,StringComparison.OrdinalIgnoreCase)?1000:0)
            .ThenByDescending(x=>x.Data.DesktopIndex==target.DesktopIndex?50:0)
            .ThenByDescending(x=>Similarity(x.Data.Title,target.Title)).First();
    }
    private static int Similarity(string a,string b)
    {
        int i=0; while(i<a.Length && i<b.Length && char.ToUpperInvariant(a[i])==char.ToUpperInvariant(b[i]))i++;return i;
    }
    private static System.Drawing.Rectangle ClampToScreens(SavedWindow w)
    {
        var r=new System.Drawing.Rectangle(w.Left,w.Top,Math.Clamp(w.Width,150,10000),Math.Clamp(w.Height,100,10000));
        if(Screen.AllScreens.Any(s=>s.WorkingArea.IntersectsWith(r))) return r;
        var area=Screen.PrimaryScreen?.WorkingArea ?? new System.Drawing.Rectangle(0,0,1280,720);
        return new System.Drawing.Rectangle(area.Left+30,area.Top+30,Math.Min(r.Width,area.Width-60),Math.Min(r.Height,area.Height-60));
    }
}
