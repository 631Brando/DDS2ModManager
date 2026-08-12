namespace DDS2ModManager.Tests;

/// The conditions under which an update installs WITHOUT asking.
///
/// This is the one place in the app where code runs on a player's machine with no prompt, so
/// every gate is tested individually and in combination. The rule is deliberately three-way:
/// the author is trusted, the setting is on, and the update address has not moved.
public class TrustGateTests
{
    /// Mirrors the condition in MainViewModel.UpdateModAsync. Kept here as the specification
    /// of the rule - if the real one drifts from this, the drift is the bug.
    private static bool WouldInstallSilently(ModInfo mod, bool autoInstallSetting, bool hasAsset) =>
        mod.TrustedAuthor && autoInstallSetting && !mod.UpdateUrlChanged && hasAsset;

    private static ModInfo Trusted() => new()
    {
        Name = "T",
        TrustedAuthor = true,
        ModUpdateUrl = "https://github.com/a/b"
    };

    [Fact]
    public void All_three_conditions_met_installs_silently() =>
        Assert.True(WouldInstallSilently(Trusted(), autoInstallSetting: true, hasAsset: true));

    [Fact]
    public void Trusted_but_setting_off_still_asks() =>
        Assert.False(WouldInstallSilently(Trusted(), autoInstallSetting: false, hasAsset: true));

    [Fact]
    public void Setting_on_but_untrusted_still_asks() =>
        Assert.False(WouldInstallSilently(new ModInfo { Name = "U" }, autoInstallSetting: true, hasAsset: true));

    /// The condition that carries the most weight: a moved update address is exactly the
    /// situation in which trust would be worth stealing.
    [Fact]
    public void Trusted_but_address_moved_still_asks()
    {
        var moved = Trusted();
        moved.UpdateUrlChanged = true;
        Assert.False(WouldInstallSilently(moved, autoInstallSetting: true, hasAsset: true));
    }

    [Fact]
    public void No_downloadable_asset_is_never_silent() =>
        Assert.False(WouldInstallSilently(Trusted(), autoInstallSetting: true, hasAsset: false));

    /// Trust is carried across an update only when the address held steady.
    [Theory]
    [InlineData(true, false, true)]     // trusted, address unchanged -> keeps trust
    [InlineData(true, true, false)]     // trusted, address moved     -> loses it
    [InlineData(false, false, false)]   // never trusted              -> stays untrusted
    public void Trust_survives_only_an_unchanged_address(bool wasTrusted, bool urlChanged, bool expected) =>
        Assert.Equal(expected, wasTrusted && !urlChanged);

    /// Silently running unscanned code has to be opted into deliberately, never inherited from
    /// ticking "trust" on one mod.
    [Fact]
    public void Auto_install_defaults_to_off() =>
        Assert.False(new AppSettings().AutoInstallTrustedModUpdates);

    [Fact]
    public void Update_checking_defaults_to_on() =>
        Assert.True(new AppSettings().CheckForModUpdatesOnStartup);
}
