using System.Collections.Specialized;
using System.ComponentModel;
using System.Reflection;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CUE4Parse.UE4.Versions;
using DDS2ModManager.Views;

namespace DDS2ModManager.ViewModels;

/// Nexus discovery: the "what's new" banner, and the per-mod detail used by the hover card.
///
/// Part of MainViewModel, split across files rather than extracted into separate classes. The
/// logic here is view-model glue - command wiring, status text, dialog plumbing - which gains
/// nothing from indirection. What it does gain is that two people can add features in different
/// areas without landing in the same 2,000-line file: this class produced three of the nine
/// conflicts the last merge had to resolve by hand.
public partial class MainViewModel
{
    // ---- Nexus "what's new" banner ---------------------------------------------------------
    //
    // Discovery only: it says a mod EXISTS and links to its page. Nothing is downloaded, and it
    // needs no Nexus account - see NexusFeedService for why that is possible.

    public ObservableCollection<NexusModPost> NexusNewMods { get; } = new();

    [ObservableProperty] private bool hasNexusNewMods;
    [ObservableProperty] private string nexusBannerText = "";

    /// Fire-and-forget from startup. A slow or down Nexus must never delay the window.
    private async Task CheckNexusFeedAsync()
    {
        var settings = AppSettingsService.Instance.Current;
        if (!settings.ShowNexusNewModBanner) return;

        // First run starts two weeks back. Without a floor the first launch would list every
        // mod ever published for the game, which is a catalogue, not news.
        var since = settings.NexusFeedLastSeenUtc ?? DateTime.UtcNow.AddDays(-14);

        var posts = await _nexus.GetNewModsAsync(NexusGameDomain, since);
        if (posts.Count == 0) return;

        // Anything the user already has installed is not news to them. Matched on name because
        // that is all the two sides share - the registry has no Nexus id.
        var installed = Mods.Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fresh = posts.Where(p => !installed.Contains(p.Name)).ToList();
        if (fresh.Count == 0) return;

        NexusNewMods.Clear();
        foreach (var p in fresh.Take(6)) NexusNewMods.Add(p);

        HasNexusNewMods = true;
        NexusBannerText = fresh.Count == 1
            ? "1 new DDS2 mod on Nexus"
            : $"{fresh.Count} new DDS2 mods on Nexus";

        LoggingService.Instance.Info(
            $"{fresh.Count} new mod(s) published on Nexus since {since.ToLocalTime():d MMM}: " +
            string.Join(", ", fresh.Take(5).Select(p => p.Name)));
    }

    // ---- Nexus mod details (the hover card) -------------------------------------------------
    //
    // The whole catalogue is cached locally, so hovering a row does NO network work - it is a
    // dictionary lookup against something already on disk, and it keeps working offline.

    /// Attaches each installed mod to its Nexus entry, if one can be identified.
    ///
    /// Fire-and-forget from startup, and deliberately quiet: a mod with no card is the normal
    /// case (roughly half of a typical install is unpublished local work), so nothing here
    /// reports a failure to find one.
    private async Task RefreshNexusDetailsAsync(bool force = false)
    {
        if (!AppSettingsService.Instance.Current.ShowNexusModDetails) return;

        try
        {
            var catalogue = await _nexusIndex.GetAsync(NexusGameDomain, force);
            if (catalogue.Count == 0) return;

            // Built once for the whole list rather than per mod: the uniqueness guard that makes
            // matching safe needs to see every mod at once to know which keys are ambiguous.
            var index = NexusModMatcher.BuildIndex(catalogue);

            var matched = 0;
            foreach (var mod in Mods)
            {
                var hit = NexusModMatcher.Match(mod.Name, index);
                if (hit == null) continue;

                mod.NexusInfo = hit;
                matched++;
            }

            AppSettingsService.Instance.Current.NexusIndexRefreshedUtc = DateTime.UtcNow;
            AppSettingsService.Instance.Save();

            LoggingService.Instance.Info(
                $"Matched {matched} of {Mods.Count} installed mod(s) to their Nexus page. " +
                "Hover a mod to see its picture and description.");
        }
        catch (Exception ex)
        {
            // Decoration. Never an error.
            LoggingService.Instance.Warn($"Couldn't load Nexus mod details: {ex.Message}");
        }
    }

    /// Marks everything currently shown as seen, so the banner does not return for the same mods.
    private void DismissNexusFeed()
    {
        if (NexusNewMods.Count > 0)
        {
            var newest = NexusNewMods.Max(p => p.CreatedAt).ToUniversalTime();
            AppSettingsService.Instance.Current.NexusFeedLastSeenUtc = newest;
            AppSettingsService.Instance.Save();
        }

        NexusNewMods.Clear();
        HasNexusNewMods = false;
        NexusBannerText = "";
    }

    private void OpenNexusMod(NexusModPost? post)
    {
        if (post == null) return;
        OpenUrl(post.Url);
    }

    /// Opens the repository a mod publishes its updates from.
    ///
    /// Re-checked against the allowlist rather than trusted because it came from a ModInfo:
    /// the URL originates in a file inside a mod, and "we validated it on the way in" is a
    /// weaker guarantee than validating it at the point of use - registries get hand-edited.
    private void OpenModSource(ModInfo? mod)
    {
        if (mod == null) return;

        if (!GitHubUrlParser.TryParse(mod.ModUpdateUrl, out var owner, out var repo))
        {
            LoggingService.Instance.Warn(
                $"'{mod.Name}' has an update address that isn't a GitHub repository, so it wasn't opened: {mod.ModUpdateUrl}");
            return;
        }

        // Opens the parsed owner/repo rather than the raw string. Re-parsing at the point of use
        // is the point: the URL originates inside a mod file, and registries get hand-edited.
        OpenUrl($"https://github.com/{owner}/{repo}");
    }

    private static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { LoggingService.Instance.Warn($"Couldn't open {url}: {ex.Message}"); }
    }

    private void RefreshModUpdateBanner()
    {
        ModUpdatesAvailable = Mods.Count(m => m.UpdateAvailable);
        HasModUpdates = ModUpdatesAvailable > 0;
        ModUpdateBannerText = ModUpdatesAvailable switch
        {
            0 => "",
            1 => $"1 mod has an update available: {Mods.First(m => m.UpdateAvailable).Name}",
            _ => $"{ModUpdatesAvailable} mods have updates available"
        };
    }
}
