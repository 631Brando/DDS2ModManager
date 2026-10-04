using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DDS2ModManager.ViewModels;

/// One game in the game picker: an install the catalog found, plus what the UI needs to show it.
///
/// Identified by <see cref="Key"/> - the install, not the game - so two copies of one game are two
/// entries, and a rescan updates an entry in place (keeping pictures already loaded) instead of
/// rebuilding the list and making every card flicker.
public partial class GameEntryViewModel : ObservableObject
{
    public GameEntryViewModel(DetectedGame game) => _game = game;

    private DetectedGame _game;

    public DetectedGame Game => _game;

    /// Whether this is the install currently being managed.
    [ObservableProperty] private bool isActive;

    /// Steam's 600x900 box art, decoded at card size. Null until loaded, or when Steam has none.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCapsule), nameof(ShowIconFallback), nameof(ShowInitialsFallback))]
    private BitmapSource? capsule;

    /// The wide hero banner, used behind the header for the open game.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHero))]
    private BitmapSource? hero;

    /// The game's own icon, for games Steam has no art for.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowIconFallback), nameof(ShowInitialsFallback))]
    private BitmapSource? icon;

    public GameProfile Profile => _game.Profile;
    public string Key => _game.Key;

    public bool IsInstalled => _game.State == CatalogState.Installed;
    public bool IsLeftover => _game.State == CatalogState.Leftover;
    public bool IsNotFound => _game.State == CatalogState.NotFound;
    public bool IsFullSupport => Profile.IsBuiltIn;

    public string DisplayName => Profile.DisplayName;
    public string ShortName => Profile.ShortName;

    /// The line under the name: what clicking will DO when it isn't a plain switch, so a missing
    /// game reads as an action rather than a dead control.
    public string StateLabel => _game.State switch
    {
        CatalogState.Installed => Profile.IsBuiltIn ? Profile.DisplayName : EngineDisplay,
        CatalogState.Leftover => "Uninstalled - only leftover files remain",
        _ => "Not found - click to locate"
    };

    public string SupportLabel => IsFullSupport ? "Full support" : "Basic support";

    /// "Full support" means the app knows the game - its Nexus catalogue, its loader, its file
    /// names. "Basic" means it reads the game's paks like any other Unreal game, and says no to
    /// everything it would otherwise have to guess.
    public string SupportTooltip => IsFullSupport
        ? $"{Profile.DisplayName} has a profile written for it: Nexus integration, mod loader setup, save handling and everything else."
        : "Any Unreal Engine game: mods install as paks and conflicts are detected by reading every pak. " +
          "Nexus features, installing UE4SS and save cloning are off, because they need knowledge of this particular game.";

    public string EngineDisplay => string.IsNullOrWhiteSpace(Profile.EngineLabel)
        ? ""
        : Profile.EngineIsEstimated ? $"{Profile.EngineLabel} (estimated)" : Profile.EngineLabel;

    public string SourceLabel => IsNotFound
        ? ""
        : _game.AddedByHand ? "Added folder" : _game.Identity.Store == GameStore.None ? "Steam library" : _game.Identity.StoreLabel;

    public string RootPath => _game.RootPath;

    public bool HasAntiCheat => _game.AntiCheat != AntiCheat.None;

    public string AntiCheatLabel => _game.AntiCheat switch
    {
        AntiCheat.EasyAntiCheat => "EasyAntiCheat",
        AntiCheat.BattlEye => "BattlEye",
        AntiCheat.None => "",
        _ => "EasyAntiCheat + BattlEye"
    };

    /// Said on the card itself rather than in a dialog, because the one way this tool can cost
    /// someone their account is modding a protected multiplayer game - and the user is the only one
    /// who knows whether they play it online.
    public string AntiCheatTooltip =>
        $"This game ships {AntiCheatLabel}. Modded files can get an account flagged in online play. " +
        "Single-player is usually fine, but check the game's own rules before playing online with mods.";

    public bool HasCapsule => Capsule != null;
    public bool HasHero => Hero != null;
    public bool ShowIconFallback => Capsule == null && Icon != null;
    public bool ShowInitialsFallback => Capsule == null && Icon == null;

    /// Up to two letters for a game with no art at all: "Drug Dealer Simulator" -> "DD".
    public string Initials
    {
        get
        {
            var words = DisplayName.Split([' ', '-', ':', '_'], StringSplitOptions.RemoveEmptyEntries)
                .Where(w => char.IsLetterOrDigit(w[0]))
                .ToList();
            return words.Count switch
            {
                0 => "?",
                1 => words[0][..Math.Min(2, words[0].Length)].ToUpperInvariant(),
                _ => $"{char.ToUpperInvariant(words[0][0])}{char.ToUpperInvariant(words[1][0])}"
            };
        }
    }

    /// The colour behind a game with no art: derived from its name, so the same game is always the
    /// same colour - a placeholder that changed between launches would read as a bug.
    public Brush PlaceholderBrush
    {
        get
        {
            var c = LocalImageLoader.PlaceholderColor(DisplayName);
            var dark = Color.FromRgb((byte)(c.R * 0.45), (byte)(c.G * 0.45), (byte)(c.B * 0.45));
            var brush = new LinearGradientBrush(c, dark, new System.Windows.Point(0, 0), new System.Windows.Point(1, 1));
            brush.Freeze();
            return brush;
        }
    }

    /// Everything the search box matches against.
    public string SearchText => $"{DisplayName} {_game.ProjectName} {SourceLabel} {EngineDisplay}";

    /// Takes a fresh scan's result for the same install, keeping loaded pictures unless the art
    /// files themselves changed.
    public void Update(DetectedGame fresh)
    {
        var artChanged = fresh.Art != _game.Art;
        _game = fresh;
        if (artChanged)
        {
            Capsule = null;
            Hero = null;
            _artRequested = false;
        }

        OnPropertyChanged(string.Empty);
    }

    private bool _artRequested;

    /// Loads the card's pictures off the UI thread, once. The hero banner is only fetched when
    /// asked for, because it is large and only the open game shows one.
    public async Task LoadArtAsync(bool includeHero)
    {
        if (!_artRequested)
        {
            _artRequested = true;

            // Decoded at twice the card's display width, so it stays sharp at 200% scaling without
            // keeping a 600x900 bitmap in memory per game.
            Capsule = await LocalImageLoader.LoadAsync(_game.Art.Capsule ?? _game.Art.Header, decodeWidth: 320);

            if (Capsule == null && _game.ExecutablePath != null)
                Icon = await LocalImageLoader.LoadExeIconAsync(_game.ExecutablePath, 128);
        }

        if (includeHero && Hero == null)
            Hero = await LocalImageLoader.LoadAsync(_game.Art.Hero ?? _game.Art.HeroBlur ?? _game.Art.Header, decodeWidth: 1920);
    }
}
