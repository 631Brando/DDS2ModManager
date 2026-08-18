using DDS2ModManager.Services;

namespace DDS2ModManager.Tests;

/// Updating UE4SS has to do two things at once that pull in opposite directions: keep the settings
/// the user chose, and pick up everything the new version added. Keeping their file does the first
/// and fails the second; taking the new file does the second and fails the first. These pin down
/// the merge that does both.
public class IniSettingsMergeTests
{
    // What UE4SS shipped with the version the user currently has.
    private const string Baseline = """
        [General]
        ; Whether to reload all mods when the key defined by HotReloadKey is hit.
        ; Default: 1
        EnableHotReloadSystem = 1

        ; The key that will trigger a reload of all mods.
        ; Default: R
        HotReloadKey = R

        [Debug]
        ; Default: 0
        ConsoleEnabled = 0
        """;

    // The same file in a newer UE4SS: one default changed, one option added, one section added,
    // and the documentation reworded.
    private const string NewDefault = """
        [General]
        ; Whether to reload all mods when the key defined by HotReloadKey is hit.
        ; Default: 1
        EnableHotReloadSystem = 1

        ; The key that will trigger a reload of all mods. The CTRL key is always required.
        ; Default: R
        HotReloadKey = R

        ; Whether to watch the Scripts directory and reload on change.
        ; Default: 0
        EnableAutoReloadingLuaMods = 0

        [Debug]
        ; Default: 1
        ConsoleEnabled = 1

        [Threads]
        ; Default: 4
        SigScannerMultithreadingModuleSizeThreshold = 4
        """;

