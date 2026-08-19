namespace DDS2ModManager.Tests;

/// The follow-up round. Every case here is a bug that shipped or nearly shipped, and every one of
/// them failed SILENTLY - which is why each gets a test rather than a comment.
public class FollowUpFixTests : IDisposable
{
    private readonly List<string> _temps = [];

    public void Dispose()
    {
        foreach (var t in _temps) { try { Directory.Delete(t, true); } catch { } }
    }

    private string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "dds_fu_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        _temps.Add(d);
        return d;
    }

    private static void Write(string path, string text = "x")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    // ---- DDS1 logic mods must be installed flat --------------------------------------------------

    // UnrealModLoader scans Content\Paks\LogicMods with a NON-recursive iterator, and that scan is
    // the only thing that populates its mod list. A pak in a subfolder still mounts - Unreal finds
    // paks recursively - so its assets load while its ModActor never spawns. The mod appears
    // installed, does nothing, and logs nothing anywhere.
    [Fact]
    public void Only_a_loader_that_reads_subfolders_gets_subfoldered_logic_mods()
    {
        Assert.True(GameProfiles.Dds2.LogicModsUseSubfolders);
        Assert.False(GameProfiles.Dds1.LogicModsUseSubfolders);
    }

    // ---- the fingerprint must not outrun the analysis it describes --------------------------------

    // Capture() returns an empty fingerprint when none of a mod's files can be found. The old guard
    // then read that as "nothing recorded" on the NEXT pass, so a mod whose files vanished had its
    // drift check permanently disarmed - five rows in one real registry were already in that state.
    [Fact]
    public void Every_recorded_file_missing_reports_drift_rather_than_silence()
    {
        var dir = TempDir();
        var pak = Path.Combine(dir, "Cool.pak");
        Write(pak);

        var mod = new ModInfo { Name = "Cool", Type = ModType.PatchMod, InstallPath = dir, InstallFiles = [pak] };
        mod.Fingerprint = ModFileStateService.Capture(mod);
        Assert.True(mod.Fingerprint.Files.Count > 0);

        File.Delete(pak);

        var drift = ModFileStateService.Compare(mod);

        Assert.True(drift.Any);
        Assert.Single(drift.Missing);
    }

    // A loose-asset mod's files span Content\<Category>\ subfolders, where the same filename under
    // two categories is entirely normal. Keyed on the bare filename those two collapsed into one
    // entry, so the fingerprint silently described fewer files than the mod owns and drift in the
    // shadowed one could never be seen.
    [Fact]
    public void Two_files_with_one_name_in_different_folders_stay_separate()
    {
        var content = TempDir();
        var a = Path.Combine(content, "DataTables", "Shared.uasset");
        var b = Path.Combine(content, "Drugs", "Shared.uasset");
        Write(a, "aaa");
        Write(b, "bbbbbb");

        var mod = new ModInfo
        {
            Name = "Loose", Type = ModType.LooseAsset, InstallPath = content, InstallFiles = [a, b]
        };

        var print = ModFileStateService.Capture(mod);

        Assert.Equal(2, print.Files.Count);
    }

    // ---- conflict checking must not quietly stop covering a mod ----------------------------------

    // Deep Scan is the button the UI presents as authoritative, and it cleared the panel before
    // rebuilding it. Dropping loose mods there meant DDS1 - where nearly every mod is loose - got
    // "no conflicts found" reported over the top of real conflicts.
    [Fact]
    public void A_drifted_mod_is_flagged_as_needing_a_refresh()
    {
        var clean = new ModInfo
        {
            Name = "Clean", Type = ModType.LogicMod, IsInstalled = true,
            HasModActor = true, DataTableScanCompleted = true
        };
        Assert.False(CompatibilityCheckerService.NeedsDataTableRefresh([clean]));

        // Same mod, but its files changed under us: what is recorded describes the previous build.
        var drifted = new ModInfo
        {
            Name = "Drifted", Type = ModType.LogicMod, IsInstalled = true,
            HasModActor = true, DataTableScanCompleted = true,
            DriftSummary = "1 file modified"
        };
        Assert.True(CompatibilityCheckerService.NeedsDataTableRefresh([drifted]));
    }

    // A patch mod's ContainedAssetPaths are refreshed by DeepScan too, but nothing ever asked for
    // that refresh, so a drifted patch mod stayed stale indefinitely.
    [Fact]
    public void A_drifted_patch_mod_also_needs_a_refresh()
    {
        var drifted = new ModInfo
        {
            Name = "Patch", Type = ModType.PatchMod, IsInstalled = true, DriftSummary = "1 file modified"
        };

        Assert.True(CompatibilityCheckerService.NeedsDataTableRefresh([drifted]));
    }

    // ---- Nexus data must not cross between games --------------------------------------------------

    // Nexus mod ids restart per game: id 118 is this app on one game and somebody else's mod on the
    // other. Hardcoded, the "this app" badge would eventually land on a stranger's mod.
    [Fact]
    public void The_managers_own_nexus_id_is_per_game()
    {
        Assert.Equal(118, GameProfiles.Dds2.ManagerNexusModId);
        Assert.Null(GameProfiles.Dds1.ManagerNexusModId);
    }

    // ---- curated authors are per game --------------------------------------------------------------

    // Absent games means every game. That is what keeps the file already published at a fixed URL
    // working unchanged for a build that understands the new field, and vice versa.
    [Fact]
    public void An_author_with_no_games_listed_applies_everywhere()
    {
        var any = new TrustedNexusAuthor { Name = "huslaa" };

        Assert.True(any.AppliesTo("dds1"));
        Assert.True(any.AppliesTo("dds2"));
    }

    [Fact]
    public void An_author_scoped_to_one_game_does_not_appear_on_the_other()
    {
        var list = new TrustedNexusAuthorList
        {
            Authors =
            {
                new TrustedNexusAuthor { Name = "dds1only", Games = ["dds1"] },
                new TrustedNexusAuthor { Name = "everywhere" }
            }
        };

        Assert.True(list.Contains("dds1only", "dds1"));
        Assert.False(list.Contains("dds1only", "dds2"));

        Assert.True(list.Contains("everywhere", "dds1"));
        Assert.True(list.Contains("everywhere", "dds2"));
    }

    // The published file has schema 1 and no games field. A new build must still read it, because
    // TrustedNexusAuthorService refuses anything above SupportedSchema and keeps the previous copy -
    // so bumping the schema would make every installed build reject the file permanently.
    [Fact]
    public void The_published_schema_is_not_bumped()
    {
        Assert.Equal(1, TrustedNexusAuthorList.SupportedSchema);
        Assert.Equal(1, new TrustedNexusAuthorList().Schema);
    }
}
