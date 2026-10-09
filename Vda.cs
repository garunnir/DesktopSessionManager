using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace DesktopSessionManager;

// Picks the VirtualDesktopAccessor release built for the running Windows build.
// VDA calls undocumented COM interfaces whose layout changes between Windows builds, so a
// mismatched DLL can corrupt memory. The table in the csproj (embedded as AssemblyMetadata) is used first.
// On a build missing from it, a DLL is used only after it passes a test in a separate process (RunProbe),
// and the result is remembered per build and UBR in vda-verified.json.
internal static class Vda
{
    private sealed record Release(string Tag,int Build,int MinUbr,string Sha256);
    private sealed class VerifiedDll
    {
        public int Build {get;set;}
        public int Ubr {get;set;}
        public string Tag {get;set;}="";
        public string Path {get;set;}="";
        public string Sha256 {get;set;}="";
    }
    private sealed class VerifiedCache
    {
        public List<VerifiedDll> Verified {get;set;}=[];
        // "build.ubr" -> last time no DLL worked
        public Dictionary<string,DateTime> Failed {get;set;}=[];
    }

    public const string ProbeArg="--probe-vda";
    private const string Repo="Ciantic/VirtualDesktopAccessor";
    private const string DllName="VirtualDesktopAccessor.dll";
    // Every export Native imports, plus GetCurrentDesktopNumber for the probe.
    private static readonly string[] RequiredExports=["GetDesktopCount","GetWindowDesktopNumber","MoveWindowToDesktopNumber","CreateDesktop","GetDesktopName","IsPinnedWindow","GetCurrentDesktopNumber"];
    private static readonly TimeSpan RetryAfterFailure=TimeSpan.FromDays(1);

    public static bool Loaded { get; private set; }
    public static string Status => string.Join(Environment.NewLine,notes);
    private static readonly List<string> notes=[];
    private static IntPtr handle;

