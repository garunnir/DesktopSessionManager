using System.Diagnostics;

namespace DesktopSessionManager;

public sealed partial class MainForm
{
    private async Task RestoreProfile(string? profile = null, bool quick = false, bool exitOnSuccess = true)
    {
        if (restoring || !apiReady) return;
        string? p = profile ?? loadedProfile;
        if (p == null) { MessageBox.Show(this, "복원할 프로필을 선택하세요."); return; }
        if (ReadProfile(p) is not { } data) return;
        data.Windows = ResolveLayout(data);
        restoring = true; restore.Enabled = false; RebuildTray();
        try
        {
            bool layoutOnly = false;
            if (!quick)
            {
                using var preview = MakeDialog("선택 복원 — 저장된 프로필 기준 (미저장 편집 내용 제외)", out var panel);
                var list = new ListView
                {
                    Width = 720, Height = 350, View = View.Details, CheckBoxes = true, FullRowSelect = true,
                    GridLines = true, HeaderStyle = ColumnHeaderStyle.Nonclickable, HideSelection = false
                };
                list.Columns.Add("창", 360); list.Columns.Add("동작", 340);
                var current = ScanIdentified();
                foreach (var w in data.Windows)
                {
                    var matches = WindowMatcher.Candidates(current, w);
                    string action = matches.Count == 1 ? "기존 창 재사용" : matches.Count > 1 ? "연결 모호: 복원 보류" : "새 앱 실행";
                    string issue = PathIssue(w);
                    if (issue != "") action = issue;
                    if (!w.AllDesktops && w.DesktopIndex >= Native.GetDesktopCount()) action += " / 대상 데스크톱 부족";
                    if (w.Pinned) action = "고정 창: 건너뜀";
                    var item = new ListViewItem(w.Title) { Checked = !w.Pinned, Tag = w, UseItemStyleForSubItems = false };
                    var cell = item.SubItems.Add(action);
                    cell.ForeColor = RestoreActionColor(action);
                    list.Items.Add(item);
                }
                panel.Controls.Add(list);
                panel.Controls.Add(new Label
                {
                    Text = "초록: 기존 창 재사용 · 파랑: 새 앱 실행 · 주황: 연결 모호 · 빨강: 경로/데스크톱 문제 · 회색: 건너뜀",
                    AutoSize = true, ForeColor = System.Drawing.SystemColors.GrayText
                });
                var mode = new CheckBox { Text = "배치만 복원 (새 앱을 실행하지 않음)", AutoSize = true }; panel.Controls.Add(mode);
                panel.Controls.Add(new Label { Text = "실행 직전 창을 다시 확인합니다. 부족한 데스크톱 생성은 메인 화면 설정을 따릅니다.", AutoSize = true });
                AddAccept(preview, panel, () => list.CheckedItems.Count > 0);
                if (preview.ShowDialog(this) != DialogResult.OK) return;
                data.Windows = list.CheckedItems.Cast<ListViewItem>().Select(x => (SavedWindow)x.Tag!).ToList(); layoutOnly = mode.Checked;
            }
            MarkRecent(p);
            await RunRestore(data.Windows, layoutOnly, exitOnSuccess);
        }
        catch (Exception ex) { Write("복원 실패: " + ex.Message); MessageBox.Show(this, ex.Message, "복원 실패"); }
        finally { restoring = false; restore.Enabled = apiReady; RebuildTray(); RefreshProfiles(); }
    }