    private static string ValueOf(string ini, string key) =>
        ini.Replace("\r\n", "\n").Split('\n')
            .Where(l => !l.TrimStart().StartsWith(';'))
            .Select(l => l.Split('=', 2))
            .Where(p => p.Length == 2 && p[0].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
            .Select(p => p[1].Trim())
            .Single();

    // The whole point. The user turned the console on; that has to survive the update.
    [Fact]
    public void A_value_the_user_changed_is_kept()
    {
        var current = Baseline.Replace("ConsoleEnabled = 0", "ConsoleEnabled = 1");

        var merged = IniSettingsMerger.Merge(NewDefault, current, Baseline);

        Assert.Equal("1", ValueOf(merged.Text, "ConsoleEnabled"));
    }

    // The other half of the point, and the reason preserving their whole file is not good enough.
    [Fact]
    public void Options_added_by_the_new_version_appear()
    {
        var current = Baseline.Replace("HotReloadKey = R", "HotReloadKey = F1");

        var merged = IniSettingsMerger.Merge(NewDefault, current, Baseline);

        Assert.Contains("EnableAutoReloadingLuaMods", merged.Text);
        Assert.Contains("[Threads]", merged.Text);
        Assert.Contains("SigScannerMultithreadingModuleSizeThreshold", merged.Text);
        Assert.Equal("F1", ValueOf(merged.Text, "HotReloadKey"));
    }

    // A default the user never touched should follow the new version, not stay pinned at the old
    // one. This is exactly what a baseline buys: without it, ConsoleEnabled = 0 is indistinguishable
    // from a deliberate choice.
    [Fact]
    public void A_default_the_user_never_touched_follows_the_new_version()
    {
        var merged = IniSettingsMerger.Merge(NewDefault, Baseline, Baseline);

        Assert.Equal("1", ValueOf(merged.Text, "ConsoleEnabled"));
        Assert.Equal(NewDefault, merged.Text);
        Assert.False(merged.ChangedAnything);
    }

    // The new file's comments document the new options. Carrying the user's values must not carry
    // the old file's prose with them.
    [Fact]
    public void The_new_versions_comments_and_layout_survive()
    {
        var current = Baseline.Replace("ConsoleEnabled = 0", "ConsoleEnabled = 1");

        var merged = IniSettingsMerger.Merge(NewDefault, current, Baseline);

        Assert.Contains("The CTRL key is always required.", merged.Text);
        Assert.Contains("; Whether to watch the Scripts directory and reload on change.", merged.Text);
        // Section order is the new file's.
        Assert.True(merged.Text.IndexOf("[General]", StringComparison.Ordinal)
                    < merged.Text.IndexOf("[Debug]", StringComparison.Ordinal));
        Assert.True(merged.Text.IndexOf("[Debug]", StringComparison.Ordinal)
                    < merged.Text.IndexOf("[Threads]", StringComparison.Ordinal));
    }

    // A setting the new UE4SS no longer has cannot be honoured. Re-adding it would put back a key
    // nothing reads, so it is reported instead - the user chose it and deserves to know.
    [Fact]
    public void A_setting_the_new_version_dropped_is_reported_not_reinstated()
    {
        const string baseline = "[General]\nRetiredOption = 0\n";
        const string current = "[General]\nRetiredOption = 1\n";
        const string newer = "[General]\nSomethingElse = 1\n";

        var merged = IniSettingsMerger.Merge(newer, current, baseline);

        Assert.DoesNotContain("RetiredOption", merged.Text);
        Assert.Contains(merged.Dropped, d => d.Contains("retiredoption", StringComparison.OrdinalIgnoreCase));
    }

    // A key the user added by hand that the new version does have is still their choice.
    [Fact]
    public void A_key_absent_from_the_baseline_counts_as_the_users()
    {
        const string baseline = "[General]\nEnableHotReloadSystem = 1\n";
        var current = baseline + "HotReloadKey = F5\n";
        const string newer = "[General]\nEnableHotReloadSystem = 1\nHotReloadKey = R\n";

        var merged = IniSettingsMerger.Merge(newer, current, baseline);

        Assert.Equal("F5", ValueOf(merged.Text, "HotReloadKey"));
    }

    // Upgrading from a build that kept no baseline. Values differing from the new default are
    // treated as the user's - the generous reading, because the alternative discards real settings.
    [Fact]
    public void Without_a_baseline_differences_are_treated_as_the_users()
    {
        var current = Baseline.Replace("ConsoleEnabled = 0", "ConsoleEnabled = 1")
                              .Replace("HotReloadKey = R", "HotReloadKey = F1");

        var merged = IniSettingsMerger.Merge(NewDefault, current, baseline: null);

        Assert.Equal("F1", ValueOf(merged.Text, "HotReloadKey"));
        // Still gains what the new version added.
        Assert.Contains("EnableAutoReloadingLuaMods", merged.Text);
    }

    // ConsoleEnabled went 0 -> 1 between versions. With no baseline the user's 0 looks like a
    // choice and is kept; this documents that cost of the fallback rather than hiding it.
    [Fact]
    public void Without_a_baseline_a_changed_default_can_be_pinned()
    {
        var merged = IniSettingsMerger.Merge(NewDefault, Baseline, baseline: null);

        Assert.Equal("0", ValueOf(merged.Text, "ConsoleEnabled"));
    }

    // Sections and keys are matched case-insensitively, as .ini readers do.
    [Fact]
    public void Matching_ignores_case()
    {
        const string baseline = "[General]\nHotReloadKey = R\n";
        const string current = "[GENERAL]\nhotreloadkey = F9\n";
        const string newer = "[General]\nHotReloadKey = R\n";

        var merged = IniSettingsMerger.Merge(newer, current, baseline);

        Assert.Equal("F9", ValueOf(merged.Text, "HotReloadKey"));
    }

    // The same key in two sections is two different settings.
    [Fact]
    public void The_same_key_in_different_sections_stays_separate()
    {
        const string baseline = "[A]\nEnabled = 0\n\n[B]\nEnabled = 0\n";
        const string current = "[A]\nEnabled = 1\n\n[B]\nEnabled = 0\n";
        const string newer = "[A]\nEnabled = 0\n\n[B]\nEnabled = 0\n";

        var merged = IniSettingsMerger.Merge(newer, current, baseline);
        var lines = merged.Text.Replace("\r\n", "\n").Split('\n');
        var a = Array.IndexOf(lines, "[A]");
        var b = Array.IndexOf(lines, "[B]");

        Assert.Equal("Enabled = 1", lines[a + 1]);
        Assert.Equal("Enabled = 0", lines[b + 1]);
    }

    // An empty value is a real setting - UE4SS ships several - and must not read as "unset".
    [Fact]
    public void Clearing_a_value_is_itself_a_change()
    {
        const string baseline = "[Overrides]\nModsFolderPath = C:/Mods\n";
        const string current = "[Overrides]\nModsFolderPath =\n";
        const string newer = "[Overrides]\nModsFolderPath = C:/Mods\n";

        var merged = IniSettingsMerger.Merge(newer, current, baseline);

        Assert.Equal("", ValueOf(merged.Text, "ModsFolderPath"));
    }

    // UE4SS uses +/- prefixes for list-style options, where the key repeats.
    [Fact]
    public void Repeated_list_style_keys_are_carried_as_a_set()
    {
        const string baseline = "[Overrides]\nModsFolderPaths = \n";
        const string current = "[Overrides]\n+ModsFolderPaths = ../SharedMods\n+ModsFolderPaths = D:/MyMods\n";
        const string newer = "[Overrides]\nModsFolderPaths = \n";

        var merged = IniSettingsMerger.Merge(newer, current, baseline);

        Assert.Contains("+ModsFolderPaths = ../SharedMods", merged.Text);
        Assert.Contains("+ModsFolderPaths = D:/MyMods", merged.Text);
    }

    // Commented-out examples sit directly above the real key in UE4SS's file. Reading them as
    // values would invent settings nobody wrote.
    [Fact]
    public void Commented_examples_are_not_read_as_values()
    {
        const string newer = """
            [Overrides]
            ; Example: +ModsFolderPaths = ../SharedMods
            ModsFolderPath =
            """;

        var merged = IniSettingsMerger.Merge(newer, newer, newer);

        Assert.False(merged.ChangedAnything);
        Assert.Equal(newer, merged.Text);
    }

    // Merging an unchanged file must be a no-op, or repeated updates would slowly reformat it.
    [Fact]
    public void Merging_an_identical_file_changes_nothing()
    {
        var merged = IniSettingsMerger.Merge(NewDefault, NewDefault, NewDefault);

        Assert.Equal(NewDefault, merged.Text);
        Assert.Empty(merged.Carried);
        Assert.Empty(merged.Dropped);
    }

    // Running the merge twice must land in the same place - the second update after a change
    // shouldn't drift the file further.
    [Fact]
    public void The_merge_is_stable_when_repeated()
    {
        var current = Baseline.Replace("ConsoleEnabled = 0", "ConsoleEnabled = 1");

        var once = IniSettingsMerger.Merge(NewDefault, current, Baseline).Text;
        var twice = IniSettingsMerger.Merge(NewDefault, once, NewDefault).Text;

        Assert.Equal(once, twice);
    }

    [Fact]
    public void What_was_carried_over_is_reported()
    {
        var current = Baseline.Replace("ConsoleEnabled = 0", "ConsoleEnabled = 1");

        var merged = IniSettingsMerger.Merge(NewDefault, current, Baseline);

        Assert.Single(merged.Carried);
        Assert.Contains("consoleenabled", merged.Carried[0], StringComparison.OrdinalIgnoreCase);
    }
}
