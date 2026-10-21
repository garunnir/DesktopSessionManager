using DesktopSessionManager;
using System.Text.Json;

int checks = 0;
void Assert(bool condition, string label)
{
    if (!condition) throw new Exception(label);
    checks++;
    Console.WriteLine("PASS " + label);
}
LiveWindow Live(int id, string project, string title = "same title") => new()
{
    Handle = (IntPtr)id, ProcessId = (uint)id,
    Data = new SavedWindow { Path = @"C:\Code.exe", ProjectPath = project, Title = title }
};
var first = Live(1, @"C:\a\same");
var second = Live(2, @"C:\b\same");
var target = new SavedWindow { Path = @"c:/Code.exe", ProjectPath = @"C:/a/same", Title = "old title" };
Assert(WindowMatcher.Unique([first, second], target) == first, "projects with same folder name use full path");
Assert(WindowMatcher.Unique([second], target) == null, "different project never reused");
Assert(WindowMatcher.Unique([first, Live(3, first.Data.ProjectPath)], target) == null, "ambiguous project stays unresolved");
Assert(WindowMatcher.Unique([Live(4, "")], target) == null, "unknown project is not guessed from title");
var bare = new SavedWindow { Path = first.Data.Path, Title = "same title" };
Assert(WindowMatcher.Unique([first], bare) == null, "bare target cannot capture a project window");
Assert(WindowMatcher.Unique([Live(4, ""), Live(5, "")], bare) == null, "duplicate bare titles are ambiguous");
var files = Live(6, ""); files.Data.OpenFiles = [@"C:\a.ase", @"C:\b.ase"];
var fileTarget = new SavedWindow { Path = files.Data.Path, OpenFiles = [@"C:\b.ase", @"C:\a.ase"] };
Assert(WindowMatcher.Unique([files], fileTarget) == files, "file identities ignore tab order");
fileTarget.OpenFiles.Add(@"C:\c.ase");
Assert(WindowMatcher.Unique([files], fileTarget) == null, "partial file session is not reused");
Assert(RestorePolicy.Decide(0, true) == RestoreAction.Missing, "layout-only never launches a missing app");
Assert(RestorePolicy.Decide(1, true) == RestoreAction.Reuse, "layout-only reuses an existing window");
Assert(RestorePolicy.Decide(2, false) == RestoreAction.Ambiguous, "ambiguous restore does not launch another app");
var visited = new List<string>();
await RestorePolicy.RunBatch([target], w => { visited.Add(w.Id); return Task.CompletedTask; }, CancellationToken.None);
Assert(visited.SequenceEqual(new[] { target.Id }), "only selected targets are processed");
using (var cancellation = new CancellationTokenSource())
{
    visited.Clear();
    try
    {
        await RestorePolicy.RunBatch([target, bare], w => { visited.Add(w.Id); cancellation.Cancel(); return Task.CompletedTask; }, cancellation.Token);
    }
    catch (OperationCanceledException) { }
    Assert(visited.SequenceEqual(new[] { target.Id }), "cancellation prevents every subsequent target");
}
string root = Path.Combine(Path.Combine(Directory.GetCurrentDirectory(), "tests"), "dsm-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    string path = Path.Combine(root, "profile.json");
    foreach (int schema in new[] { 1, 2, 3 })
    {
        File.WriteAllText(path, "{\"SchemaVersion\":" + schema + ",\"Name\":\"legacy\",\"Windows\":[{\"Path\":\"app.exe\"}]}");
        var old = ProfileStore.Read(path);
        Assert(old.Windows[0].OpenFiles.Count == 0 && old.Layouts.Count == 0, "legacy schema " + schema + " optional defaults");
        ProfileStore.Write(path, old, true);
        Assert(ProfileStore.Read(path).Name == "legacy", "legacy schema " + schema + " roundtrip");
    }
    var snapshot = new Snapshot { SchemaVersion = 3, Name = "changed", Windows = [target], Layouts = [new MonitorLayout { Configuration = "test", Windows = [target] }] };
    ProfileStore.Write(path, snapshot, true);
    Assert(ProfileStore.Read(path).Layouts[0].Windows[0].Id == target.Id, "monitor layout and stable identity roundtrip");
    var history = Directory.GetFiles(path + ".history").OrderBy(x => x).Last();
    Assert(ProfileStore.Read(history).Name == "legacy", "overwrite preserves previous snapshot");
    for (int i = 0; i < 25; i++) { snapshot.Name = "revision " + i; ProfileStore.Write(path, snapshot, true); }
    Assert(Directory.GetFiles(path + ".history").Length == 20, "backup retention limit");
    string prior = File.ReadAllText(path);
    using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        bool failed = false;
        try { ProfileStore.Write(path, new Snapshot { Name = "must fail" }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed = true; }
        Assert(failed, "replacement failure surfaced");
    }
    Assert(File.ReadAllText(path) == prior, "failed save preserves original bytes");
    Assert(Directory.GetFiles(root, "*.tmp").Length == 0, "failed save cleans temporary file");
    var backup = ProfileStore.Read(Directory.GetFiles(path + ".history").OrderBy(x => x).First());
    ProfileStore.Write(path, backup, true);
    Assert(Directory.GetFiles(path + ".history").Any(f => File.ReadAllText(f) == prior), "restore backs up current version");
    string manual = File.ReadAllText(path);
    ProfileStore.Write(Path.Combine(root, "Recovery", "autosave.json"), snapshot);
    Assert(File.ReadAllText(path) == manual, "autosave leaves manual profile untouched");
    File.WriteAllText(path, "{\"Windows\":[{\"OpenFiles\":null}]}");
    bool invalid = false;
    try { ProfileStore.Read(path); } catch (InvalidDataException) { invalid = true; }
    Assert(invalid, "malformed profile rejected before editing");
}
finally { Directory.Delete(root, true); }

