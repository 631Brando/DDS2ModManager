using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using DDS2ModManager.ViewModels;

namespace DDS2ModManager.Views;

/// Browse and install mods from a published catalog.
///
/// The catalog is only a list of pointers. Installing from here downloads the release asset and
/// hands it to the ordinary installer, so it gets the same type detection, the same placement
/// rules and the same conflict checking as a mod installed by hand. Being listed grants nothing.
public partial class ModCatalogWindow : Window
{
    private readonly ModCatalogService _catalogService = new();
    private readonly ModUpdateService _downloader = new();
    private readonly GitHubReleaseService _github = new();
    private readonly MainViewModel _mainViewModel;

    private ModCatalog? _catalog;

    public ModCatalogWindow(MainViewModel mainViewModel)
    {
        InitializeComponent();
        _mainViewModel = mainViewModel;
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        SubtitleText.Text = "Loading...";
        ModList.ItemsSource = null;

        _catalog = await _catalogService.LoadAsync();
        Render();
    }

    private void Render()
    {
        if (_catalog == null)
        {
            TitleText.Text = "Browse Mods";
            SubtitleText.Text = "";
            EmptyText.Visibility = Visibility.Visible;
            EmptyText.Text =
                "No mod catalog is available yet.\n\n"
                + "This page lists mods published by the manager's maintainers so they can be installed and kept "
                + "up to date from here. It fills in automatically once the catalog is published - nothing is "
                + "wrong with your install, and you can carry on installing mods normally in the meantime.";
            CountText.Text = "";
            return;
        }

        TitleText.Text = string.IsNullOrWhiteSpace(_catalog.Title) ? "Browse Mods" : _catalog.Title;

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(_catalog.Description)) parts.Add(_catalog.Description!);
        if (!string.IsNullOrWhiteSpace(_catalog.Author)) parts.Add($"by {_catalog.Author}");
        if (!string.IsNullOrWhiteSpace(_catalog.Updated)) parts.Add($"updated {_catalog.Updated}");
        SubtitleText.Text = string.Join("  ·  ", parts);

        // Being explicit about a cached list matters: an out-of-date catalog looks identical to a
        // current one otherwise.
        OfflineBanner.Visibility = _catalogService.LastFetchWasLive ? Visibility.Collapsed : Visibility.Visible;
        OfflineText.Text = "Showing a saved copy of the catalog - it couldn't be refreshed just now, so it may be "
                           + "out of date. Anything you install still comes straight from its own GitHub release.";

        MatchAgainstInstalled();
        ApplyFilter();
    }

    /// Marks catalog entries the user already has, so the page reflects their install rather than
    /// offering everything as though it were new.
    private void MatchAgainstInstalled()
    {
        if (_catalog == null) return;

        foreach (var entry in _catalog.Mods)
        {
            entry.Installed = _mainViewModel.Mods.FirstOrDefault(m =>
                string.Equals(m.Name, entry.Name, StringComparison.OrdinalIgnoreCase)
                || (entry.Id.Length > 0 && string.Equals(m.Name, entry.Id, StringComparison.OrdinalIgnoreCase)));
        }
    }

    private void Filter_Changed(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        if (_catalog == null) return;

        var filter = FilterBox.Text.Trim();
        FilterHint.Visibility = filter.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        var matches = _catalog.Mods.Where(m =>
            filter.Length == 0
            || m.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || (m.Summary?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
            || m.Tags.Any(t => t.Contains(filter, StringComparison.OrdinalIgnoreCase))).ToList();

        ModList.ItemsSource = matches;

        EmptyText.Visibility = matches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (matches.Count == 0)
        {
            // A published-but-empty catalog, a search that found nothing, and no catalog at all
            // are three different situations. Each gets its own wording rather than one vague one.
            EmptyText.Text = _catalog.Mods.Count == 0
                ? "The catalog is published but doesn't list any mods yet. Check back later."
                : $"Nothing matches \"{filter}\".";
        }

        CountText.Text = filter.Length == 0
            ? $"{_catalog.Mods.Count} mod(s)"
            : $"{matches.Count} of {_catalog.Mods.Count} mod(s)";
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CatalogMod entry) return;

        if (!GitHubUrlParser.TryParse(entry.Repo, out var owner, out var repo))
        {
            MessageBox.Show($"'{entry.Name}' doesn't point at a GitHub repository, so it can't be installed from here.",
                "Browse Mods", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (entry.IsInstalled)
        {
            var replace = MessageBox.Show(
                $"'{entry.Name}' is already installed. Reinstall it with the latest release?",
                "Browse Mods", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (replace != MessageBoxResult.Yes) return;
        }

        IsEnabled = false;
        try
        {
            var release = await _github.GetLatestReleaseAsync(owner, repo);
            if (release == null)
            {
                MessageBox.Show($"Couldn't reach the release page for '{entry.Name}'. See the log for details.",
                    "Browse Mods", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var asset = PickAsset(release, entry);
            if (asset == null)
            {
                MessageBox.Show(
                    $"The latest release of '{entry.Name}' ({release.TagName}) doesn't have a single obvious file to " +
                    "download, so it's being left alone rather than guessing. Use View Source to grab it by hand.",
                    "Browse Mods", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var downloaded = await _downloader.DownloadAsync(asset.BrowserDownloadUrl, asset.Name);
            if (downloaded == null)
            {
                MessageBox.Show($"Downloading '{entry.Name}' failed. See the log for details.",
                    "Browse Mods", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Straight into the normal installer - same detection and conflict checks as any other
            // install, and the same prompts if something needs deciding.
            await _mainViewModel.InstallFromPathAsync(downloaded);

            MatchAgainstInstalled();
            ApplyFilter();
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Error($"Installing '{entry.Name}' from the catalog failed: {ex.Message}");
            MessageBox.Show($"Couldn't install '{entry.Name}': {ex.Message}",
                "Browse Mods", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private static readonly string[] InstallableExtensions = { ".zip", ".7z", ".rar", ".pak" };

    private static GitHubAsset? PickAsset(GitHubReleaseInfo release, CatalogMod entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.Asset))
            return release.Assets.FirstOrDefault(a => a.Name.Equals(entry.Asset, StringComparison.OrdinalIgnoreCase));

        var installable = release.Assets
            .Where(a => InstallableExtensions.Contains(Path.GetExtension(a.Name), StringComparer.OrdinalIgnoreCase))
            .ToList();

        return installable.Count == 1 ? installable[0] : null;
    }

    private void ViewSource_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CatalogMod entry) return;
        if (!GitHubUrlParser.TryParse(entry.Repo, out var owner, out var repo)) return;

        try { Process.Start(new ProcessStartInfo($"https://github.com/{owner}/{repo}") { UseShellExecute = true }); }
        catch (Exception ex) { LoggingService.Instance.Warn($"Couldn't open the repository page: {ex.Message}"); }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
