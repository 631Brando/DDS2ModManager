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
        if (!AppSettingsService.Instance.Current.ShowNexusNewModBanner) return;
        if (Game == null) return;

        // Per game - the two games have separate Nexus catalogues, so "what's new since I last
        // looked" is a separate answer for each.
        var settings = AppSettingsService.Instance.ForGame(Game.Profile);

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

        // Named from the game this actually came from. It was hardcoded to "DDS2", so a DDS1 user
        // was told about "new DDS2 mods" on a page listing DDS1's catalogue.
        var game = Game?.Profile.ShortName ?? GameProfiles.Default.ShortName;
        NexusBannerText = fresh.Count == 1
            ? $"1 new {game} mod on Nexus"
            : $"{fresh.Count} new {game} mods on Nexus";

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
    /// The active game's catalogue and its name index, kept for the session so ONE mod can be
    /// resolved without re-reading the cache file - NexusIndexService holds nothing in memory and
    /// re-deserialises on every GetAsync. Domain-tagged, and dropped in ClearPerGameState: this is
    /// per GAME, and the registry's per-install key is not the same question.
    private (string Domain, List<NexusModPost> Catalogue, Dictionary<string, NexusModPost> Index)? _nexusCatalogue;

    private async Task RefreshNexusDetailsAsync(int context, bool force = false)
    {
        if (!AppSettingsService.Instance.Current.ShowNexusModDetails) return;

        try
        {
            // Captured BEFORE the await. Everything below is about this domain, and Game can change
            // while a cold-cache fetch pages through the whole catalogue.
            var domain = NexusGameDomain;

            var catalogue = await _nexusIndex.GetAsync(domain, force);
            if (catalogue.Count == 0) return;

            // RunForGameAsync only LOGS a stale result; it does not discard one. The check has to
            // happen here, before anything is written - Mods is a single collection that a game
            // switch refills in place, and the settings write below is against the CURRENT game.
            // With id-based lookup this stops being unlikely: mod 79 exists on both domains and is
            // a different mod on each.
            if (IsStaleGameContext(context)) return;

            // Built once for the whole list rather than per mod: the uniqueness guard that makes
            // matching safe needs to see every mod at once to know which keys are ambiguous.
            var index = NexusModMatcher.BuildIndex(catalogue);
            _nexusCatalogue = (domain, catalogue, index);

            var matched = 0;
            var linked = 0;
            foreach (var mod in Mods)
            {
                // Assigned unconditionally, INCLUDING null. That is what makes unlinking and
                // re-pointing take effect - nothing else in this codebase ever sets NexusInfo back.
                mod.NexusInfo = NexusModMatcher.Resolve(mod.Name, mod.NexusLink, catalogue, index, domain);

                if (mod.HasExplicitNexusLink) linked++;
                else if (mod.NexusInfo != null) matched++;
            }

            if (Game != null)
            {
                AppSettingsService.Instance.ForGame(Game.Profile).NexusIndexRefreshedUtc = DateTime.UtcNow;
                AppSettingsService.Instance.Save();
            }

            LoggingService.Instance.Info(
                $"Matched {matched} of {Mods.Count} installed mod(s) to their Nexus page by name" +
                (linked > 0 ? $", plus {linked} you linked yourself. " : ". ") +
                "Hover a mod to see its picture and description.");

            // One aggregate line, and only what is known. A link the catalogue does not carry is
            // normal for a mod published inside the 3-day refresh window - the link still opens.
            var pending = Mods.Where(m => m.HasExplicitNexusLink && m.NexusInfo == null).ToList();
            if (pending.Count > 0)
            {
                LoggingService.Instance.Info(
                    $"{pending.Count} linked mod(s) aren't in the cached Nexus list yet (" +
                    string.Join(", ", pending.Take(3).Select(m => $"{m.Name} -> mod {m.NexusLink!.ModId}")) +
                    "). Their links still open; the cards fill in after a later refresh.");
            }
        }
        catch (Exception ex)
        {
            // Decoration. Never an error.
            LoggingService.Instance.Warn($"Couldn't load Nexus mod details: {ex.Message}");
        }
    }

    /// Resolves ONE mod, for a link the user just set or a mod just installed.
    ///
    /// RefreshNexusDetailsAsync runs once per game load, so without this a link takes effect only
    /// after a restart - which reads as the feature not working. Synchronous and network-free by
    /// construction: it reads the catalogue already held in memory, so it is safe to call from a
    /// dialog's OK button and from an install loop.
    private void ResolveNexusFor(ModInfo mod)
    {
        if (_nexusCatalogue is not { } c) return;
        if (!string.Equals(c.Domain, NexusGameDomain, StringComparison.OrdinalIgnoreCase)) return;

        mod.NexusInfo = NexusModMatcher.Resolve(mod.Name, mod.NexusLink, c.Catalogue, c.Index, c.Domain);
    }

    /// Asks which Nexus page a mod is, and records the answer.
    ///
    /// Single mod only. One Nexus id applied across a multi-selection is almost always wrong, and a
    /// wrong card is the thing this whole area refuses to risk.
    [RelayCommand]
    private void LinkNexusPage(ModInfo? mod)
    {
        if (mod == null) return;

        if (_nexusCatalogue is not { } c ||
            !string.Equals(c.Domain, NexusGameDomain, StringComparison.OrdinalIgnoreCase))
        {
            StatusMessage = "The Nexus mod list hasn't loaded yet - try again in a moment.";
            return;
        }

        var dlg = new Views.LinkNexusModWindow(mod, c.Catalogue, c.Domain)
        {
            Owner = System.Windows.Application.Current.MainWindow
        };

        if (dlg.ShowDialog() != true || !dlg.Committed) return;

        // The property change persists it: NexusLink is on OnModAnnotationChanged's list, so this
        // one assignment both saves and re-renders. Nothing calls Upsert here - two writers of one
        // fact is how they get out of step.
        mod.NexusLink = dlg.Result;

        // Immediate, and network-free.
        ResolveNexusFor(mod);
    }

    /// Marks everything currently shown as seen, so the banner does not return for the same mods.
    private void DismissNexusFeed()
    {
        if (NexusNewMods.Count > 0 && Game != null)
        {
            var newest = NexusNewMods.Max(p => p.CreatedAt).ToUniversalTime();
            AppSettingsService.Instance.ForGame(Game.Profile).NexusFeedLastSeenUtc = newest;
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
