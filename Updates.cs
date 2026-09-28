using System.Text.Json;

namespace DesktopSessionManager;

// Checks GitHub Releases for a newer version. New Windows builds need a new VirtualDesktopAccessor,
// which only reaches users through a new app release.
internal static class Updates
{
    private const string Repo="garunnir/DesktopSessionManager";
    public const string ReleasesUrl=$"https://github.com/{Repo}/releases/latest";

    public static Version Current { get; }=Normalize(typeof(Updates).Assembly.GetName().Version ?? new Version(0,0,0));
    // Local builds are 0.0.0 and have nothing to compare against.
    public static bool Enabled => Current>new Version(0,0,0);

    public static async Task<(Version Version,string Url)?> CheckAsync()
    {
        using var http=new HttpClient{Timeout=TimeSpan.FromSeconds(10)};
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DesktopSessionManager/"+Current);
        using var doc=JsonDocument.Parse(await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest"));
        var tag=doc.RootElement.GetProperty("tag_name").GetString()?.TrimStart('v');
        var url=doc.RootElement.GetProperty("html_url").GetString() ?? ReleasesUrl;
        return Version.TryParse(tag,out var v) && Normalize(v)>Current ? (Normalize(v),url) : null;
    }

    private static Version Normalize(Version v) => new(v.Major,v.Minor,Math.Max(v.Build,0));
}
