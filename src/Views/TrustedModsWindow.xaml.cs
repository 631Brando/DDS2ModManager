using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using DDS2ModManager.ViewModels;

namespace DDS2ModManager.Views;

/// One row on the Trusted Mods page: a mod from Nexus, plus whether it's already installed.
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

    /// Why this author is on the list, shown when hovering their name. A curated list is only
    /// useful if a name you don't recognise comes with a reason for being there.
    public string? AuthorNote { get; init; }

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

/// Every DDS2 mod published by an author on the curated trusted list.
///
/// The mods come from the Nexus index the app already keeps for hover cards - the same public
/// GraphQL API, the same three-day cache - filtered to those authors. No page scraping: a profile
/// page is HTML built for a browser, so reading it would break the first time Nexus restyled
/// anything, while the API already returns names, versions, pictures and counts as data.
///
/// The author list is fetched separately (TrustedNexusAuthorService) rather than compiled in, so
/// adding someone reaches everybody without a new build.
///
/// "Trusted" here means a recommendation and nothing more, which is why this page cannot install
/// anything. That isn't only a design choice - Nexus doesn't hand download links to automated
/// clients, its API only does so for premium members - but it's the right shape regardless: a
/// curated browsing list is a poor thing to hang an installer off, because nobody has read every
/// file these authors will publish in future. The page shows what exists and opens the page.
public partial class TrustedModsWindow : Window
{
    private const string GameDomain = "drugdealersimulator2";

    /// This application's own Nexus page. Matched on the mod id rather than the name: an id never
    /// changes, whereas a title can be reworded at any time and would silently stop matching.
    private const int ThisAppModId = 118;

    private const string AllAuthors = "All authors";

    private readonly NexusIndexService _index = new();
    private readonly MainViewModel _mainViewModel;

    private List<CatalogRow> _rows = new();
    private TrustedNexusAuthorList _authors = TrustedNexusAuthorList.Default;

    public TrustedModsWindow(MainViewModel mainViewModel)
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

        _authors = await TrustedNexusAuthorService.Instance.GetAsync(forceRefresh);

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
            .Where(m => _authors.Contains(m.Uploader))
            .OrderByDescending(m => m.UpdatedAt)
            .Select(m => new CatalogRow
            {
                Post = m,
                IsThisApp = m.ModId == ThisAppModId,
                AuthorNote = _authors.Find(m.Uploader)?.Note
            })
            .ToList();

        MatchAgainstInstalled();
        PopulateAuthorFilter();
        Render();

        // After the list is on screen, not before: the rows appear immediately and gain their
        // pictures as they arrive, rather than the window sitting blank through every download.
        _ = LoadThumbnailsAsync();
    }

    /// Only authors who actually have mods in the index get an entry, so the dropdown never offers
    /// a filter that can only produce an empty list.
    private void PopulateAuthorFilter()
    {
        var present = _rows
            .Select(r => r.Post.Uploader)
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(u => u, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var previous = AuthorBox.SelectedItem as string;

        AuthorBox.SelectionChanged -= Author_Changed;
        AuthorBox.ItemsSource = new[] { AllAuthors }.Concat(present).ToList();
        AuthorBox.SelectedItem = previous != null && (previous == AllAuthors || present.Contains(previous, StringComparer.OrdinalIgnoreCase))
            ? previous
            : AllAuthors;
        AuthorBox.SelectionChanged += Author_Changed;
    }

    /// Fetches each row's picture through the same cache the hover cards use, so a picture is
    /// downloaded once per machine no matter which feature asks for it first.
    ///
    /// Sequential on purpose. These are small images, and firing off one connection per mod at
    /// Nexus to save a second of loading is not a trade worth making.
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
        var names = _authors.Authors.Select(a => a.Name).ToList();
        var who = names.Count switch
        {
            0 => "the trusted authors",
            1 => names[0],
            _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1]
        };

        // Says what the list is and, just as importantly, what it isn't. "Trusted" on a page of
        // downloadable-looking things reads as a safety claim unless it's spelled out otherwise.
        SubtitleText.Text = $"DDS2 mods published by {who}. Authors the maintainers rate and think are worth "
                            + "finding - not a check of any individual file. Opens each mod's page; nothing is "
                            + "downloaded from here.";

        // An empty list after a successful fetch means Nexus returned nothing for these authors,
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

    private void Author_Changed(object sender, SelectionChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        if (ModList == null) return;

        var filter = FilterBox.Text.Trim();
        FilterHint.Visibility = filter.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        var author = AuthorBox.SelectedItem as string;
        var byAuthor = author == null || author == AllAuthors
            ? _rows
            : _rows.Where(r => string.Equals(r.Post.Uploader, author, StringComparison.OrdinalIgnoreCase)).ToList();

        var matches = byAuthor.Where(r =>
            filter.Length == 0
            || r.Post.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || r.Post.Summary.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || r.Post.Uploader.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        ModList.ItemsSource = matches;

        EmptyText.Visibility = matches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (matches.Count == 0)
        {
            EmptyText.Text = _rows.Count == 0
                ? "No mods to show yet."
                : filter.Length > 0
                    ? $"Nothing matches \"{filter}\"."
                    : $"{author} hasn't published anything for this game yet.";
        }

        var narrowed = matches.Count != _rows.Count;
        CountText.Text = narrowed
            ? $"{matches.Count} of {_rows.Count} mod(s)"
            : $"{_rows.Count} mod(s)  ·  {_rows.Count(r => r.IsInstalled)} installed";
    }

    private void OpenOnNexus_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CatalogRow row) return;
        OpenUrl(row.Post.Url);
    }

    private void OpenProfile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CatalogRow row) return;
        if (string.IsNullOrWhiteSpace(row.Post.Uploader)) return;
        OpenUrl($"https://www.nexusmods.com/profile/{row.Post.Uploader}/mods");
    }

    /// Everything published for the game, not just these authors. A curated list that offered no
    /// way past itself would quietly imply that nothing outside it is worth looking at.
    private void OpenGame_Click(object sender, RoutedEventArgs e) =>
        OpenUrl($"https://www.nexusmods.com/{GameDomain}/mods/?sort=lastcreated");

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { LoggingService.Instance.Warn($"Couldn't open '{url}': {ex.Message}"); }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync(forceRefresh: true);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