string godotRoot = Path.Combine(Path.GetTempPath(), "dsm-label-" + Guid.NewGuid().ToString("N"), "project-beta");
Directory.CreateDirectory(godotRoot);
try
{
    File.WriteAllLines(Path.Combine(godotRoot, "project.godot"), ["[application]", "config/name=\"Project\""]);
    Assert(ProjectDetector.Label(godotRoot, new AppPlugin { ProjectPicker = "godot-folder" }) == "Project", "Godot title uses config name");
    Assert(ProjectDetector.Label(godotRoot, new AppPlugin { ProjectPicker = "workspace-or-folder" }) == "project-beta", "VS Code title uses folder name of a Godot project");
}
finally { Directory.Delete(Path.GetDirectoryName(godotRoot)!, true); }
Assert(ProjectDetector.VsCodeFilePath(new Uri("file:///z%3A/Work/a")) == @"Z:\Work\a", "VS Code URI drive letter normalized");
SavedWindow OnDesktop(int desktop, string name = "") => new() { DesktopIndex = desktop, DesktopName = name };
var d0 = OnDesktop(0, "code"); var d1 = OnDesktop(1, "art"); var d2 = OnDesktop(2); var pinned = OnDesktop(2); pinned.Pinned = true;
var everywhere = OnDesktop(-1); everywhere.AllDesktops = true;
List<SavedWindow> board = [d0, d1, d2, pinned, everywhere];
Assert(DesktopOrder.Count(board) == 3, "board shows every desktop a movable window uses");
DesktopOrder.Reorder(board, 0, 2);
Assert(d0.DesktopIndex == 2 && d1.DesktopIndex == 0 && d2.DesktopIndex == 1 && pinned.DesktopIndex == 1, "moving a desktop later shifts the ones in between forward");
DesktopOrder.Reorder(board, 2, 0);
Assert(d0.DesktopIndex == 0 && d1.DesktopIndex == 1 && d2.DesktopIndex == 2 && everywhere.DesktopIndex == -1, "moving a desktop earlier restores the order; all-desktop windows untouched");
DesktopOrder.MoveTo([d2, pinned], board, 1);
Assert(d2.DesktopIndex == 1 && d2.DesktopName == "art" && pinned.DesktopIndex == 2, "moved window joins the target desktop and its name; pinned stays");
Assert(DesktopOrder.Count(board) == 2, "emptied last desktop disappears");
var gap = OnDesktop(3); board.Add(gap);
DesktopOrder.Reorder(board, 2, DesktopOrder.Count(board) - 1);
Assert(gap.DesktopIndex == 2 && DesktopOrder.Count(board) == 3, "deleting an empty desktop pulls later desktops forward");
var onLeft = new SavedWindow { Left = 100, Top = 50, Width = 800, Height = 600 };
DesktopOrder.MoveToMonitor(onLeft, new System.Drawing.Rectangle(0, 0, 1920, 1080), new System.Drawing.Rectangle(1920, -200, 2560, 1440));
Assert(onLeft.Left == 2020 && onLeft.Top == -150 && onLeft.Width == 800, "monitor move keeps the offset within the monitor");
Console.WriteLine($"{checks} checks passed");

