using System.Runtime.InteropServices;
using System.Text.Json;

namespace DesktopSessionManager;

public sealed partial class MainForm
{
    private UserSettings settings = new();
    private readonly NotifyIcon tray = new() { Icon = System.Drawing.SystemIcons.Application, Text = "Desktop Session Manager" };
    private readonly System.Windows.Forms.Timer autoSave = new();
    private bool exitRequested, restoring;
    private CancellationTokenSource? restoreCancellation;
    private readonly List<SavedWindow> failedWindows = [];
    private List<MonitorLayout> layouts = [];
    private readonly string settingsPath = Path.Combine(DataRoot, "settings.json");
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    private void InitializeUsability()
    {
        try { if (File.Exists(settingsPath)) settings = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(settingsPath)) ?? new(); }
        catch (Exception ex) { Write("설정 읽기 실패: " + ex.Message); }
        tray.Visible = true;
        tray.DoubleClick += (_, _) => ShowMain();
        RebuildTray();
        Shown += (_, _) => ApplyHotkey();
        FormClosed += (_, _) => { restoreCancellation?.Cancel(); UnregisterHotKey(Handle, 1); tray.Dispose(); autoSave.Dispose(); };
        autoSave.Interval = 60000;
        int ticks = 0;
        autoSave.Tick += (_, _) =>
        {
            if (settings.AutoSaveMinutes <= 0 || ++ticks < settings.AutoSaveMinutes || restoring || editing.Count == 0) return;
            ticks = 0;
            try { ProfileStore.Write(Path.Combine(DataRoot, "Recovery", "autosave.json"), CurrentSnapshot("자동 저장")); }
            catch (Exception ex) { Write("자동 저장 실패: " + ex.Message); }
        };
        autoSave.Start();
    }
    private void ShowMain() { Show(); WindowState = FormWindowState.Normal; Activate(); }
    private void ExitApp() { exitRequested = true; Close(); if (!IsDisposed) exitRequested = false; }
    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (!exitRequested && e.CloseReason == CloseReason.UserClosing && settings.CloseToTray) { e.Cancel = true; Hide(); return; }
        if (!ConfirmDiscard()) { e.Cancel = true; return; }
        restoreCancellation?.Cancel();
    }
    private void SaveSettings()
    {
        try { ProfileStore.Write(settingsPath, settings); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "설정 저장 실패"); }
        RebuildTray();
    }
    private void ApplyHotkey()
    {
        UnregisterHotKey(Handle, 1);
        if (settings.Hotkey && !RegisterHotKey(Handle, 1, 0x4003, (uint)Keys.F9))
            MessageBox.Show(this, "Ctrl+Alt+F9 등록 실패: 다른 프로그램의 단축키와 충돌할 수 있습니다.", "전역 단축키");
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x312 && m.WParam.ToInt32() == 1) { _ = RestoreProfile(settings.Recent.FirstOrDefault() ?? loadedProfile, true); }
        base.WndProc(ref m);
    }
    private void RebuildTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("메인 창 열기", null, (_, _) => ShowMain());
        void AddGroup(string label, IEnumerable<string> names)
        {
            var group = new ToolStripMenuItem(label);
            foreach (string p in names.Where(p => File.Exists(ProfilePath(p))).Distinct())
            { var item = group.DropDownItems.Add(p); item.Enabled = !restoring && apiReady; item.Click += async (_, _) => await RestoreProfile(p, true); }
            menu.Items.Add(group);
        }
        AddGroup("즐겨찾기", settings.Favorites);
        AddGroup("최근 복원", settings.Recent);
        AddGroup("모든 프로필", Directory.GetFiles(folder, "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>());
        menu.Items.Add("종료", null, (_, _) => ExitApp());
        var old = tray.ContextMenuStrip; tray.ContextMenuStrip = menu; old?.Dispose();
    }
    private void UsabilitySettings()
    {
        using var dialog = MakeDialog("트레이 및 자동 저장 설정", out var panel);
        var close = new CheckBox { Text = "창 닫기 시 트레이로 보내기 (종료 메뉴는 앱 종료)", Checked = settings.CloseToTray, AutoSize = true };
        var key = new CheckBox { Text = "Ctrl+Alt+F9: 최근 프로필 복원", Checked = settings.Hotkey, AutoSize = true };
        var minutes = new NumericUpDown { Minimum = 0, Maximum = 120, Value = Math.Clamp(settings.AutoSaveMinutes, 0, 120) };
        panel.Controls.AddRange([close, key, new Label { Text = "자동 저장 간격(분), 0 = 끔. 별도 Recovery 파일에 편집 내용을 보관합니다.", AutoSize = true }, minutes]);
        AddAccept(dialog, panel, () => { settings.CloseToTray = close.Checked; settings.Hotkey = key.Checked; settings.AutoSaveMinutes = (int)minutes.Value; SaveSettings(); ApplyHotkey(); return true; });
        dialog.ShowDialog(this);
    }
    private static Form MakeDialog(string title, out FlowLayoutPanel panel)
    {
        var form = new Form { Text = title, Width = 800, Height = 600, StartPosition = FormStartPosition.CenterParent, Font = new System.Drawing.Font("Malgun Gothic", 9) };
        panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(12) };
        form.Controls.Add(panel); return form;
    }
    private static void AddAccept(Form dialog, FlowLayoutPanel panel, Func<bool> action)
    {
        var buttons = new FlowLayoutPanel { AutoSize = true };
        var ok = new Button { Text = "적용", Width = 100 }; var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) => { if (action()) dialog.DialogResult = DialogResult.OK; };
        buttons.Controls.AddRange([ok, cancel]); panel.Controls.Add(buttons); dialog.CancelButton = cancel;
    }
    private string PathIssue(SavedWindow w)
    {
        if (!File.Exists(w.Path)) return "실행 파일 없음";
        var plugin = pluginsCatalog.ById(PluginIdOf(w)) ?? pluginsCatalog.ForExe(w.Path);
        if (w.ProjectPath != "" && (plugin == null || !ProjectDetector.IsValid(w.ProjectPath, plugin))) return "프로젝트 경로 확인 필요";
        if (w.OpenFiles.Any(f => !File.Exists(f))) return "열 파일 경로 확인 필요";
        if ((w.ProjectPath != "" || w.OpenFiles.Count > 0) && plugin == null) return "확장 규칙 없음";
        if (w.ProjectPath != "" && plugin?.OpensFiles == true) return "파일 방식 확장에는 열 파일을 지정하세요";
        if (w.OpenFiles.Count > 0 && plugin?.OpensFiles == false) return "프로젝트 방식 확장에는 프로젝트 경로를 지정하세요";
        return "";
    }
    private bool EditWindow(SavedWindow original)
    {
        var copy = ProfileStore.Clone(original);
        using var dialog = MakeDialog("저장된 창 편집 — " + original.Title, out var panel);
        TextBox Field(string title, string value, bool multiline = false)
        {
            panel.Controls.Add(new Label { Text = title, AutoSize = true });
            var box = new TextBox { Text = value, Width = 720, Multiline = multiline, Height = multiline ? 100 : 26, ScrollBars = multiline ? ScrollBars.Vertical : ScrollBars.None };
            panel.Controls.Add(box); return box;
        }
        var exe = Field("실행 파일", copy.Path);
        var project = Field("프로젝트 경로", copy.ProjectPath);
        var files = Field("열 파일 (한 줄에 하나)", string.Join(Environment.NewLine, copy.OpenFiles), true);
        var browse = new Button { Text = "프로젝트 / 파일 선택", AutoSize = true }; panel.Controls.Add(browse);
        browse.Click += (_, _) =>
        {
            var plugin = pluginsCatalog.ForExe(exe.Text);
            if (plugin == null) { MessageBox.Show(dialog, "해당 EXE의 플러그인을 먼저 등록하세요."); return; }
            if (plugin.OpensFiles || (plugin.ProjectPicker == "workspace-or-folder" && MessageBox.Show(dialog, "워크스페이스 파일을 선택할까요? 아니오: 폴더 선택", "경로 선택", MessageBoxButtons.YesNo) == DialogResult.Yes))
            {
                using var picker = new OpenFileDialog { Multiselect = plugin.OpensFiles, Filter = plugin.OpensFiles ? "모든 파일|*.*" : "VS Code workspace|*.code-workspace" };
                if (picker.ShowDialog(dialog) == DialogResult.OK) { if (plugin.OpensFiles) files.Lines = picker.FileNames; else project.Text = picker.FileName; }
            }
            else { using var picker = new FolderBrowserDialog(); if (picker.ShowDialog(dialog) == DialogResult.OK) project.Text = picker.SelectedPath; }
        };
        panel.Controls.Add(new Label { Text = "대상 데스크톱 (1부터)", AutoSize = true });
        var desktop = new NumericUpDown { Minimum = 1, Maximum = 100, Value = Math.Clamp(copy.DesktopIndex + 1, 1, 100), Enabled = !copy.AllDesktops && !copy.Pinned }; panel.Controls.Add(desktop);
        var state = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 }; state.Items.AddRange(["일반", "최대화", "최소화"]); state.SelectedIndex = copy.Maximized ? 1 : copy.Minimized ? 2 : 0; panel.Controls.Add(state);
        panel.Controls.Add(new Label { Text = "현재 경로 상태: " + (PathIssue(copy) is { Length: > 0 } issue ? issue : "정상") + " · 적용 후 프로필 저장을 눌러야 파일에 반영됩니다.", AutoSize = true });
        AddAccept(dialog, panel, () =>
        {
            copy.Path = exe.Text.Trim(); copy.ProjectPath = project.Text.Trim(); copy.OpenFiles = files.Lines.Select(x => x.Trim()).Where(x => x != "").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var plugin = pluginsCatalog.ForExe(copy.Path); copy.PluginId = plugin?.Id ?? copy.PluginId;
            if (copy.ProjectPath != "" && copy.OpenFiles.Count > 0) { MessageBox.Show(dialog, "프로젝트 또는 열 파일 중 한 방식만 지정하세요."); return false; }
            bool pathsChanged = copy.Path != original.Path || copy.ProjectPath != original.ProjectPath || !copy.OpenFiles.SequenceEqual(original.OpenFiles);
            if (pathsChanged && PathIssue(copy) is { Length: > 0 } error) { MessageBox.Show(dialog, error); return false; }
            copy.DesktopIndex = copy.AllDesktops ? original.DesktopIndex : (int)desktop.Value - 1;
            copy.Maximized = state.SelectedIndex == 1; copy.Minimized = state.SelectedIndex == 2;
            return true;
        });
        if (dialog.ShowDialog(this) != DialogResult.OK) return false;
        int at = editing.FindIndex(w => ReferenceEquals(w, original) || w.Id == original.Id);
        if (at >= 0) editing[at] = copy;
        original.Path = copy.Path; original.ProjectPath = copy.ProjectPath; original.OpenFiles = copy.OpenFiles; original.PluginId = copy.PluginId; original.DesktopIndex = copy.DesktopIndex; original.Maximized = copy.Maximized; original.Minimized = copy.Minimized;
        if (at >= 0) editorDirty = true;
        FillEditor(); return true;
    }
    private void EditSelected()
    {
        if (SelectedEditorWindows() is [var one]) EditWindow(one);
        else MessageBox.Show(this, "편집할 프로필 항목 하나를 선택하세요.");
    }
    private List<LiveWindow> ScanIdentified()
    {
        var result = Scanner.Scan(); var detector = new ProjectDetector(projectCache);
        foreach (var w in result)
        {
            var p = pluginsCatalog.ForExe(w.Data.Path); if (p == null) continue;
            w.Data.PluginId = p.Id;
            if (p.OpensFiles) w.Data.OpenFiles = ProjectDetector.AsepriteOpenFiles(w) ?? [];
            else w.Data.ProjectPath = detector.Detect(w, p) ?? "";
        }
        return result;
    }
    private void UpdateFromCurrent()
    {
        if (!apiReady) return;
        var current = ScanIdentified();
        using var dialog = MakeDialog("현재 배치로 갱신 — 적용 전 변경 확인", out var panel);
        var selections = new List<(SavedWindow Saved, ComboBox Choice)>();
        foreach (var w in editing)
        {
            panel.Controls.Add(new Label { Text = w.Title + " · 현재 " + RectLabel(w) + " / " + DesktopLabel(w), AutoSize = true });
            var choice = new ComboBox { Width = 720, DropDownStyle = ComboBoxStyle.DropDownList };
            choice.Items.Add("유지 (닫힌 창 / 불확실한 연결)"); choice.Items.Add("프로필에서 제거");
            var possible = current.Where(x => WindowMatcher.SamePath(x.Data.Path, w.Path)).ToList();
            foreach (var candidate in possible) choice.Items.Add(new LiveChoice(candidate));
            var match = WindowMatcher.Unique(current, w);
            choice.SelectedIndex = match == null ? 0 : possible.IndexOf(match) + 2;
            panel.Controls.Add(choice); selections.Add((w, choice));
        }
        AddAccept(dialog, panel, () =>
        {
            var chosen = selections.Select(x => x.Choice.SelectedItem).OfType<LiveChoice>().ToList();
            if (chosen.Select(x => x.Window.Handle).Distinct().Count() != chosen.Count) { MessageBox.Show(dialog, "같은 창을 여러 항목에 연결할 수 없습니다."); return false; }
            foreach (var (saved, choice) in selections)
            {
                if (choice.SelectedIndex == 1) editing.Remove(saved);
                if (choice.SelectedItem is LiveChoice selected)
                {
                    var d = selected.Window.Data;
                    saved.Left = d.Left; saved.Top = d.Top; saved.Width = d.Width; saved.Height = d.Height;
                    saved.Maximized = d.Maximized; saved.Minimized = d.Minimized; saved.DesktopIndex = d.DesktopIndex; saved.DesktopName = d.DesktopName; saved.Pinned = d.Pinned; saved.AllDesktops = d.AllDesktops;
                }
            }
            editorDirty = true; FillEditor(); return true;
        });
        dialog.ShowDialog(this);
    }
    private sealed record LiveChoice(LiveWindow Window)
    {
        public override string ToString() => $"갱신: {Window.Data.Title} · {Window.Data.ProjectPath} · {RectLabel(Window.Data)} / {DesktopLabel(Window.Data)}";
    }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(System.Drawing.Point point, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
    private static string MonitorKey() => string.Join("|", Screen.AllScreens.OrderBy(x => x.DeviceName).Select(s =>
    {
        var monitor = MonitorFromPoint(new System.Drawing.Point(s.Bounds.Left + s.Bounds.Width / 2, s.Bounds.Top + s.Bounds.Height / 2), 2);
        int result = GetDpiForMonitor(monitor, 0, out uint x, out uint y);
        return $"{s.DeviceName}:{s.Bounds}:{s.WorkingArea}:{(result == 0 ? x : 96)}:{(result == 0 ? y : 96)}";
    }));
    private static List<SavedWindow> ResolveLayout(Snapshot snapshot)
    {
        var windows = ProfileStore.Clone(snapshot.Windows);
        var layout = snapshot.Layouts.FirstOrDefault(x => x.Configuration == MonitorKey());
        if (layout == null) return windows;
        foreach (var w in windows)
        {
            var position = layout.Windows.FirstOrDefault(x => x.Id == w.Id);
            if (position == null) continue;
            w.Left = position.Left; w.Top = position.Top; w.Width = position.Width; w.Height = position.Height;
            w.Maximized = position.Maximized; w.Minimized = position.Minimized;
        }
        return windows;
    }
    private Snapshot CurrentSnapshot(string title)
    {
        var versions = ProfileStore.Clone(layouts); versions.RemoveAll(x => x.Configuration == MonitorKey());
        versions.Add(new MonitorLayout { Configuration = MonitorKey(), Windows = ProfileStore.Clone(editing) });
        return new Snapshot { SchemaVersion = 3, Name = title, DesktopCount = apiReady ? Native.GetDesktopCount() : editing.Select(x => x.DesktopIndex + 1).DefaultIfEmpty(1).Max(), Windows = ProfileStore.Clone(editing), Layouts = versions };
    }
    private void ShowHistory()
    {
        using var dialog = MakeDialog("백업 및 복구 — 복구 내용은 확인 후 저장하세요", out var panel);
        var list = new ListBox { Width = 720, Height = 360 };
        var paths = new List<string>();
        if (loadedProfile != null && Directory.Exists(ProfilePath(loadedProfile) + ".history")) paths.AddRange(Directory.GetFiles(ProfilePath(loadedProfile) + ".history", "*.json").OrderByDescending(x => x));
        string recovery = Path.Combine(DataRoot, "Recovery", "autosave.json"); if (File.Exists(recovery)) paths.Insert(0, recovery);
        foreach (var path in paths) list.Items.Add($"{(path == recovery ? "자동 저장" : loadedProfile)} · {File.GetLastWriteTime(path):yyyy-MM-dd HH:mm:ss.fff}");
        panel.Controls.Add(list);
        AddAccept(dialog, panel, () =>
        {
            if (list.SelectedIndex < 0 || !ConfirmDiscard()) return false;
            try { var snapshot = ProfileStore.Read(paths[list.SelectedIndex]); editing.Clear(); editing.AddRange(snapshot.Windows); layouts = snapshot.Layouts; editorDirty = true; FillEditor(); return true; }
            catch (Exception ex) { MessageBox.Show(dialog, ex.Message, "복구 실패"); return false; }
        });
        dialog.ShowDialog(this);
    }
}
