using System.Text.Json;

namespace DesktopSessionManager;

internal static class ProfileStore
{
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    public static void Write<T>(string path, T value, bool backup = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, Json);
                stream.Flush(true);
            }
            if (backup && File.Exists(path))
            {
                string history = path + ".history";
                Directory.CreateDirectory(history);
                File.Copy(path, Path.Combine(history, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + ".json"));
            }
            if (backup && Directory.Exists(path + ".history"))
                foreach (var old in Directory.GetFiles(path + ".history", "*.json").OrderByDescending(x => x).Skip(20))
                    File.Delete(old);
            // Same-directory rename with replacement: no delete/truncate window, and no
            // ReplaceFile metadata/ACL merge (which may require extra access on shared drives).
            // All potentially failing backup work happens before the commit.
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static Snapshot Read(string path)
    {
        var result = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(path)) ?? throw new InvalidDataException("빈 프로필");
        if (result.SchemaVersion is < 1 or > 3 || result.Windows == null || result.Layouts == null || result.Windows.Any(InvalidWindow)
            || result.Layouts.Any(l => l == null || l.Windows == null || l.Windows.Any(InvalidWindow)))
            throw new InvalidDataException("지원하지 않거나 손상된 프로필");
        return result;
    }
    private static bool InvalidWindow(SavedWindow w) => w == null || w.Path == null || w.Title == null || w.ProjectPath == null || w.PluginId == null || w.OpenFiles == null || w.OpenFiles.Any(f => f == null);
}

internal static class WindowMatcher
{
    public static bool SamePath(string a, string b) => string.Equals(a.TrimEnd('\\', '/').Replace('/', '\\'), b.TrimEnd('\\', '/').Replace('/', '\\'), StringComparison.OrdinalIgnoreCase);
    public static List<LiveWindow> Candidates(IEnumerable<LiveWindow> source, SavedWindow target)
    {
        var same = source.Where(x => SamePath(x.Data.Path, target.Path)).ToList();
        if (!string.IsNullOrWhiteSpace(target.ProjectPath))
            return same.Where(x => SamePath(x.Data.ProjectPath, target.ProjectPath)).ToList();
        if (target.OpenFiles.Count > 0)
            return same.Where(x => new HashSet<string>(x.Data.OpenFiles.Select(Normalize), StringComparer.OrdinalIgnoreCase)
                .SetEquals(target.OpenFiles.Select(Normalize))).ToList();
        return same.Where(x => x.Data.ProjectPath == "" && x.Data.OpenFiles.Count == 0 && x.Data.Title == target.Title).ToList();
    }
    private static string Normalize(string path) => path.Replace('/', '\\');
    public static LiveWindow? Unique(IEnumerable<LiveWindow> source, SavedWindow target)
    {
        var matches = Candidates(source, target);
        return matches.Count == 1 ? matches[0] : null;
    }
}

public sealed class MonitorLayout
{
    public string Configuration { get; set; } = "";
    public List<SavedWindow> Windows { get; set; } = [];
}

internal sealed class UserSettings
{
    public bool CloseToTray { get; set; }
    public bool Hotkey { get; set; }
    public int AutoSaveMinutes { get; set; }
    public List<string> Favorites { get; set; } = [];
    public List<string> Recent { get; set; } = [];
}
