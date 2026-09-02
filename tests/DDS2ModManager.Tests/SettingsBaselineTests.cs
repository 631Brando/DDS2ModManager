namespace DDS2ModManager.Tests;

/// Deciding which values in a settings file are the USER'S, when merging a UE4SS update onto it.
///
/// This is the bug that broke a real user's entire mod list. Between UE4SS builds 1093 and 1111 the
/// only meaningful change was in UE4SS-settings.ini: `SecondsToScanBeforeGivingUp` went 30 → 120,
/// because 30 was timing out in the field. The manager read the old 30 as a deliberate choice and
/// carried it onto the new file, UE4SS then gave up scanning, and every mod failed to load with
/// nothing on screen connecting the two.
///
/// The values below are the real ones from those two builds.
public class SettingsBaselineTests
{
    /// UE4SS-settings.ini as build 1093 shipped it, trimmed to the keys that matter here.
    private const string Shipped1093 = """
        [General]
        ; Default: 30
        SecondsToScanBeforeGivingUp = 30

        bUseUObjectArrayCache = true

        [Debug]
        ConsoleEnabled = 1
        """;

    /// The same file from build 1111: the timeout raised, and a new option added.
    private const string Shipped1111 = """
        [General]
        ; Default: 120
        SecondsToScanBeforeGivingUp = 120

        bUseUObjectArrayCache = true

        ; Force slow GUObjectArray iteration even if FUObjectHashTables is available.
        ; Default: false
        bForceGUObjectArrayForIteration = false

        [Debug]
        ConsoleEnabled = 1
        """;

    private static string ValueOf(string ini, string key) =>
        ini.Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith(key))?.Split('=')[1].Trim() ?? "(absent)";

    // ---- the failure, and the fix ---------------------------------------------------------------

    /// A user who never touched the file has a "current" that IS the old default. Treating it as the
    /// baseline carries nothing, so the new value lands — which is the whole fix.
    [Fact]
    public void An_untouched_file_takes_the_new_default()
    {
        var merged = IniSettingsMerger.Merge(Shipped1111, Shipped1093, baseline: Shipped1093);

        Assert.Equal("120", ValueOf(merged.Text, "SecondsToScanBeforeGivingUp"));
        Assert.Empty(merged.Carried);
    }

    /// What used to happen, kept as a test so the behaviour can't come back by accident: with no
    /// baseline at all the merger reads any difference from the new defaults as the user's, and
    /// pins the old value. This is why UE4SSManagerService no longer ever passes null.
    [Fact]
    public void With_no_baseline_at_all_the_old_default_is_still_pinned()
    {
        var merged = IniSettingsMerger.Merge(Shipped1111, Shipped1093, baseline: null);

        Assert.Equal("30", ValueOf(merged.Text, "SecondsToScanBeforeGivingUp"));
        Assert.NotEmpty(merged.Carried);
    }

    /// A value the user really did change still survives, which is the thing the merge exists for.
    [Fact]
    public void A_genuine_edit_is_still_carried()
    {
        var theirs = Shipped1093.Replace("ConsoleEnabled = 1", "ConsoleEnabled = 0");

        var merged = IniSettingsMerger.Merge(Shipped1111, theirs, baseline: Shipped1093);

        Assert.Equal("0", ValueOf(merged.Text, "ConsoleEnabled"));
        // ...and it does not drag the stale timeout along with it.
        Assert.Equal("120", ValueOf(merged.Text, "SecondsToScanBeforeGivingUp"));
    }

    /// Options a newer UE4SS adds have to arrive, with the comments documenting them. Preserving the
    /// user's whole file is the other wrong answer, and this is what rules it out.
    [Fact]
    public void A_setting_the_new_version_adds_arrives_either_way()
    {
        foreach (var baseline in new[] { Shipped1093, null })
        {
            var merged = IniSettingsMerger.Merge(Shipped1111, Shipped1093, baseline);

            Assert.Contains("bForceGUObjectArrayForIteration", merged.Text);
            Assert.Contains("Force slow GUObjectArray iteration", merged.Text);
        }
    }

    // ---- reporting what was not carried ----------------------------------------------------------

    /// Assuming a file is untouched can be wrong, so what that assumption discards has to be
    /// nameable — this is the only record of it, and it has to read as the line to type back.
    [Fact]
    public void Differences_are_reported_as_the_lines_the_user_had()
    {
        var theirs = Shipped1093.Replace("ConsoleEnabled = 1", "ConsoleEnabled = 0");

        var lines = IniSettingsMerger.DifferingLines(theirs, Shipped1111);

        Assert.Contains(lines, l => l.Contains("ConsoleEnabled", StringComparison.OrdinalIgnoreCase)
                                    && l.Contains('0'));
    }

    [Fact]
    public void An_identical_file_reports_no_differences()
    {
        Assert.Empty(IniSettingsMerger.DifferingLines(Shipped1111, Shipped1111));
    }

    /// The old default counts as a difference against the new file — that is exactly the line a user
    /// needs to see explained, rather than silently replaced.
    [Fact]
    public void The_changed_default_shows_up_as_a_difference()
    {
        var lines = IniSettingsMerger.DifferingLines(Shipped1093, Shipped1111);

        Assert.Contains(lines, l => l.Contains("secondstoscan", StringComparison.OrdinalIgnoreCase));
    }
}