    // exitOnSuccess=false keeps the app (tray, hotkey) running after a fully successful restore.
    private async Task RunRestore(List<SavedWindow> targets, bool layoutOnly, bool exitOnSuccess = true)
    {
        using var progress = MakeDialog("복원 진행 — 중단은 남은 작업만 멈춥니다", out var panel);
        var status = new Label { AutoSize = true, Text = "준비 중" }; panel.Controls.Add(status);
        var results = new ListBox { Width = 720, Height = 350 }; panel.Controls.Add(results);
        var cancel = new Button { Text = "남은 작업 중단", AutoSize = true }; panel.Controls.Add(cancel);
        var retry = new Button { Text = "실패 항목만 재시도", AutoSize = true, Enabled = false }; panel.Controls.Add(retry);
        var edit = new Button { Text = "선택 실패 항목 경로 수정", AutoSize = true, Enabled = false }; panel.Controls.Add(edit);
        var close = new Button { Text = "닫기", Enabled = false }; panel.Controls.Add(close);
        var quit = new Button { Text = "프로그램 종료", AutoSize = true }; panel.Controls.Add(quit);
        bool running = false, exitAfter = false;
        var rows = new List<(SavedWindow Target, bool Failed)>();
        var reserved = new HashSet<IntPtr>();
        async Task Execute(List<SavedWindow> batch)
        {
            bool allOk = false;
            running = true; retry.Enabled = edit.Enabled = close.Enabled = false; cancel.Enabled = true;
            restoreCancellation?.Dispose(); restoreCancellation = new CancellationTokenSource(); var token = restoreCancellation.Token;
            failedWindows.Clear(); results.Items.Clear(); rows.Clear();
            try
            {
                int count = Native.GetDesktopCount();
                int required = batch.Where(w => !w.Pinned && !w.AllDesktops).Select(w => w.DesktopIndex + 1).DefaultIfEmpty(1).Max();
                if (!layoutOnly && createDesktops.Checked && required <= 20)
                    while (count < required) { token.ThrowIfCancellationRequested(); if (Native.CreateDesktop() == -1 || Native.GetDesktopCount() <= count) break; count = Native.GetDesktopCount(); }
                int i = 0;
                await RestorePolicy.RunBatch(batch, async w =>
                {
                    token.ThrowIfCancellationRequested();
                    status.Text = $"{++i}/{batch.Count} · 대기 중: {w.Title}";
                    string prefix = $"{i}/{batch.Count} · {w.Title} · ";
                    var phase = new Progress<string>(value => status.Text = prefix + value);
                    string result;
                    // Window discovery and native desktop calls can block synchronously.
                    // Keep them off the UI thread so the progress dialog can repaint and
                    // its cancellation button remains usable.
                    try { result = await Task.Run(() => RestoreOne(w, layoutOnly, reserved, token, phase)); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { result = "실행 / 복원 오류: " + ex.Message; }
                    bool failed = result != "완료" && result != "고정 창 건너뜀";
                    if (failed) failedWindows.Add(w);
                    rows.Add((w, failed)); results.Items.Add($"{w.Title}: {result}"); Write($"{w.Title}: {result}");
                }, token);
                status.Text = $"완료 {batch.Count - failedWindows.Count}/{batch.Count} · 실패 {failedWindows.Count}";
                allOk = failedWindows.Count == 0;
            }
            catch (OperationCanceledException)
            {
                foreach (var w in batch.Skip(rows.Count)) { failedWindows.Add(w); rows.Add((w, true)); results.Items.Add(w.Title + ": 중단됨 (재시도 가능)"); }
                status.Text = "중단됨. 이미 실행하거나 이동한 창은 유지됩니다.";
            }
            finally { running = false; cancel.Enabled = false; retry.Enabled = edit.Enabled = failedWindows.Count > 0; close.Enabled = true; }
            if (allOk && exitOnSuccess) exitAfter = true;
            if (exitAfter || allOk) progress.Close();
        }
        cancel.Click += (_, _) => restoreCancellation?.Cancel();
        close.Click += (_, _) => progress.Close();
        quit.Click += (_, _) => { exitAfter = true; if (running) restoreCancellation?.Cancel(); else progress.Close(); };
        progress.FormClosing += (_, e) => { if (running) { restoreCancellation?.Cancel(); e.Cancel = true; } };
        retry.Click += async (_, _) => await Execute(failedWindows.ToList());
        edit.Click += (_, _) => { int index = results.SelectedIndex; if (index >= 0 && rows[index].Failed) EditWindow(rows[index].Target); };
        // Modal dialog keeps the shared restore guard active through retries while async work pumps UI events.
        progress.Shown += async (_, _) => await Execute(targets);
        progress.ShowDialog(this);
        restoreCancellation?.Dispose(); restoreCancellation = null;
        if (exitAfter) BeginInvoke(ExitApp);
        await Task.CompletedTask;
    }

    private async Task<string> RestoreOne(SavedWindow target, bool layoutOnly, HashSet<IntPtr> reserved, CancellationToken token, IProgress<string>? phase = null)
    {
        void Report(string value)
        {
            Trace.WriteLine($"{DateTimeOffset.Now:O} Restore [{target.Title}] {value}");
            phase?.Report(value);
        }
        Report("복원 시작");
        token.ThrowIfCancellationRequested();
        if (target.Pinned) return "고정 창 건너뜀";
        if (!target.AllDesktops && (target.DesktopIndex < 0 || target.DesktopIndex >= Native.GetDesktopCount())) return "대상 데스크톱 없음";
        Report("현재 창 검색 중");
        var matches = WindowMatcher.Candidates(ScanIdentified().Where(x => !reserved.Contains(x.Handle)), target);
        Report($"현재 창 검색 완료: {matches.Count}개 일치");
        var action = RestorePolicy.Decide(matches.Count, layoutOnly);
        if (action == RestoreAction.Ambiguous) return "연결 모호: 같은 프로젝트의 중복 창을 확인하세요";
        LiveWindow? candidate = matches.SingleOrDefault();
        if (action == RestoreAction.Missing) return "실행 중인 대상 없음 (배치 모드: 실행 안 함)";
        if (action == RestoreAction.Launch)
        {
            string issue = PathIssue(target); if (issue != "") return issue;
            var plugin = pluginsCatalog.ById(PluginIdOf(target)) ?? pluginsCatalog.ForExe(target.Path);
            var psi = AppLaunch.Create(target.Path, Path.GetDirectoryName(target.Path)!);
            if (target.ProjectPath != "") foreach (var arg in plugin!.LaunchArguments) psi.ArgumentList.Add(arg.Replace("{projectPath}", target.ProjectPath));
            else if (target.OpenFiles.Count > 0)
                foreach (var arg in plugin!.LaunchArguments)
                    if (arg.Contains("{file}")) foreach (var file in target.OpenFiles) psi.ArgumentList.Add(arg.Replace("{file}", file));
                    else psi.ArgumentList.Add(arg);
            var before = Scanner.Scan().Select(x => x.Handle).ToHashSet();
            token.ThrowIfCancellationRequested();
            Report("프로그램 실행 및 창 대기 중");
            using var launched = AppLaunch.Start(psi);
            Report($"프로그램 실행 요청 반환: PID {launched?.Id}");
            int attempts = (plugin?.WaitSeconds ?? 30) * 2;
            for (int i = 0; i < attempts && candidate == null; i++)
            {
                await Task.Delay(500, token);
                var current = ScanIdentified().Where(x => !reserved.Contains(x.Handle)).ToList();
                Report($"창 대기 {i + 1}/{attempts}: 검색 {current.Count}개");
                candidate = WindowMatcher.Unique(current, target);
                if (candidate == null && launched is { HasExited: true } && launched.ExitCode != 0)
                    return $"프로그램 실행 실패 (종료 코드 {launched.ExitCode})";
                if (candidate == null && target.ProjectPath == "" && target.OpenFiles.Count == 0)
                {
                    var fresh = current.Where(x => !before.Contains(x.Handle) && WindowMatcher.SamePath(x.Data.Path, target.Path) && launched != null && x.ProcessId == launched.Id).ToList();
                    if (fresh.Count == 1) candidate = fresh[0];
                }
            }
            if (candidate == null) return "창 탐색 시간 초과";
        }
        token.ThrowIfCancellationRequested();
        if (candidate == null || !Native.IsWindow(candidate.Handle)) return "대상 창이 닫힘";
        if (candidate.Data.Pinned) return "현재 고정된 창: 이동 보류";
        if (candidate.Data.AllDesktops && !target.AllDesktops) return "모든 데스크톱 창: 대상 데스크톱 이동 불가";
        Report("데스크톱 이동 중");
        if (!target.AllDesktops && Native.MoveWindowToDesktopNumber(candidate.Handle, target.DesktopIndex) == -1) return "데스크톱 이동 실패";
        Report("창 위치 읽는 중");
        token.ThrowIfCancellationRequested();
        var wp = Native.WINDOWPLACEMENT.Init();
        if (!Native.GetWindowPlacement(candidate.Handle, ref wp)) return "창 위치 읽기 실패";
        var rect = ClampToScreens(target);
        wp.rcNormalPosition = new Native.RECT { Left = rect.Left, Top = rect.Top, Right = rect.Right, Bottom = rect.Bottom };
        wp.showCmd = target.Maximized ? 3 : target.Minimized ? 2 : 1;
        token.ThrowIfCancellationRequested();
        Report("창 위치 적용 중");
        if (!Native.SetWindowPlacement(candidate.Handle, ref wp)) return "창 위치 이동 실패";
        Report("창 위치 적용 완료");
        reserved.Add(candidate.Handle);
        if (!layoutOnly && target.ClaudeRemoteControl)
        {
            Report("Claude Remote Control 터미널 실행 중");
            // Shell startup can block (for example while Windows resolves a network drive).
            // Do not block the UI or the remaining restore indefinitely.
            string? error;
            try { error = await Task.Run(() => StartClaudeRemoteControl(target)).WaitAsync(TimeSpan.FromSeconds(15), token); }
            catch (TimeoutException) { return "Claude Remote Control 시작 대기 시간 초과 (나중에 실행될 수 있음)"; }
            if (error != null) return "Claude Remote Control 시작 실패: " + error;
        }
        Report("복원 완료");
        return "완료";
    }

    private static readonly Dictionary<string, Process> ClaudeSessions = new(StringComparer.OrdinalIgnoreCase);
    private static string? StartClaudeRemoteControl(SavedWindow target)
    {
        lock (ClaudeSessions) return StartClaudeRemoteControlLocked(target);
    }
    private static string? StartClaudeRemoteControlLocked(SavedWindow target)
    {
        if (target.PluginId != "vscode" || !Directory.Exists(target.ProjectPath)) return "VS Code 프로젝트 폴더를 확인하세요";
        string directory = Path.GetFullPath(target.ProjectPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (ClaudeSessions.TryGetValue(directory, out var existing))
        {
            if (!existing.HasExited) return null;
            existing.Dispose(); ClaudeSessions.Remove(directory);
        }
        try
        {
            // ShellExecute gives the interactive CLI its own terminal, including
            // when the manager was launched from a terminal or a test runner.
            var psi = new ProcessStartInfo("powershell.exe") { WorkingDirectory = directory, UseShellExecute = true };
            psi.ArgumentList.Add("-NoLogo"); psi.ArgumentList.Add("-NoProfile"); psi.ArgumentList.Add("-Command");
            string logFolder = Path.Combine(DataRoot, "Logs");
            Directory.CreateDirectory(logFolder);
            string logFile = Path.Combine(logFolder, $"claude-remote-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log");
            Trace.WriteLine("Claude Remote Control log: " + logFile);
            // npm's claude.ps1 can be blocked by PowerShell's execution policy. Prefer
            // the native executable or npm's cmd shim without changing that policy.
            psi.ArgumentList.Add("$c = Get-Command claude.exe, claude.cmd -ErrorAction SilentlyContinue | Select-Object -First 1; if (-not $c) { Write-Host 'Claude Code was not found in PATH.'; Read-Host 'Press Enter to close'; exit 1 }; & $c.Source remote-control --spawn=session --name ('Desktop Session - ' + (Split-Path -Leaf (Get-Location))) --debug-file '" + logFile.Replace("'", "''") + "'; if ($LASTEXITCODE -ne 0) { Write-Host ('Claude exited with code ' + $LASTEXITCODE); Read-Host 'Press Enter to close' }");
            var process = Process.Start(psi);
            if (process == null) return "터미널을 실행할 수 없습니다";
            ClaudeSessions[directory] = process;
            return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { return ex.Message; }
    }

    private static System.Drawing.Color RestoreActionColor(string action)
    {
        if (action.StartsWith("고정 창", StringComparison.Ordinal)) return System.Drawing.SystemColors.GrayText;
        if (action.Contains("대상 데스크톱 부족", StringComparison.Ordinal)) return System.Drawing.Color.Firebrick;
        if (action.StartsWith("기존 창 재사용", StringComparison.Ordinal)) return System.Drawing.Color.FromArgb(30, 130, 90);
        if (action.StartsWith("새 앱 실행", StringComparison.Ordinal)) return System.Drawing.Color.FromArgb(40, 100, 200);
        if (action.StartsWith("연결 모호", StringComparison.Ordinal)) return System.Drawing.Color.FromArgb(190, 110, 20);
        return System.Drawing.Color.Firebrick;
    }
}
