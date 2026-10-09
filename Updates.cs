using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DesktopSessionManager;

// Checks GitHub Releases for a newer version and installs it in place. New Windows builds need a new
// VirtualDesktopAccessor, which only reaches users through a new app release.
internal static class Updates
{
    private const string Repo="garunnir/DesktopSessionManager";
    public const string ReleasesUrl=$"https://github.com/{Repo}/releases/latest";
    // Files replaced by an update are renamed with this suffix (a running EXE or loaded DLL can be renamed
    // but not overwritten) and deleted on the next start.
    private const string OldSuffix=".update-old";

    public static Version Current { get; }=Normalize(typeof(Updates).Assembly.GetName().Version ?? new Version(0,0,0));
    // Local builds are 0.0.0 and have nothing to compare against.
    public static bool Enabled => Current>new Version(0,0,0);
    private static readonly string AppDir=AppContext.BaseDirectory;
    private static readonly string WorkDir=Path.Combine(Path.GetTempPath(),"DesktopSessionManager-update");
    // Captured at startup: after an update renames the running EXE, the original path holds the new one.
    public static string ExePath { get; }=Environment.ProcessPath ?? Path.Combine(AppDir,"DesktopSessionManager.exe");

    public sealed record Release(Version Version,string PageUrl,string? ZipUrl,string? Sha256)
    {
        public bool HasPackage => ZipUrl!=null && Sha256!=null;
    }

    public static async Task<Release?> CheckAsync()
    {
        using var http=CreateClient(TimeSpan.FromSeconds(10));
        using var doc=JsonDocument.Parse(await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest"));
        var root=doc.RootElement;
        var tag=root.GetProperty("tag_name").GetString()?.TrimStart('v');
        if(!Version.TryParse(tag,out var v) || Normalize(v)<=Current) return null;
        var page=root.GetProperty("html_url").GetString() ?? ReleasesUrl;
        string? zipUrl=null,sha=null;
        foreach(var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name=asset.GetProperty("name").GetString() ?? "";
            if(!name.EndsWith("-win-x64.zip",StringComparison.OrdinalIgnoreCase)) continue;
            zipUrl=asset.GetProperty("browser_download_url").GetString();
            // GitHub records "sha256:<hex>" for uploaded assets; older releases only have it in the notes (package.ps1).
            if(asset.TryGetProperty("digest",out var d) && d.GetString() is {} digest && digest.StartsWith("sha256:"))
                sha=digest["sha256:".Length..];
            else if(root.TryGetProperty("body",out var body) &&
                Regex.Match(body.GetString() ?? "",$@"SHA256 \(`?{Regex.Escape(name)}`?\): `?([0-9a-fA-F]{{64}})") is {Success:true} m)
                sha=m.Groups[1].Value;
            break;
        }
        return new Release(Normalize(v),page,zipUrl,sha?.ToLowerInvariant());
    }

    // Null when the update can be installed here; otherwise why not.
    public static string? CannotInstallReason(Release r)
    {
        if(!r.HasPackage) return "릴리즈에 설치 파일(zip, SHA256)이 없습니다";
        try
        {
            var probe=Path.Combine(AppDir,$".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe,""); File.Delete(probe);
            return null;
        }
        catch(Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return $"프로그램 폴더에 쓸 수 없습니다 ({AppDir})"; }
    }

    // Downloads, checks SHA256 and extracts the release. Returns the folder holding the new files; the app is untouched.
    public static async Task<string> DownloadAsync(Release r)
    {
        if(Directory.Exists(WorkDir)) Directory.Delete(WorkDir,true);
        Directory.CreateDirectory(WorkDir);
        var zip=Path.Combine(WorkDir,"update.zip");
        using(var http=CreateClient(TimeSpan.FromMinutes(5)))
        using(var response=await http.GetAsync(r.ZipUrl,HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            await using var file=File.Create(zip);
            await response.Content.CopyToAsync(file);
        }
        string hash;
        await using(var file=File.OpenRead(zip)) hash=Convert.ToHexString(await SHA256.HashDataAsync(file)).ToLowerInvariant();
        if(hash!=r.Sha256) throw new InvalidDataException($"SHA256 불일치 (받은 파일 {hash}, 릴리즈 {r.Sha256})");
        var extracted=Path.Combine(WorkDir,"files");
        ZipFile.ExtractToDirectory(zip,extracted);
        // The zip has one version folder at its root (package.ps1).
        var exe=Directory.GetFiles(extracted,"DesktopSessionManager.exe",SearchOption.AllDirectories);
        if(exe.Length!=1) throw new InvalidDataException("압축 파일에서 DesktopSessionManager.exe를 찾지 못했습니다");
        return Path.GetDirectoryName(exe[0])!;
    }

    // Copies the new files over the app folder, rolling back on failure. Existing plugin settings are kept.
    public static void Apply(string source)
    {
        var replaced=new List<(string Target,string Backup)>();
        var added=new List<string>();
        try
        {
            foreach(var file in Directory.GetFiles(source,"*",SearchOption.AllDirectories))
            {
                var rel=Path.GetRelativePath(source,file);
                if(rel.EndsWith(".pdb",StringComparison.OrdinalIgnoreCase)) continue;
                // The user may have renamed the EXE.
                var target=rel.Equals("DesktopSessionManager.exe",StringComparison.OrdinalIgnoreCase) ? ExePath : Path.Combine(AppDir,rel);
                if(rel.StartsWith("plugins"+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)
                    && (File.Exists(target) || File.Exists(target+".disabled"))) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if(File.Exists(target))
                {
                    var backup=target+OldSuffix;
                    File.Delete(backup);
                    File.Move(target,backup);
                    replaced.Add((target,backup));
                }
                File.Copy(file,target);
                added.Add(target);
            }
        }
        catch
        {
            foreach(var f in added) TryDelete(f);
            foreach(var (target,backup) in replaced) {try{File.Move(backup,target,true);}catch(IOException){}}
            throw;
        }
    }

    // Removes files left by the previous update. Some may still be locked while the old process exits.
    public static void CleanupOldFiles()
    {
        try
        {
            foreach(var f in Directory.EnumerateFiles(AppDir,"*"+OldSuffix,SearchOption.AllDirectories)) TryDelete(f);
            if(Directory.Exists(WorkDir)) Directory.Delete(WorkDir,true);
        }
        catch(Exception ex) when (ex is IOException or UnauthorizedAccessException) {}
    }

    private static void TryDelete(string path)
    {
        try{File.Delete(path);}catch(Exception ex) when (ex is IOException or UnauthorizedAccessException){}
    }

    private static HttpClient CreateClient(TimeSpan timeout)
    {
        var http=new HttpClient{Timeout=timeout};
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DesktopSessionManager/"+Current);
        return http;
    }

    private static Version Normalize(Version v) => new(v.Major,v.Minor,Math.Max(v.Build,0));
}
