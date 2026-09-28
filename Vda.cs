using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace DesktopSessionManager;

// Picks the VirtualDesktopAccessor release built for the running Windows build.
// VDA calls undocumented COM interfaces whose layout changes between Windows builds, so a
// mismatched DLL can corrupt memory. No DLL is loaded unless the table in the csproj
// (embedded as AssemblyMetadata) has an entry for this build.
internal static class Vda
{
    private sealed record Release(string Tag,int Build,int MinUbr,string Sha256);

    public static bool Loaded { get; private set; }
    public static string Status { get; private set; }="";
    private static IntPtr handle;

    public static void Initialize()
    {
        // Never fall back to the default search, which could pick up a stray DLL beside the EXE.
        NativeLibrary.SetDllImportResolver(typeof(Vda).Assembly,(name,_,_)=>
            name!="VirtualDesktopAccessor.dll" ? IntPtr.Zero : Loaded ? handle : throw new DllNotFoundException(Status));
        var (build,ubr,display)=WindowsBuild();
        var meta=typeof(Vda).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToList();
        var aliases=meta.Where(m=>m.Key=="VdaBuildAlias").Select(m=>m.Value!.Split('|'))
            .ToDictionary(p=>int.Parse(p[0]),p=>int.Parse(p[1]));
        var releases=meta.Where(m=>m.Key=="VdaRelease").Select(m=>m.Value!.Split('|'))
            .Select(p=>new Release(p[0],int.Parse(p[1]),int.Parse(p[2]),p[3])).ToList();
        int baseBuild=aliases.TryGetValue(build,out var b) ? b : build;
        var release=releases.Where(r=>r.Build==baseBuild && r.MinUbr<=ubr).MaxBy(r=>r.MinUbr);
        if(release==null)
        {
            var supported=string.Join(", ",releases.OrderBy(r=>r.Build).ThenBy(r=>r.MinUbr).Select(r=>$"{r.Build}.{r.MinUbr}+"));
            Status=$"Windows {display}용 VirtualDesktopAccessor가 없습니다. 지원 빌드: {supported}. 앱을 업데이트하세요.";
            return;
        }
        var path=Path.Combine(AppContext.BaseDirectory,"vda",release.Tag,"VirtualDesktopAccessor.dll");
        if(!File.Exists(path)) {Status=$"{path} 파일이 없습니다. 배포 폴더를 다시 설치하세요."; return;}
        var hash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        if(!hash.Equals(release.Sha256,StringComparison.OrdinalIgnoreCase)) {Status=$"{path} SHA256 불일치. 배포 폴더를 다시 설치하세요."; return;}
        try {handle=NativeLibrary.Load(path);}
        catch(Exception ex) when (ex is DllNotFoundException or BadImageFormatException) {Status=$"{path} 로드 실패: {ex.Message}"; return;}
        Loaded=true;
        Status=$"Windows {display} → VirtualDesktopAccessor {release.Tag}";
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
