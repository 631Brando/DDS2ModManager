using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace DDS2ModManager.Views;

/// Asks which Nexus page a mod is, when its name can't reach one.
///
/// The reason this exists at all: NexusModMatcher does exact name equality, and "AERR" is published
/// as "AE Revolutions Reloaded". No normalisation gets from one to the other, so the only honest
/// answer is to ask. A declared id is not a guess, which is why it is allowed to win outright.
///
/// Works entirely from the catalogue the caller already holds in memory. It must NOT call
/// NexusIndexService.GetAsync: past the 3-day refresh interval that pages the whole catalogue
/// sequentially at a 30-second per-request timeout, behind a modal the user just opened.
public partial class LinkNexusModWindow : Window
{
    /// One catalogue entry as the list shows it. The thumbnail arrives later, per row.
    public class Row : INotifyPropertyChanged
    {
        public required NexusModPost Post { get; init; }

        public string ByLine =>
            string.IsNullOrWhiteSpace(Post.Uploader) ? $"mod {Post.ModId}" : $"by {Post.Uploader}  ·  mod {Post.ModId}";

        private BitmapImage? _thumbnail;
        public BitmapImage? Thumbnail
        {
            get => _thumbnail;
            set { _thumbnail = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail))); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// How many pictures to fetch for the visible filter.
    ///
    /// Bounded deliberately. The equivalent loop in TrustedModsWindow walks every row, which is
    /// sized for a curated subset; over an unfiltered 183-mod catalogue that is 183 sequential HTTP
    /// fetches behind a virtualizing list showing six of them.
    private const int ThumbnailBudget = 30;

    private readonly IReadOnlyList<NexusModPost> _catalogue;
    private readonly string _domain;
    private readonly ObservableCollection<Row> _rows = new();

    private CancellationTokenSource? _thumbnails;

    /// True when the user committed. Result null then means "unlink" - matching resumes.
    public bool Committed { get; private set; }
    public NexusModLink? Result { get; private set; }

    public LinkNexusModWindow(ModInfo mod, IReadOnlyList<NexusModPost> catalogue, string domain)
    {
        _catalogue = catalogue;
        _domain = domain;

        InitializeComponent();

        HeaderText.Text = $"Link '{mod.Name}' to its Nexus page";
        UnlinkButton.Visibility = mod.NexusLink != null ? Visibility.Visible : Visibility.Collapsed;

        ModList.ItemsSource = _rows;
        ApplyFilter("");

        // A mod already linked opens with its own address in the box, so a mistyped digit can be
        // corrected by editing rather than retyped from scratch.
        if (mod.NexusLink is { IsUsable: true } existing) UrlBox.Text = existing.Url;

        Loaded += (_, _) => UrlBox.Focus();
        Closed += (_, _) => _thumbnails?.Cancel();
    }

    // ---- the paste route --------------------------------------------------------------------

    /// The three outcomes are all shown BEFORE the user commits, because a mistyped digit resolves
    /// to a real page belonging to somebody else, and that is cheapest to catch here.
    private void UrlBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var text = UrlBox.Text?.Trim() ?? "";

        if (text.Length == 0)
        {
            UrlFeedback.Visibility = Visibility.Collapsed;
            UseUrlButton.IsEnabled = false;
            return;
        }

        UrlFeedback.Visibility = Visibility.Visible;

        if (!NexusUrlParser.TryParse(text, _domain, out var domain, out var modId))
        {
            Say("That isn't a Nexus mod address. It should look like " +
                $"https://www.nexusmods.com/{_domain}/mods/123 - or just type the mod number.", warn: true);
            UseUrlButton.IsEnabled = false;
            return;
        }

        // Refused, not silently corrected. Mod 79 is "AE Revolutions Reloaded" here and
        // "Gh0sted - Rebalance" on the other game's domain; 85 ids collide across the two.
        if (!string.Equals(domain, _domain, StringComparison.OrdinalIgnoreCase))
        {
            var other = GameProfiles.All.FirstOrDefault(p =>
                string.Equals(p.NexusDomain, domain, StringComparison.OrdinalIgnoreCase));

            var whose = other != null
                ? $"{other.DisplayName} ({domain})"
                : $"a different game on Nexus ({domain})";

            Say($"That address is for {whose}. This mod is installed under " +
                $"{GameProfiles.All.First(p => p.NexusDomain == _domain).ShortName}, so it can't be that page.",
                warn: true);
            UseUrlButton.IsEnabled = false;
            return;
        }

