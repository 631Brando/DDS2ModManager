using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using DDS2ModManager.ViewModels;

namespace DDS2ModManager.Views;

/// One row on the Brando's Mods page: a mod from Nexus, plus whether it's already installed.
///
/// Observable because the thumbnail arrives after the row does: the list appears straight away and
/// each picture fills in as it's fetched.
public partial class CatalogRow : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public NexusModPost Post { get; init; } = new();

    /// Filled in once the picture has been fetched and decoded off the UI thread.
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private System.Windows.Media.Imaging.BitmapImage? thumbnail;

    /// The matching installed mod, if the user already has it.
    public ModInfo? Installed { get; set; }

    /// True for the manager's own Nexus page. It belongs in the list - it's one of the author's
    /// published mods, and people endorse it there - but it is not something to install as a mod,
    /// and a row that looks identical to the others invites exactly that mistake.
    public bool IsThisApp { get; init; }

    public bool IsInstalled => Installed != null && !IsThisApp;

    public string VersionDisplay => string.IsNullOrWhiteSpace(Post.Version) ? "" : $"v{Post.Version}";

    public string UpdatedDisplay => Post.UpdatedAt == default
        ? ""
        : $"updated {Post.UpdatedAt:d MMM yyyy}";
}

/// Everything one author has published, listed from Nexus.
///
/// The list comes from the Nexus index the app already keeps for hover cards - the same public
/// GraphQL API, the same three-day cache - filtered to one uploader. No page scraping: the profile
/// page is HTML built for a browser, so reading it would break the first time Nexus restyled
/// anything, while the API already returns names, versions, pictures and counts as data.
///
/// Deliberately links out rather than installing. Nexus doesn't hand download links to automated
/// clients (its API only does so for premium members), which is the same constraint that made mod
/// updating use each mod's own GitHub releases instead. Offering an Install button here would mean
/// promising something that cannot work, so the page shows what exists and opens the page.
public partial class ModCatalogWindow : Window
{
    /// The Nexus account whose mods this page lists.
    private const string Uploader = "brando136";
    private const string GameDomain = "drugdealersimulator2";

    /// This application's own Nexus page. Matched on the mod id rather than the name: an id never
    /// changes, whereas a title can be reworded at any time and would silently stop matching.
    private const int ThisAppModId = 118;

    private readonly NexusIndexService _index = new();
    private readonly MainViewModel _mainViewModel;

    private List<CatalogRow> _rows = new();

    public ModCatalogWindow(MainViewModel mainViewModel)
    {
        InitializeComponent();
        _mainViewModel = mainViewModel;
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync(bool forceRefresh = false)
    {
        SubtitleText.Text = "Loading from Nexus...";
        ModList.ItemsSource = null;
        EmptyText.Visibility = Visibility.Collapsed;

        List<NexusModPost> all;
        try
        {
            all = await _index.GetAsync(GameDomain, forceRefresh);
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Warn($"Couldn't load the Nexus mod index: {ex.Message}");
            all = new List<NexusModPost>();
        }

        _rows = all
            .Where(m => string.Equals(m.Uploader, Uploader, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(m => m.UpdatedAt)
            .Select(m => new CatalogRow { Post = m, IsThisApp = m.ModId == ThisAppModId })
            .ToList();

        MatchAgainstInstalled();
        Render();

        // After the list is on screen, not before: the rows appear immediately and gain their
        // pictures as they arrive, rather than the window sitting blank through nine downloads.
        _ = LoadThumbnailsAsync();
    }

    /// Fetches each row's picture through the same cache the hover cards use, so a picture is
    /// downloaded once per machine no matter which feature asks for it first.
    ///
    /// Sequential on purpose. Nine small images is not worth nine simultaneous connections to
    /// Nexus, and each one lands the moment it's ready either way.
    private async Task LoadThumbnailsAsync()
    {
        foreach (var row in _rows.ToList())
        {
            if (row.Thumbnail != null) continue;

            var url = row.Post.CardImageUrl;
            if (string.IsNullOrWhiteSpace(url)) continue;

            try
            {
                row.Thumbnail = await NexusImageCache.Instance.GetAsync(row.Post.ModId, url);
            }
            catch
            {
                // The cache logs its own failures; a missing picture is not worth a second line,
                // and the row is perfectly usable without one.
            }
        }
    }

    private void Render()
    {
        SubtitleText.Text = $"Published by {Uploader} on Nexus Mods. Opens each mod's page - nothing is downloaded from here.";

        // An empty list after a successful fetch means Nexus returned nothing for this uploader,
        // which is a different situation from the fetch having failed. Say which.
        NoticeBanner.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoticeText.Text = "Couldn't reach Nexus, and there's no saved copy of the mod list yet. "
                          + "Check your connection and press Refresh.";

        ApplyFilter();
    }

    /// Reuses the same matcher the mod grid uses for its hover cards, so a mod counts as installed
    /// here on exactly the same basis it does there - rather than by a second, subtly different rule.
    private void MatchAgainstInstalled()
    {
        var installed = _mainViewModel.Mods.ToList();
        if (installed.Count == 0) return;

        var index = NexusModMatcher.BuildIndex(_rows.Select(r => r.Post));

        foreach (var mod in installed)
        {
            var match = NexusModMatcher.Match(mod.Name, index);
            if (match == null) continue;

            var row = _rows.FirstOrDefault(r => r.Post.ModId == match.ModId);
            if (row != null) row.Installed = mod;
        }
    }

    private void Filter_Changed(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        var filter = FilterBox.Text.Trim();
        FilterHint.Visibility = filter.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        var matches = _rows.Where(r =>
            filter.Length == 0
            || r.Post.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || r.Post.Summary.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        ModList.ItemsSource = matches;

        EmptyText.Visibility = matches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (matches.Count == 0)
        {
            EmptyText.Text = _rows.Count == 0
                ? "No mods to show yet."
                : $"Nothing matches \"{filter}\".";
        }

        CountText.Text = filter.Length == 0
            ? $"{_rows.Count} mod(s)  ·  {_rows.Count(r => r.IsInstalled)} installed"
            : $"{matches.Count} of {_rows.Count} mod(s)";
    }

    private void OpenOnNexus_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CatalogRow row) return;
        OpenUrl(row.Post.Url);
    }

    private void OpenProfile_Click(object sender, RoutedEventArgs e) =>
        OpenUrl($"https://www.nexusmods.com/profile/{Uploader}/mods");

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { LoggingService.Instance.Warn($"Couldn't open '{url}': {ex.Message}"); }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync(forceRefresh: true);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
