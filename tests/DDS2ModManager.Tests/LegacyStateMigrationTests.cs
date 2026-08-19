namespace DDS2ModManager.Tests;

/// The one-time move of %AppData% state from the original flat layout into per-game folders.
///
/// This is the most dangerous code in the multi-game change. A disabled mod's registry entry holds
/// ABSOLUTE paths into the flat DisabledMods folder; move the files without rewriting those paths
/// and the mod's file list resolves to nothing, which is unrecoverable except by hand-editing JSON.
/// So the rules under test are: attribute confidently or not at all, never touch what you cannot
/// attribute, and never move files without re-recording where they went.
public class LegacyStateMigrationTests : IDisposable
{
    private readonly List<string> _temps = [];

    public void Dispose()
    {
        foreach (var t in _temps) { try { Directory.Delete(t, true); } catch { } }
    }

    private string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "dds_mig_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        _temps.Add(d);
        return d;
    }

    private const string KeyA = "a1b2c3d4e5f6";
    private const string KeyB = "0f1e2d3c4b5a";

    // ---- attribution ------------------------------------------------------------------------

    // The flat layout predates multi-game entirely, so the state can only belong to one game. One
    // tracked install names it with no ambiguity.
    [Fact]
    public void A_single_tracked_install_is_the_owner()
    {
        Assert.Equal(KeyA, LegacyStateMigrationService.ResolveLegacyGameKey([KeyA], rememberedKey: null));
    }

    // Two installs and no way to tell which owned the shared pile. Guessing would move one install's
    // disabled mods under the other's key and strand them, so the answer must be "don't".
    [Fact]
    public void Several_tracked_installs_with_no_remembered_path_refuses_to_guess()
    {
        Assert.Null(LegacyStateMigrationService.ResolveLegacyGameKey([KeyA, KeyB], rememberedKey: null));
    }

    [Fact]
    public void Several_tracked_installs_are_resolved_by_the_remembered_path()
    {
        Assert.Equal(KeyB, LegacyStateMigrationService.ResolveLegacyGameKey([KeyA, KeyB], KeyB));
    }

    // A remembered path that isn't one of the tracked installs tells us nothing about who owns the
    // state - it is likelier to be a game that was just never used than the owner of this pile.
    [Fact]
    public void A_remembered_path_that_matches_no_registry_refuses_to_guess()
    {
        Assert.Null(LegacyStateMigrationService.ResolveLegacyGameKey([KeyA, KeyB], "ffffffffffff"));
    }

    // No mods ever tracked, but profiles and history can still exist and are worth keeping.
    [Fact]
    public void With_no_registries_the_remembered_path_is_used()
    {
        Assert.Equal(KeyA, LegacyStateMigrationService.ResolveLegacyGameKey([], KeyA));
        Assert.Null(LegacyStateMigrationService.ResolveLegacyGameKey([], null));
    }

    // ---- key recognition ----------------------------------------------------------------------

    // The per-game folders live INSIDE the legacy roots, so the migration has to be able to tell a
    // key folder from content. Getting this wrong means trying to move a folder into itself.
    [Fact]
    public void A_game_key_is_told_apart_from_a_mod_id()
    {
        Assert.True(LegacyStateMigrationService.LooksLikeGameKey(KeyA));
        Assert.False(LegacyStateMigrationService.LooksLikeGameKey(Guid.NewGuid().ToString("N")));
        Assert.False(LegacyStateMigrationService.LooksLikeGameKey("Profiles"));
        Assert.False(LegacyStateMigrationService.LooksLikeGameKey("zzzzzzzzzzzz"));
    }

    [Fact]
    public void A_key_is_recovered_from_a_registry_filename()
    {
        Assert.Equal(KeyA, AppPaths.KeyFromRegistryPath($@"C:\x\registry_{KeyA}.json"));
        Assert.Null(AppPaths.KeyFromRegistryPath(@"C:\x\settings.json"));
        Assert.Null(AppPaths.KeyFromRegistryPath(@"C:\x\registry_.json"));
    }

    // ---- path rewriting -----------------------------------------------------------------------

    [Fact]
    public void Rebase_moves_a_matching_prefix_and_leaves_anything_else()
    {
        Assert.Equal(@"C:\new\Mod.pak", LegacyStateMigrationService.Rebase(@"C:\old\Mod.pak", @"C:\old", @"C:\new"));

        // Windows paths are case-insensitive, and the recorded casing is whatever was written years ago.
        Assert.Equal(@"C:\new\Mod.pak", LegacyStateMigrationService.Rebase(@"c:\OLD\Mod.pak", @"C:\old", @"C:\new"));

        Assert.Equal(@"D:\elsewhere\Mod.pak",
            LegacyStateMigrationService.Rebase(@"D:\elsewhere\Mod.pak", @"C:\old", @"C:\new"));

        Assert.Equal("", LegacyStateMigrationService.Rebase(null, @"C:\old", @"C:\new"));
    }

    // ---- moving children ----------------------------------------------------------------------

    [Fact]
    public void Children_are_moved_into_the_per_game_folder()
    {
        var legacy = TempDir();
        File.WriteAllText(Path.Combine(legacy, "Main.json"), "{}");
        Directory.CreateDirectory(Path.Combine(legacy, "somefolder"));

        var target = Path.Combine(legacy, KeyA);
        LegacyStateMigrationService.MoveChildrenInto(legacy, target, "profiles", LoggingService.Instance);

        Assert.True(File.Exists(Path.Combine(target, "Main.json")));
        Assert.True(Directory.Exists(Path.Combine(target, "somefolder")));
        Assert.False(File.Exists(Path.Combine(legacy, "Main.json")));
    }

    // The trap: the destination is a subfolder of the source. A second run - or a run after another
    // game already has a folder here - must not try to move the key folder into itself.
    [Fact]
    public void An_existing_per_game_folder_is_not_moved_into_itself()
    {
        var legacy = TempDir();
        var other = Path.Combine(legacy, KeyB);
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "already.json"), "{}");

        var target = Path.Combine(legacy, KeyA);
        LegacyStateMigrationService.MoveChildrenInto(legacy, target, "profiles", LoggingService.Instance);

        // The other game's folder is untouched, and did not end up nested inside this one.
        Assert.True(File.Exists(Path.Combine(other, "already.json")));
        Assert.False(Directory.Exists(Path.Combine(target, KeyB)));
    }

    // ---- disabled mods, the destructive one -----------------------------------------------------

    [Fact]
    public void A_claimed_disabled_mod_is_moved_and_its_recorded_paths_rewritten()
    {
        var root = TempDir();
        var legacy = Path.Combine(root, "DisabledMods");
        var modId = Guid.NewGuid().ToString("N");
        var modDir = Path.Combine(legacy, modId);
        Directory.CreateDirectory(modDir);
        var pak = Path.Combine(modDir, "Cool.pak");
        File.WriteAllText(pak, "pak");

        var registryPath = Path.Combine(root, $"registry_{KeyA}.json");
        var registry = new ModRegistryService(registryPath);
        registry.Upsert(new ModInfo
        {
            Id = modId, Name = "Cool", IsEnabled = false,
            InstallPath = modDir, InstallFiles = [pak]
        });

        var target = Path.Combine(legacy, KeyA);
        var (moved, skipped) = LegacyStateMigrationService.MigrateDisabledMods(
            legacy, target, registry, LoggingService.Instance);

        Assert.Equal(1, moved);
        Assert.Equal(0, skipped);

        var movedPak = Path.Combine(target, modId, "Cool.pak");
        Assert.True(File.Exists(movedPak));
        Assert.False(Directory.Exists(modDir));

        // The whole point: the record follows the files. Re-read from disk, not from memory, because
        // an in-memory-only rewrite would still lose everything on the next launch.
        var reloaded = new ModRegistryService(registryPath).Mods.Single();
        Assert.Equal(movedPak, reloaded.InstallFiles.Single());
        Assert.Equal(Path.Combine(target, modId), reloaded.InstallPath);
    }

    // An unclaimed folder may belong to a second install whose own registry still resolves it via
    // the absolute path it recorded. Moving it under this game's key would strand it.
    [Fact]
    public void An_unclaimed_disabled_mod_folder_is_left_alone()
    {
        var root = TempDir();
        var legacy = Path.Combine(root, "DisabledMods");
        var orphan = Path.Combine(legacy, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(orphan);
        File.WriteAllText(Path.Combine(orphan, "Someone.pak"), "pak");

        var registry = new ModRegistryService(Path.Combine(root, $"registry_{KeyA}.json"));

        var target = Path.Combine(legacy, KeyA);
        var (moved, skipped) = LegacyStateMigrationService.MigrateDisabledMods(
            legacy, target, registry, LoggingService.Instance);

        Assert.Equal(0, moved);
        Assert.Equal(1, skipped);
        Assert.True(File.Exists(Path.Combine(orphan, "Someone.pak")));
    }

    // Running twice must not double-move, lose anything, or rewrite an already-correct path.
    [Fact]
    public void Migrating_twice_is_a_no_op_the_second_time()
    {
        var root = TempDir();
        var legacy = Path.Combine(root, "DisabledMods");
        var modId = Guid.NewGuid().ToString("N");
        var modDir = Path.Combine(legacy, modId);
        Directory.CreateDirectory(modDir);
        var pak = Path.Combine(modDir, "Cool.pak");
        File.WriteAllText(pak, "pak");

        var registryPath = Path.Combine(root, $"registry_{KeyA}.json");
        var registry = new ModRegistryService(registryPath);
        registry.Upsert(new ModInfo
        {
            Id = modId, Name = "Cool", IsEnabled = false,
            InstallPath = modDir, InstallFiles = [pak]
        });

        var target = Path.Combine(legacy, KeyA);
        LegacyStateMigrationService.MigrateDisabledMods(legacy, target, registry, LoggingService.Instance);
        var after1 = new ModRegistryService(registryPath).Mods.Single().InstallFiles.Single();

        var (moved2, _) = LegacyStateMigrationService.MigrateDisabledMods(
            legacy, target, new ModRegistryService(registryPath), LoggingService.Instance);
        var after2 = new ModRegistryService(registryPath).Mods.Single().InstallFiles.Single();

        Assert.Equal(0, moved2);
        Assert.Equal(after1, after2);
        Assert.True(File.Exists(after2));
    }
}