        var post = _catalogue.FirstOrDefault(p => p.ModId == modId);

        if (post == null)
        {
            // Allowed. The catalogue is up to 3 days stale, so a mod published yesterday is
            // legitimately absent - and that is exactly the case most in need of a manual link.
            Say($"Mod {modId} isn't in the cached Nexus list yet. The link will still open its " +
                "page; the card fills in after the next refresh.", warn: false);
        }
        else
        {
            Say($"{post.Name}  ·  by {post.Uploader}  ·  {post.Downloads:N0} downloads", warn: false);
        }

        UseUrlButton.IsEnabled = true;
    }

    private void Say(string text, bool warn)
    {
        UrlFeedback.Text = text;
        UrlFeedback.Foreground = warn
            ? (System.Windows.Media.Brush)FindResource("WarningBrush")
            : (System.Windows.Media.Brush)FindResource("TextMutedBrush");
    }

    private void UrlBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && UseUrlButton.IsEnabled) UseUrl_Click(sender, e);
    }

    private void UseUrl_Click(object sender, RoutedEventArgs e)
    {
        if (!NexusUrlParser.TryParse(UrlBox.Text, _domain, out var domain, out var modId)) return;
        if (!string.Equals(domain, _domain, StringComparison.OrdinalIgnoreCase)) return;

        Commit(new NexusModLink { ModId = modId, GameDomain = domain, Kind = NexusLinkKind.Linked });
    }

    // ---- the search route -------------------------------------------------------------------

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter(FilterBox.Text);

    private void ApplyFilter(string? term)
    {
        term = (term ?? "").Trim();

        var matches = _catalogue
            .Where(p => term.Length == 0
                        || Has(p.Name, term) || Has(p.Summary, term) || Has(p.Uploader, term)
                        || p.ModId.ToString() == term)
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _rows.Clear();
        foreach (var p in matches) _rows.Add(new Row { Post = p });

        if (_catalogue.Count == 0)
        {
            EmptyText.Text = "The Nexus mod list hasn't loaded. Paste the mod's address above instead.";
            EmptyText.Visibility = Visibility.Visible;
        }
        else if (matches.Count == 0)
        {
            EmptyText.Text = $"Nothing matches \"{term}\". Try fewer words, or paste the address above.";
            EmptyText.Visibility = Visibility.Visible;
        }
        else
        {
            EmptyText.Visibility = Visibility.Collapsed;
        }

        _ = LoadThumbnailsAsync();

        static bool Has(string? haystack, string needle) =>
            !string.IsNullOrEmpty(haystack) && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    /// Only the current filter's first rows, and cancelled whenever the filter changes.
    private async Task LoadThumbnailsAsync()
    {
        _thumbnails?.Cancel();
        var cts = _thumbnails = new CancellationTokenSource();

        foreach (var row in _rows.Take(ThumbnailBudget).ToList())
        {
            if (cts.IsCancellationRequested) return;
            if (row.Post.CardImageUrl == null) continue;

            var image = await NexusImageCache.Instance
                .GetAsync(row.Post.ModId, row.Post.GameDomain, row.Post.CardImageUrl, cts.Token);

            if (cts.IsCancellationRequested) return;
            if (image != null) row.Thumbnail = image;
        }
    }

    private void ModList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        LinkButton.IsEnabled = ModList.SelectedItem is Row;

    private void ModList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ModList.SelectedItem is Row) Link_Click(sender, e);
    }

    private void Link_Click(object sender, RoutedEventArgs e)
    {
        if (ModList.SelectedItem is not Row row) return;

        Commit(new NexusModLink
        {
            ModId = row.Post.ModId,
            // The post's own domain, not the active one. They agree here, and taking it from the
            // post keeps that true if this dialog is ever handed a catalogue from elsewhere.
            GameDomain = string.IsNullOrEmpty(row.Post.GameDomain) ? _domain : row.Post.GameDomain,
            Kind = NexusLinkKind.Linked
        });
    }

    // ---- the two answers that are not a page ------------------------------------------------

    private void NoPage_Click(object sender, RoutedEventArgs e) =>
        Commit(new NexusModLink { Kind = NexusLinkKind.NoPage, GameDomain = _domain });

    /// Null is a real answer here, not a cancel: it clears the stored link so name matching resumes.
    private void Unlink_Click(object sender, RoutedEventArgs e) => Commit(null);

    private void Commit(NexusModLink? link)
    {
        Result = link;
        Committed = true;
        DialogResult = true;
        Close();
    }
}
