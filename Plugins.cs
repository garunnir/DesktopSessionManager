using System.Text;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace DesktopSessionManager;

// Data-only extension: no code/DLL loading. ArgumentList passes each entry without shell evaluation.
public sealed class AppPlugin
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> ExecutableNames { get; set; } = [];
    public string ProjectPicker { get; set; } = "folder"; // folder, workspace-or-folder, unity-folder, godot-folder, aseprite-files
    public List<string> LaunchArguments { get; set; } = [];
    public int WaitSeconds { get; set; } = 45;
    // The app restores a list of open files ({file}) instead of one project ({projectPath}).
    [JsonIgnore] public bool OpensFiles => ProjectPicker == "aseprite-files";
}

internal sealed class PluginCatalog
{
    public string Folder { get; } = Path.Combine(AppContext.BaseDirectory, "plugins");
    private readonly Dictionary<string, AppPlugin> entries = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyCollection<AppPlugin> All => entries.Values.OrderBy(x=>x.Name).ToList();
    public PluginCatalog() { Directory.CreateDirectory(Folder); Reload(); }
    public static string Validate(AppPlugin p)
    {
        if (string.IsNullOrWhiteSpace(p.Id) || p.Id.Length > 64 || p.Id.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_')))
            return "ID는 영문/숫자/-/_ 1~64자로 지정하세요.";
        if (string.IsNullOrWhiteSpace(p.Name) || p.Name.Length > 100) return "프로그램 이름은 1~100자로 지정하세요.";
        if (p.ExecutableNames.Count == 0 || p.ExecutableNames.Any(n => string.IsNullOrWhiteSpace(n) || Path.GetFileName(n) != n || !n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
            return "실행 파일명은 경로 없이 EXE 이름으로 지정하세요. (예: Code.exe)";
        if (p.ProjectPicker is not ("folder" or "workspace-or-folder" or "unity-folder" or "godot-folder" or "aseprite-files")) return "지원하지 않는 프로젝트 선택 방식입니다.";
        string variable = p.OpensFiles ? "{file}" : "{projectPath}";
        if (p.LaunchArguments.Count > 20 || p.LaunchArguments.Any(a => a.Length > 4096 || a.Replace(variable, "").IndexOfAny(['{', '}']) >= 0))
            return $"인자는 20개 이내이며 {variable} 변수만 사용할 수 있습니다.";
        if (!p.LaunchArguments.Any(a => a.Contains(variable))) return $"인자에 {variable}를 포함해야 합니다.";
        if (p.WaitSeconds is < 5 or > 180) return "대기 시간은 5~180초로 입력하세요.";
        return "";
    }
    public List<string> Reload()
    {
        entries.Clear(); List<string> errors=[];
        foreach (var file in Directory.GetFiles(Folder, "*.json").OrderBy(x=>x))
        {
            try
            {
                var p=JsonSerializer.Deserialize<AppPlugin>(File.ReadAllText(file,Encoding.UTF8),new JsonSerializerOptions{PropertyNameCaseInsensitive=true});
                if(p==null) throw new InvalidDataException("빈 설정");
                var issue=Validate(p); if(issue!="") throw new InvalidDataException(issue);
                if(entries.ContainsKey(p.Id)) throw new InvalidDataException("중복 ID: "+p.Id);
                entries.Add(p.Id,p);
            }
            catch(Exception ex) { errors.Add($"{Path.GetFileName(file)}: {ex.Message}"); }
        }
        return errors;
    }
    public AppPlugin? ById(string id) => entries.GetValueOrDefault(id);
    public AppPlugin? ForExe(string path) => All.FirstOrDefault(p=>p.ExecutableNames.Any(n=>n.Equals(Path.GetFileName(path),StringComparison.OrdinalIgnoreCase)));
    public void Save(AppPlugin p, string originalId)
    {
        var issue=Validate(p); if(issue!="") throw new InvalidDataException(issue);
        if (!string.Equals(originalId,p.Id,StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(Folder,p.Id+".json")))
            throw new IOException("다른 플러그인이 해당 ID를 사용 중입니다.");
        string path=Path.Combine(Folder,p.Id+".json");
        File.WriteAllText(path,JsonSerializer.Serialize(p,new JsonSerializerOptions{WriteIndented=true}),new UTF8Encoding(false));
        if(!string.IsNullOrEmpty(originalId) && !originalId.Equals(p.Id,StringComparison.OrdinalIgnoreCase))
        {
            string old=Path.Combine(Folder,originalId+".json");
            // Do not delete arbitrary third-party files; leave original untouched.
        }
        Reload();
    }
}

// Project paths assigned before, per plugin ID, most recent first. Lets later scans link windows whose
// project cannot be read from the process (e.g. Unity started from the Hub, VS Code history cleared).
internal sealed class ProjectCache(string file)
{
    private const int MaxPerPlugin = 100;
    private Dictionary<string, List<string>> entries = new(StringComparer.OrdinalIgnoreCase);
    public bool Exists => File.Exists(file);
    public IReadOnlyList<string> For(string pluginId) => entries.GetValueOrDefault(pluginId) ?? [];
    public void Load()
    {
        try
        {
            if (!File.Exists(file)) return;
            var loaded = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(file, Encoding.UTF8));
            if (loaded != null) entries = new(loaded, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
    }
    public void Remember(string pluginId, string path)
    {
        if (string.IsNullOrWhiteSpace(pluginId) || string.IsNullOrWhiteSpace(path)) return;
        if (!entries.TryGetValue(pluginId, out var list)) entries[pluginId] = list = [];
        list.RemoveAll(x => x.Equals(path, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, path);
        if (list.Count > MaxPerPlugin) list.RemoveRange(MaxPerPlugin, list.Count - MaxPerPlugin);
    }
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
    }
}

// Best-effort detection of the project a running window has open. Anything uncertain is left empty
// so the user can still assign it manually.
internal sealed class ProjectDetector(ProjectCache cache)
{
    private readonly Dictionary<string, VsCodeState> vscodeStates = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsValid(string path, AppPlugin plugin)
    {
        if (plugin.ProjectPicker == "unity-folder") return Directory.Exists(Path.Combine(path, "Assets")) && Directory.Exists(Path.Combine(path, "ProjectSettings"));
        if (plugin.ProjectPicker == "godot-folder") return File.Exists(Path.Combine(path, "project.godot"));
        if (plugin.ProjectPicker == "workspace-or-folder") return Directory.Exists(path) || (File.Exists(path) && path.EndsWith(".code-workspace", StringComparison.OrdinalIgnoreCase));
        return Directory.Exists(path);
    }

    // The project name the app shows in its window title.
    public static string Label(string path)
    {
        if (path.EndsWith(".code-workspace", StringComparison.OrdinalIgnoreCase)) return Path.GetFileNameWithoutExtension(path);
        string godot = Path.Combine(path, "project.godot"); // Godot titles use application/config/name, not the folder
        try
        {
            if (File.Exists(godot))
                foreach (var line in File.ReadLines(godot))
                    if (line.StartsWith("config/name=\"") && line.EndsWith('"') && line.Length > 14)
                        return line[13..^1].Replace("\\\"", "\"");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return Path.GetFileName(path.TrimEnd('\\', '/'));
    }

    public string? Detect(LiveWindow w, AppPlugin plugin)
    {
        string? path = FromCommandLine(w.ProcessId, plugin);
        // Normalize separators: Godot passes Z:/a/b.
        try { if (path != null) path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { path = null; }
        if (path != null && IsValid(path, plugin) && w.Data.Title.Contains(Label(path), StringComparison.OrdinalIgnoreCase)) return path;
        bool vscode = plugin.ProjectPicker == "workspace-or-folder";
        if (vscode && FromVsCodeState(w.Data, plugin) is { } found) return found;
        // Last resort: projects the user assigned before.
        var known = cache.For(plugin.Id).Where(x => IsValid(x, plugin)).ToList();
        return known.Count == 0 ? null : MatchByName(w.Data.Title, Segments(w.Data.Title, vscode ? StateFor(w.Data.Path).Separator : " - "), known);
    }

    // Written by integrations/aseprite/desktop-session-bridge; Aseprite shows only the active tab in its title.
    public const string AsepriteListFile = "desktop-session-open-files.txt";

    // Files open in an Aseprite window, or null when the bridge extension is not running in it.
    public static List<string>? AsepriteOpenFiles(LiveWindow w)
    {
        // Portable installs keep aseprite.ini (and the user config) beside the EXE.
        string exeDir = Path.GetDirectoryName(w.Data.Path) ?? "";
        string config = File.Exists(Path.Combine(exeDir, "aseprite.ini")) ? exeDir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Aseprite");
        string file = Path.Combine(config, AsepriteListFile);
        try
        {
            if (!File.Exists(file)) return null;
            // A list older than the process was left by an earlier run: the bridge is not loaded now.
            using var process = Process.GetProcessById((int)w.ProcessId);
            if (File.GetLastWriteTime(file) < process.StartTime) return null;
            return File.ReadAllLines(file, Encoding.UTF8).Select(x => x.Trim()).Where(x => x != "")
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }

    private static List<string> Segments(string title, string separator) =>
        title.Split(separator).Select(x => x.Trim().TrimStart('●', ' ')).Where(x => x != "").ToList();

    // Reverse the plugin's own launch arguments: "-projectPath {projectPath}" → value after -projectPath.
    private static string? FromCommandLine(uint pid, AppPlugin plugin)
    {
        int i = plugin.LaunchArguments.FindIndex(a => a.Contains("{projectPath}"));
        if (i < 0) return null;
        string[] argv = Native.GetCommandLine(pid);
        string pattern = plugin.LaunchArguments[i];
        if (pattern != "{projectPath}")
        {
            int at = pattern.IndexOf("{projectPath}");
            string prefix = pattern[..at], suffix = pattern[(at + "{projectPath}".Length)..];
            if (prefix == "") return null;
            var hit = argv.Skip(1).FirstOrDefault(a => a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && a.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && a.Length > prefix.Length + suffix.Length);
            return hit?[prefix.Length..^suffix.Length];
        }
        if (i == 0) return null;
        string flag = plugin.LaunchArguments[i - 1];
        int f = Array.FindIndex(argv, 1, a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
        return f > 0 && f + 1 < argv.Length ? argv[f + 1] : null;
    }

    // VS Code runs all windows in one process, so the command line cannot tell windows apart.
    // Instead match the window title against the folders VS Code itself records as open.
    private sealed record VsCodeState(List<string> Opened, string Separator);

    private string? FromVsCodeState(SavedWindow w, AppPlugin plugin)
    {
        var segments = Segments(w.Title, StateFor(w.Path).Separator);

        // 1) A title that shows the full path (window.title with ${rootPath} or ${folderPath}).
        foreach (var s in segments)
            if (Path.IsPathFullyQualified(s) && IsValid(s, plugin)) return Path.TrimEndingDirectorySeparator(s);

        return MatchByName(w.Title, segments, StateFor(w.Path).Opened.Where(x => IsValid(x, plugin)).ToList());
    }

    private VsCodeState StateFor(string exePath)
    {
        if (!vscodeStates.TryGetValue(exePath, out var state)) vscodeStates[exePath] = state = ReadVsCodeState(exePath);
        return state;
    }

    // Pick the one candidate whose project name the title shows; null when none or still ambiguous.
    private static string? MatchByName(string title, List<string> segments, List<string> candidates)
    {
        // 2) A title segment equal to the folder/workspace name (default title layout).
        var matches = candidates.Where(x => segments.Any(s => s.Equals(Label(x), StringComparison.OrdinalIgnoreCase)
            || s.Equals(Label(x) + " (Workspace)", StringComparison.OrdinalIgnoreCase))).ToList();
        // 3) Custom title layouts: the name anywhere in the title as a whole word.
        if (matches.Count == 0)
            matches = candidates.Where(x => Regex.IsMatch(title, $@"(?<![\p{{L}}\p{{N}}_.-]){Regex.Escape(Label(x))}(?![\p{{L}}\p{{N}}_.-])", RegexOptions.IgnoreCase)).ToList();
        if (matches.Count == 1) return matches[0];
        // 4) Same-named folders: prefer the one that contains the active editor's file (first title segment).
        if (matches.Count > 1 && segments.Count > 1)
        {
            var owners = matches.Where(x => Directory.Exists(x) && ContainsFile(x, segments[0])).ToList();
            if (owners.Count == 1) return owners[0];
        }
        return null; // still ambiguous: leave for manual assignment
    }

    private static bool ContainsFile(string folder, string fileName)
    {
        if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true };
            return Directory.EnumerateFiles(folder, fileName, options).Take(1).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static VsCodeState ReadVsCodeState(string exePath)
    {
        // Portable installs keep data beside the EXE; normal installs use %APPDATA%\<product name>.
        string portable = Path.Combine(Path.GetDirectoryName(exePath) ?? "", "data", "user-data", "User");
        string user = Directory.Exists(portable) ? portable
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Path.GetFileNameWithoutExtension(exePath), "User");
        var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        List<string> opened = [];
        string separator = " - ";
        try
        {
            string file = Path.Combine(user, "globalStorage", "storage.json");
            if (File.Exists(file))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file, Encoding.UTF8), options);
                void Collect(JsonElement e)
                {
                    if (e.ValueKind == JsonValueKind.Object)
                        foreach (var prop in e.EnumerateObject())
                        {
                            if (prop.Value.ValueKind == JsonValueKind.String && prop.Name is "folderUri" or "folder" or "configPath"
                                && Uri.TryCreate(prop.Value.GetString(), UriKind.Absolute, out var uri) && uri.IsFile)
                                opened.Add(Path.TrimEndingDirectorySeparator(uri.LocalPath));
                            else Collect(prop.Value);
                        }
                    else if (e.ValueKind == JsonValueKind.Array)
                        foreach (var item in e.EnumerateArray()) Collect(item);
                }
                foreach (var key in new[] { "backupWorkspaces", "windowsState" })
                    if (doc.RootElement.TryGetProperty(key, out var section)) Collect(section);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        try
        {
            string settings = Path.Combine(user, "settings.json");
            if (File.Exists(settings))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(settings, Encoding.UTF8), options);
                if (doc.RootElement.TryGetProperty("window.titleSeparator", out var sep) && sep.ValueKind == JsonValueKind.String && sep.GetString() is { Length: > 0 } value)
                    separator = value;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return new VsCodeState(opened.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), separator);
    }
}

internal sealed class PluginEditor : Form
{
    private readonly TextBox id=new(){Width=320}, label=new(){Width=320}, names=new(){Width=320}, args=new(){Width=450,Multiline=true,Height=100,ScrollBars=ScrollBars.Vertical};
    private readonly ComboBox picker=new(){Width=320,DropDownStyle=ComboBoxStyle.DropDownList};
    private readonly NumericUpDown wait=new(){Minimum=5,Maximum=180,Value=45,Width=120};
    public AppPlugin? Result {get;private set;}
    public PluginEditor(AppPlugin? value=null)
    {
        Text="확장 기능 추가 / 편집"; Width=660;Height=480;StartPosition=FormStartPosition.CenterParent;Font=new System.Drawing.Font("Malgun Gothic",9);
        picker.Items.AddRange(["folder", "workspace-or-folder", "unity-folder", "godot-folder", "aseprite-files"]); picker.SelectedIndex=0;
        var panel=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=8,Padding=new Padding(12),AutoScroll=true};
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,155)); panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        void Add(string title,Control c,int row){panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));panel.Controls.Add(new Label{Text=title,AutoSize=true,Margin=new Padding(3,8,3,3)},0,row);panel.Controls.Add(c,1,row);}
        Add("플러그인 ID",id,0);Add("표시 이름",label,1);Add("EXE 이름 (쉼표 구분)",names,2);Add("프로젝트 선택 방식",picker,3);
        Add("실행 인자 (한 줄에 하나)",args,4);Add("창 대기 시간 (초)",wait,5);
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));panel.Controls.Add(new Label{Text="{projectPath}가 실제 폴더/워크스페이스 경로로 치환됩니다. 예: --new-window (첫 줄), {projectPath} (둘째 줄). aseprite-files 방식은 {file} 인자가 열린 파일마다 하나씩 반복됩니다.",AutoSize=true,MaximumSize=new System.Drawing.Size(445,0)},1,6);
        var buttons=new FlowLayoutPanel{AutoSize=true,FlowDirection=FlowDirection.RightToLeft,Dock=DockStyle.Fill};
        var save=new Button{Text="저장",Width=95};var cancel=new Button{Text="취소",Width=95};buttons.Controls.Add(save);buttons.Controls.Add(cancel);
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));panel.Controls.Add(buttons,1,7);Controls.Add(panel);
        cancel.Click+=(_,_)=>{DialogResult=DialogResult.Cancel;Close();};
        if(value!=null){id.Text=value.Id;id.ReadOnly=true;label.Text=value.Name;names.Text=string.Join(", ",value.ExecutableNames);picker.SelectedItem=value.ProjectPicker;args.Lines=value.LaunchArguments.ToArray();wait.Value=Math.Clamp(value.WaitSeconds,5,180);}
        save.Click+=(_,_)=>
        {
            var p=new AppPlugin{Id=id.Text.Trim(),Name=label.Text.Trim(),ExecutableNames=names.Text.Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries).ToList(),ProjectPicker=picker.SelectedItem?.ToString()??"folder",LaunchArguments=args.Lines.Select(x=>x.Trim()).Where(x=>x.Length>0).ToList(),WaitSeconds=(int)wait.Value};
            string issue=PluginCatalog.Validate(p);if(issue!=""){MessageBox.Show(issue,"설정 확인");return;}
            Result=p;DialogResult=DialogResult.OK;Close();
        };
    }
}
