using System.Diagnostics;

namespace DesktopSessionManager;

public sealed partial class MainForm
{
    private async Task RestoreProfile(string? profile = null, bool quick = false)
    {
        if (restoring || !apiReady) return;
        string? p = profile ?? profiles.SelectedItem as string;
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
                var list = new CheckedListBox { Width = 720, Height = 350, CheckOnClick = true };
                var current = ScanIdentified();
                foreach (var w in data.Windows)
                {
                    var matches = WindowMatcher.Candidates(current, w);
                    string action = matches.Count == 1 ? "기존 창 재사용" : matches.Count > 1 ? "연결 모호: 복원 보류" : "새 앱 실행";
                    string issue = PathIssue(w);
                    if (issue != "") action = issue;
                    if (!w.AllDesktops && w.DesktopIndex >= Native.GetDesktopCount()) action += " / 대상 데스크톱 부족";
                    if (w.Pinned) action = "고정 창: 건너뜀";
                    list.Items.Add($"{w.Title} — {action}", !w.Pinned);
                }
                panel.Controls.Add(list);
                var mode = new CheckBox { Text = "배치만 복원 (새 앱을 실행하지 않음)", AutoSize = true }; panel.Controls.Add(mode);
                panel.Controls.Add(new Label { Text = "실행 직전 창을 다시 확인합니다. 부족한 데스크톱 생성은 메인 화면 설정을 따릅니다.", AutoSize = true });
                AddAccept(preview, panel, () => list.CheckedIndices.Count > 0);
                if (preview.ShowDialog(this) != DialogResult.OK) return;
                data.Windows = list.CheckedIndices.Cast<int>().Select(i => data.Windows[i]).ToList(); layoutOnly = mode.Checked;
            }
            settings.Recent.Remove(p); settings.Recent.Insert(0, p); settings.Recent = settings.Recent.Take(10).ToList(); SaveSettings();
            await RunRestore(data.Windows, layoutOnly);
        }
        catch (Exception ex) { Write("복원 실패: " + ex.Message); MessageBox.Show(this, ex.Message, "복원 실패"); }
        finally { restoring = false; restore.Enabled = apiReady; RebuildTray(); RefreshProfiles(); }
    }

    private async Task RunRestore(List<SavedWindow> targets, bool layoutOnly)
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
                    string result;
                    try { result = await RestoreOne(w, layoutOnly, reserved, token); }
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
            if (allOk) exitAfter = true;
            if (exitAfter) progress.Close();
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

    private async Task<string> RestoreOne(SavedWindow target, bool layoutOnly, HashSet<IntPtr> reserved, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (target.Pinned) return "고정 창 건너뜀";
        if (!target.AllDesktops && (target.DesktopIndex < 0 || target.DesktopIndex >= Native.GetDesktopCount())) return "대상 데스크톱 없음";
        var matches = WindowMatcher.Candidates(ScanIdentified().Where(x => !reserved.Contains(x.Handle)), target);
        var action = RestorePolicy.Decide(matches.Count, layoutOnly);
        if (action == RestoreAction.Ambiguous) return "연결 모호: 같은 프로젝트의 중복 창을 확인하세요";
        LiveWindow? candidate = matches.SingleOrDefault();
        if (action == RestoreAction.Missing) return "실행 중인 대상 없음 (배치 모드: 실행 안 함)";
        if (action == RestoreAction.Launch)
        {
            string issue = PathIssue(target); if (issue != "") return issue;
            var plugin = pluginsCatalog.ById(PluginIdOf(target)) ?? pluginsCatalog.ForExe(target.Path);
            var psi = new ProcessStartInfo(target.Path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(target.Path)! };
            if (target.ProjectPath != "") foreach (var arg in plugin!.LaunchArguments) psi.ArgumentList.Add(arg.Replace("{projectPath}", target.ProjectPath));
            else if (target.OpenFiles.Count > 0)
                foreach (var arg in plugin!.LaunchArguments)
                    if (arg.Contains("{file}")) foreach (var file in target.OpenFiles) psi.ArgumentList.Add(arg.Replace("{file}", file));
                    else psi.ArgumentList.Add(arg);
            var before = Scanner.Scan().Select(x => x.Handle).ToHashSet();
            token.ThrowIfCancellationRequested();
            using var launched = Process.Start(psi);
            int attempts = (plugin?.WaitSeconds ?? 30) * 2;
            for (int i = 0; i < attempts && candidate == null; i++)
            {
                await Task.Delay(500, token);
                var current = ScanIdentified().Where(x => !reserved.Contains(x.Handle)).ToList();
                candidate = WindowMatcher.Unique(current, target);
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
        if (!target.AllDesktops && Native.MoveWindowToDesktopNumber(candidate.Handle, target.DesktopIndex) == -1) return "데스크톱 이동 실패";
        token.ThrowIfCancellationRequested();
        var wp = Native.WINDOWPLACEMENT.Init();
        if (!Native.GetWindowPlacement(candidate.Handle, ref wp)) return "창 위치 읽기 실패";
        var rect = ClampToScreens(target);
        wp.rcNormalPosition = new Native.RECT { Left = rect.Left, Top = rect.Top, Right = rect.Right, Bottom = rect.Bottom };
        wp.showCmd = target.Maximized ? 3 : target.Minimized ? 2 : 1;
        token.ThrowIfCancellationRequested();
        if (!Native.SetWindowPlacement(candidate.Handle, ref wp)) return "창 위치 이동 실패";
        reserved.Add(candidate.Handle); return "완료";
    }
}