    public static void Initialize(string dataRoot)
    {
        // Never fall back to the default search, which could pick up a stray DLL beside the EXE.
        NativeLibrary.SetDllImportResolver(typeof(Vda).Assembly,(name,_,_)=>
            name!=DllName ? IntPtr.Zero : Loaded ? handle : throw new DllNotFoundException(Status));
        var (build,ubr,display)=WindowsBuild();
        var meta=typeof(Vda).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToList();
        var aliases=meta.Where(m=>m.Key=="VdaBuildAlias").Select(m=>m.Value!.Split('|'))
            .ToDictionary(p=>int.Parse(p[0]),p=>int.Parse(p[1]));
        var releases=meta.Where(m=>m.Key=="VdaRelease").Select(m=>m.Value!.Split('|'))
            .Select(p=>new Release(p[0],int.Parse(p[1]),int.Parse(p[2]),p[3])).ToList();
        var ignored=meta.Where(m=>m.Key=="VdaIgnoredRelease").Select(m=>m.Value!).ToHashSet();
        int baseBuild=aliases.TryGetValue(build,out var b) ? b : build;
        var release=releases.Where(r=>r.Build==baseBuild && r.MinUbr<=ubr).MaxBy(r=>r.MinUbr);
        if(release!=null)
        {
            if(TryLoad(BundledPath(release.Tag),release.Sha256)) notes.Add($"Windows {display} → VirtualDesktopAccessor {release.Tag}");
            return;
        }
        var supported=string.Join(", ",releases.OrderBy(r=>r.Build).ThenBy(r=>r.MinUbr).Select(r=>$"{r.Build}.{r.MinUbr}+"));
        notes.Add($"Windows {display}은(는) VirtualDesktopAccessor 지원 표에 없습니다 (지원 빌드: {supported}). 호환 DLL을 자동으로 확인합니다.");
        try {FindCompatible(dataRoot,build,ubr,releases,ignored);}
        catch(Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {notes.Add("호환 DLL 확인 실패: "+ex.Message);}
        if(!Loaded) notes.Add("가상 데스크톱 기능을 끕니다. 앱을 업데이트하세요.");
    }

    private static void FindCompatible(string dataRoot,int build,int ubr,List<Release> releases,HashSet<string> ignored)
    {
        var cacheFile=Path.Combine(dataRoot,"vda-verified.json");
        var cache=File.Exists(cacheFile) ? JsonSerializer.Deserialize<VerifiedCache>(File.ReadAllText(cacheFile)) ?? new() : new();
        void Save() {Directory.CreateDirectory(dataRoot); File.WriteAllText(cacheFile,JsonSerializer.Serialize(cache,new JsonSerializerOptions{WriteIndented=true}));}

        if(cache.Verified.FirstOrDefault(v=>v.Build==build && v.Ubr==ubr) is {} hit)
        {
            if(TryLoad(hit.Path,hit.Sha256)) {notes.Add($"→ 이 PC에서 검증한 {hit.Tag} 사용"); return;}
            cache.Verified.Remove(hit); Save();
        }
        string key=$"{build}.{ubr}";
        if(cache.Failed.TryGetValue(key,out var failedAt) && DateTime.UtcNow-failedAt<RetryAfterFailure)
        {
            notes.Add($"→ 호환 DLL 없음 (마지막 확인 {failedAt.ToLocalTime():g}, {RetryAfterFailure.TotalHours:0}시간 뒤 다시 확인)");
            return;
        }

        bool Accept(string tag,string path,string sha)
        {
            if(!File.Exists(path)) {notes.Add($"→ {tag}: {path} 파일 없음"); return false;}
            if(!Probe(path)) {notes.Add($"→ {tag}: 이 Windows에서 동작하지 않음"); return false;}
            if(!TryLoad(path,sha)) return false;
            cache.Verified.RemoveAll(v=>v.Build==build && v.Ubr==ubr);
            cache.Verified.Add(new VerifiedDll{Build=build,Ubr=ubr,Tag=tag,Path=path,Sha256=sha});
            cache.Failed.Remove(key); Save();
            notes.Add($"→ {tag}: 동작 확인, 이 PC의 호환 목록에 추가");
            return true;
        }

        // Older DLLs target older interface layouts; only the newest bundled one is worth trying on a new build.
        var newest=releases.OrderByDescending(r=>r.Build).ThenByDescending(r=>r.MinUbr).First();
        if(Accept(newest.Tag,BundledPath(newest.Tag),newest.Sha256))
        {
            notes.Add($"   (개발자: csproj에 <VdaBuildAlias Include=\"{build}\" Base=\"{newest.Build}\" /> 추가 후보)");
            return;
        }

        var known=releases.Select(r=>r.Tag).Concat(ignored).ToHashSet();
        foreach(var (tag,url,sha) in NewerUpstreamReleases(known))
        {
            var path=Path.Combine(dataRoot,"vda",tag,DllName);
            if(!File.Exists(path) || !HashOf(path).Equals(sha,StringComparison.OrdinalIgnoreCase))
            {
                notes.Add($"→ VirtualDesktopAccessor 새 릴리즈 {tag} 다운로드");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                Download(url,path);
                if(!HashOf(path).Equals(sha,StringComparison.OrdinalIgnoreCase)) {notes.Add($"→ {tag}: SHA256 불일치, 건너뜀"); File.Delete(path); continue;}
            }
            if(Accept(tag,path,sha))
            {
                notes.Add($"   (개발자: scripts\\update-vda.ps1 -Tag {tag}로 지원 표에 추가 후보)");
                return;
            }
        }
        cache.Failed[key]=DateTime.UtcNow; Save();
    }

    // Upstream releases missing from the table, newest first. Only assets with a GitHub-recorded SHA256 are offered.
    private static List<(string Tag,string Url,string Sha256)> NewerUpstreamReleases(HashSet<string> known)
    {
        var found=new List<(string,string,string)>();
        bool any=false;
        try
        {
            using var http=CreateClient();
            using var doc=JsonDocument.Parse(http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases?per_page=20").GetAwaiter().GetResult());
            foreach(var rel in doc.RootElement.EnumerateArray().OrderByDescending(r=>r.GetProperty("published_at").GetString()))
            {
                var tag=rel.GetProperty("tag_name").GetString() ?? "";
                if(known.Contains(tag) || rel.GetProperty("draft").GetBoolean() || rel.GetProperty("prerelease").GetBoolean()) continue;
                var asset=rel.GetProperty("assets").EnumerateArray().FirstOrDefault(a=>a.GetProperty("name").GetString()==DllName);
                if(asset.ValueKind!=JsonValueKind.Object) continue;
                any=true;
                var digest=asset.TryGetProperty("digest",out var d) ? d.GetString() : null;
                if(digest==null || !digest.StartsWith("sha256:")) {notes.Add($"→ {tag}: GitHub SHA256 정보가 없어 건너뜀"); continue;}
                found.Add((tag,asset.GetProperty("browser_download_url").GetString()!,digest["sha256:".Length..]));
            }
            if(!any) notes.Add("→ VirtualDesktopAccessor 새 릴리즈 없음");
        }
        catch(Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {notes.Add("→ VirtualDesktopAccessor 릴리즈 확인 실패: "+ex.Message);}
        return found;
    }

    private static void Download(string url,string path)
    {
        using var http=CreateClient();
        var bytes=http.GetByteArrayAsync(url).GetAwaiter().GetResult();
        File.WriteAllBytes(path,bytes);
    }

    private static HttpClient CreateClient()
    {
        var http=new HttpClient{Timeout=TimeSpan.FromSeconds(15)};
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DesktopSessionManager/"+Updates.Current);
        return http;
    }

    // Runs RunProbe in a child process so a crash or corrupted state never reaches this one.
    private static bool Probe(string path)
    {
        try
        {
            using var p=Process.Start(new ProcessStartInfo(Updates.ExePath){ArgumentList={ProbeArg,path},UseShellExecute=false,CreateNoWindow=true});
            if(p==null) return false;
            if(!p.WaitForExit(15000)) {p.Kill(); return false;}
            return p.ExitCode==0;
        }
        catch(Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) {return false;}
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int IntFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int WindowFn(IntPtr hwnd);

    // Child process entry: 0 when the DLL answers consistently on this Windows build.
    public static int RunProbe(string path)
    {
        try
        {
            var lib=NativeLibrary.Load(path);
            foreach(var name in RequiredExports) NativeLibrary.GetExport(lib,name);
            var count=Marshal.GetDelegateForFunctionPointer<IntFn>(NativeLibrary.GetExport(lib,"GetDesktopCount"));
            var current=Marshal.GetDelegateForFunctionPointer<IntFn>(NativeLibrary.GetExport(lib,"GetCurrentDesktopNumber"));
            var desktopOf=Marshal.GetDelegateForFunctionPointer<WindowFn>(NativeLibrary.GetExport(lib,"GetWindowDesktopNumber"));
            int n=count();
            if(n<1 || n>100) return 2;
            int cur=current();
            if(cur<0 || cur>=n) return 3;
            // Visible, uncloaked windows are on the current desktop (or pinned to all).
            int seen=0,onCurrent=0; bool outOfRange=false;
            Native.EnumWindows((h,_)=>{
                if(!Native.IsWindowVisible(h) || Native.IsCloaked(h) || Native.GetWindowTextLengthW(h)==0) return true;
                int d=desktopOf(h); seen++;
                if(d<-1 || d>=n) {outOfRange=true; return false;}
                if(d==cur) onCurrent++;
                return true;
            },IntPtr.Zero);
            if(outOfRange) return 4;
            if(seen>0 && onCurrent==0) return 5;
            return count()==n ? 0 : 6;
        }
        catch(Exception) {return 1;}
    }

    private static string BundledPath(string tag) => Path.Combine(AppContext.BaseDirectory,"vda",tag,DllName);
    private static string HashOf(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static bool TryLoad(string path,string sha256)
    {
        if(!File.Exists(path)) {notes.Add($"{path} 파일이 없습니다. 배포 폴더를 다시 설치하세요."); return false;}
        if(!HashOf(path).Equals(sha256,StringComparison.OrdinalIgnoreCase)) {notes.Add($"{path} SHA256 불일치. 배포 폴더를 다시 설치하세요."); return false;}
        try {handle=NativeLibrary.Load(path);}
        catch(Exception ex) when (ex is DllNotFoundException or BadImageFormatException) {notes.Add($"{path} 로드 실패: {ex.Message}"); return false;}
        Loaded=true;
        return true;
    }

    private static (int Build,int Ubr,string Display) WindowsBuild()
    {
        using var key=Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        int build=int.TryParse(key?.GetValue("CurrentBuildNumber") as string,out var n) ? n : Environment.OSVersion.Version.Build;
        int ubr=key?.GetValue("UBR") is int u ? u : 0;
        var version=key?.GetValue("DisplayVersion") as string;
        return (build,ubr,$"{(version!=null ? version+" " : "")}{build}.{ubr}");
    }
}
