using System.Reflection;

namespace DDS2ModManager.Tests;

/// The identifiers the rename must NOT touch.
///
/// The app now calls itself "DDS Mod Manager", because it manages both Drug Dealer Simulator games.
/// That is a display change only. Everything pinned here is something the outside world already
/// depends on - a published SDK, a file on a user's disk, or a release asset an installed copy looks
/// for by exact name - and each has a specific, silent way of failing if it is "tidied up" to match.
///
/// If you are deliberately changing one of these, delete the case and say why in the commit. Do not
/// simply update the expected value: the point of the test is that the value cannot move quietly.
public class RebrandCompatibilityTests
{
    /// Named in the published modding guide and already shipped inside real mods. A mod author's
    /// file does not get renamed because we renamed our window title.
    [Fact]
    public void The_mod_manifest_filename_is_frozen() =>
        Assert.Equal(".dds2mod.json", ModManifest.FileName);

    /// Fixed by agreement with the SDK's ModActor template. Renaming these makes every mod already
    /// published stop declaring an update source - and the symptom is simply that updates never
    /// appear again, with no error anywhere.
    [Fact]
    public void The_blueprint_variable_names_are_frozen()
    {
        Assert.Equal("ModUpdateUrl", ModUpdateSourceResolver.UrlProperty);
        Assert.Equal("ModVersion", ModUpdateSourceResolver.VersionProperty);
        Assert.Equal("ModAuthor", ModUpdateSourceResolver.AuthorProperty);
    }

    /// Written next to the user's own .ini files when the manager first edits one, and read back as
    /// "the original, before we touched it". A rename orphans every backup already on disk, so the
    /// revert button silently stops finding anything to revert to.
    [Fact]
    public void The_config_backup_suffix_is_frozen() =>
        Assert.Equal(".dds2mm.bak", GameConfigService.BackupSuffix);

    /// The folder holding settings, the mod registry and every disabled mod's real files.
    ///
    /// Invisible to users, and renaming it would mean rewriting the absolute paths recorded inside
    /// registry_*.json for every disabled mod. That is real risk for no benefit.
    [Fact]
    public void The_appdata_folder_name_is_frozen() =>
        Assert.Equal("DDS2ModManager", AppPaths.AppDataFolderName);

    /// THE ONE THAT STRANDS PEOPLE.
    ///
    /// AppUpdateService matches the release asset by EXACT filename, and a release with no matching
    /// asset is reported as "you're on the latest version" rather than as an error. Publish one
    /// release named anything else and every installed copy stops updating, permanently and
    /// silently - and the fix can only be delivered through the channel that is broken.
    ///
    /// If this ever does change, both names have to be published on every release from then on.
    [Fact]
    public void The_release_asset_name_is_frozen()
    {
        var field = typeof(AppUpdateService)
            .GetField("AssetName", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(field);
        Assert.Equal("DDS2ModManager.exe", field!.GetRawConstantValue());
    }

    /// Two services fetch curated lists from this repository on every launch, and the shell verb and
    /// uninstall entry are registered under the same name. Renaming the repository is a separate,
    /// deliberate piece of work, not a side effect of changing what the window says.
    [Fact]
    public void The_update_repository_is_frozen()
    {
        var repo = typeof(AppUpdateService)
            .GetField("Repo", BindingFlags.NonPublic | BindingFlags.Static)?.GetRawConstantValue();

        Assert.Equal("DDS2ModManager", repo);
    }

    /// And the display name really did change, so this suite can't pass by nothing having happened.
    [Fact]
    public void The_visible_name_no_longer_claims_to_be_dds2_only()
    {
        Assert.Equal("DDS Mod Manager", AppPaths.AppDisplayName);
        Assert.DoesNotContain("DDS2", AppPaths.AppDisplayName);
    }
}
