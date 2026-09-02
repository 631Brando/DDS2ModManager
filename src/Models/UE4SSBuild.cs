using System.Text.RegularExpressions;

namespace DDS2ModManager.Models;

/// One specific UE4SS build, as published in the archive of every experimental build.
///
/// This exists because "which UE4SS am I running" has no useful answer otherwise. Every build in
/// the 3.0.1 line reports itself as "v3.0.1 Beta" - hundreds of them - so the version string
/// identifies nothing. The build number and the commit SHA in the release filename are the only
/// things that tell two of them apart, which is why a user reporting a regression quotes those.
public partial class UE4SSBuild
{
    /// The release asset this came from, e.g. "UE4SS_v3.0.1-1093-gba2efd55.zip". The identity.
    public required string AssetName { get; init; }

    public required string DownloadUrl { get; init; }
    public long Size { get; init; }

    public int Major { get; init; }
    public int Minor { get; init; }
    public int Patch { get; init; }

    /// Commits since the version tag. Monotonic within a version line, and the only ordering that
    /// actually works here - 1111 came after 1093, while the version strings are identical.
    public int Build { get; init; }

    public required string Sha { get; init; }

    /// The "zDEV-" builds open a console window with live UE4SS logs. Functionally the same for
    /// mods, and the difference is invisible in the version - which is how someone can be moved
    /// off the console build by an update and see only that their logging stopped.
    public bool IsDevBuild { get; init; }

    public string Version => $"{Major}.{Minor}.{Patch}";

    public string Display => $"{Version} build {Build}  ·  {Sha}{(IsDevBuild ? "  ·  console" : "")}";

    public string SizeDisplay => $"{Size / 1024.0 / 1024.0:F1} MB";

    /// Reads a build out of a release asset name, or null when the name is not one.
    ///
    /// Deliberately strict. The archive also carries much older shapes - UE4SS_Standard_v2.5.2-…,
    /// UE4SS_Xinput_…, UE4SS-2.XDev-windows.zip - from an era with a different archive layout, and
    /// the installer expects dwmapi.dll beside a ue4ss\ folder. Offering a build that cannot
    /// install is worse than not listing it: it fails after the download, not before.
    public static UE4SSBuild? FromAssetName(string name, string downloadUrl, long size)
    {
        var m = AssetPattern().Match(name);
        if (!m.Success) return null;

        return new UE4SSBuild
        {
            AssetName = name,
            DownloadUrl = downloadUrl,
            Size = size,
            IsDevBuild = m.Groups[1].Success,
            Major = int.Parse(m.Groups[2].Value),
            Minor = int.Parse(m.Groups[3].Value),
            Patch = int.Parse(m.Groups[4].Value),
            Build = int.Parse(m.Groups[5].Value),
            Sha = "g" + m.Groups[6].Value
        };
    }

    /// Newest first: version descending, then build number descending.
    ///
    /// Build number is compared as a NUMBER. Sorting these as text puts 998 above 1111, which would
    /// present the wrong build as the newest at the top of a list someone is picking from.
    public static int Newest(UE4SSBuild a, UE4SSBuild b)
    {
        var version = (b.Major, b.Minor, b.Patch).CompareTo((a.Major, a.Minor, a.Patch));
        return version != 0 ? version : b.Build.CompareTo(a.Build);
    }

    [GeneratedRegex(@"^(zDEV-)?UE4SS_v(\d+)\.(\d+)\.(\d+)-(\d+)-g([0-9a-f]+)\.zip$",
        RegexOptions.IgnoreCase)]
    private static partial Regex AssetPattern();
}
