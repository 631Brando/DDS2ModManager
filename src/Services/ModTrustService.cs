using System.Net.Http;

namespace DDS2ModManager.Services;

/// How much a mod's update source is trusted, and by whom.
public enum ModTrustLevel
{
    /// Nothing known. Updates are still offered, but the user is told the source is unrecognised.
    Unknown,

    /// The user has trusted this update address themselves.
    TrustedByUser,

    /// The account is on the maintainers' curated list.
    Verified
}

/// Tracks which update addresses the user trusts, and which the maintainers have checked.
///
/// Everything here is keyed on a GitHub ACCOUNT, and that is all it can ever speak for. A mod
/// declares its own update address and nothing is signed, so a mod naming an account is not
/// evidence it came from that account. What trust buys is knowledge of where a download will be
/// fetched from - which is genuinely useful, and is what the wording throughout says.
///
/// Two separate ideas that are easy to conflate:
///
///   Trusted    a local, per-user decision. "I recognise this address, stop flagging it." Stored
///              on this machine only.
///   Verified   a curated judgement from the maintainers, fetched from a repository they control.
///              Shared by everyone, and nothing the user can grant themselves.
///
/// Neither one means an update installs itself. The manager always asks first - trust decides how
/// much explaining the prompt has to do, not whether the user gets one. An account can be
/// compromised, a curated list can go stale, and a mod can name an address it has nothing to do
/// with; making trust silent would turn any of those into code running on someone's machine
/// without them ever seeing it.
public class ModTrustService
{
    private static readonly Lazy<ModTrustService> _instance = new(() => new ModTrustService());
    public static ModTrustService Instance => _instance.Value;

    /// Where the curated list is published. A raw file rather than the API: no rate limit, and it
    /// stays readable by anyone who wants to check what they're being asked to trust.
    private const string VerifiedListUrl =
        "https://raw.githubusercontent.com/631Brando/DDS2ModManager/main/verified-mods.json";

    private readonly string _trustFilePath;
    private readonly string _verifiedCachePath;

    private HashSet<string> _trustedOwners = new(StringComparer.OrdinalIgnoreCase);
    private VerifiedList _verified = new();

    private ModTrustService()
    {
        var dir = AppPaths.EnsureRoot();
        _trustFilePath = Path.Combine(dir, "trusted-authors.json");
        _verifiedCachePath = Path.Combine(dir, "verified-mods.cache.json");

        LoadTrusted();
        LoadCachedVerified();
    }

    public IReadOnlyCollection<string> TrustedOwners => _trustedOwners;
    public VerifiedList Verified => _verified;

    /// Raised when an owner is trusted or untrusted.
    ///
    /// Needed because trust is per ACCOUNT while the tick that grants it sits on one mod's row.
    /// Ticking one of an author's mods changes the answer for every other mod by that author, and
    /// without this those rows would go on showing the old state until the next full refresh.
    public event Action? TrustChanged;

    public ModTrustLevel LevelFor(ModUpdateSource source)
    {
        if (!source.IsUsable) return ModTrustLevel.Unknown;
        if (_verified.IsVerified(source.Owner, source.Repo)) return ModTrustLevel.Verified;
        return _trustedOwners.Contains(source.Owner) ? ModTrustLevel.TrustedByUser : ModTrustLevel.Unknown;
    }

    /// Trust is granted per GitHub owner, not per mod. That matches how the risk actually works:
    /// whoever controls the account controls every release under it, so trusting one of their mods
    /// and not another would be a distinction without a difference.
    public void Trust(string owner)
    {
        if (string.IsNullOrWhiteSpace(owner)) return;
        if (!_trustedOwners.Add(owner)) return;

        SaveTrusted();
        LoggingService.Instance.Info(
            $"Trusting updates from '{owner}', including their other mods. You'll still be asked before anything installs.");
        TrustChanged?.Invoke();
    }

    public void Untrust(string owner)
    {
        if (!_trustedOwners.Remove(owner)) return;

        SaveTrusted();
        LoggingService.Instance.Info($"No longer trusting updates from '{owner}'.");
        TrustChanged?.Invoke();
    }

    public bool IsTrusted(string owner) => _trustedOwners.Contains(owner);

    /// Refreshes the curated list. Failure is not an error worth interrupting anyone over - the
    /// cached copy stays in use, and an absent list just means nothing shows as verified.
    public async Task RefreshVerifiedListAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("DDS2ModManager");

            var json = await http.GetStringAsync(VerifiedListUrl);
            var list = JsonSerializer.Deserialize<VerifiedList>(json);
            if (list == null) return;

            if (list.Schema > VerifiedList.SupportedSchema)
            {
                LoggingService.Instance.Warn(
                    $"The verified mod list is written for a newer version of this manager (schema {list.Schema}). " +
                    "Keeping the previous copy rather than misreading it.");
                return;
            }

            _verified = list;
            File.WriteAllText(_verifiedCachePath, json);
            LoggingService.Instance.Info($"Verified mod list updated ({list.Entries.Count} entries).");
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Info($"Couldn't refresh the verified mod list ({ex.Message}) - using the cached copy.");
        }
    }

    private void LoadTrusted()
    {
        try
        {
            if (!File.Exists(_trustFilePath)) return;
            var owners = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_trustFilePath));
            if (owners != null) _trustedOwners = new HashSet<string>(owners, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Warn($"Couldn't read the trusted authors list: {ex.Message}");
        }
    }

    private void SaveTrusted()
    {
        try
        {
            File.WriteAllText(_trustFilePath,
                JsonSerializer.Serialize(_trustedOwners.ToList(), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Error($"Couldn't save the trusted authors list: {ex.Message}");
        }
    }

    private void LoadCachedVerified()
    {
        try
        {
            if (!File.Exists(_verifiedCachePath)) return;
            var list = JsonSerializer.Deserialize<VerifiedList>(File.ReadAllText(_verifiedCachePath));
            if (list != null && list.Schema <= VerifiedList.SupportedSchema) _verified = list;
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Warn($"Couldn't read the cached verified mod list: {ex.Message}");
        }
    }
}
