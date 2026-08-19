namespace DDS2ModManager.Tests;

/// Working out what applying a profile would change.
///
/// This decides which of the user's mods get switched on and off, so the two things that matter
/// are that it never touches a mod the profile didn't mention, and that it never tries to act on
/// a mod that isn't installed.
public class ModProfileTests : IDisposable
{
    // An explicit temp folder, not the default. These tests only exercise Plan(), which touches no
    // files - but constructing the service creates its directory, and the default is now the real
    // per-game profiles folder under %AppData%. A test run has no business creating folders there.
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "dds_profiles_" + Guid.NewGuid().ToString("N")[..8]);

    private readonly ModProfileService _service;

    public ModProfileTests() => _service = new ModProfileService(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static ModInfo Mod(string name, ModType type, bool enabled) =>
        new() { Name = name, Type = type, IsEnabled = enabled, IsInstalled = true };

    private static ModProfile Profile(params (string Name, ModType Type, bool Enabled)[] mods) => new()
    {
        Name = "test",
        Mods = mods.Select(m => new ProfileMod { Name = m.Name, Type = m.Type, Enabled = m.Enabled }).ToList()
    };

    [Fact]
    public void A_mod_the_profile_wants_on_is_enabled()
    {
        var installed = new[] { Mod("Scooter", ModType.LogicMod, enabled: false) };
        var plan = _service.Plan(Profile(("Scooter", ModType.LogicMod, true)), installed);

        Assert.Single(plan.ToEnable);
        Assert.Empty(plan.ToDisable);
        Assert.True(plan.ChangesAnything);
    }

    [Fact]
    public void A_mod_the_profile_wants_off_is_disabled()
    {
        var installed = new[] { Mod("Scooter", ModType.LogicMod, enabled: true) };
        var plan = _service.Plan(Profile(("Scooter", ModType.LogicMod, false)), installed);

        Assert.Single(plan.ToDisable);
        Assert.Empty(plan.ToEnable);
    }

    /// A mod already in the wanted state is not touched, so applying a profile twice is a no-op
    /// rather than a churn of enable/disable operations on the filesystem.
    [Fact]
    public void A_mod_already_in_the_wanted_state_is_left_alone()
    {
        var installed = new[]
        {
            Mod("On", ModType.LogicMod, enabled: true),
            Mod("Off", ModType.LuaMod, enabled: false)
        };

        var plan = _service.Plan(Profile(("On", ModType.LogicMod, true), ("Off", ModType.LuaMod, false)), installed);

        Assert.Empty(plan.ToEnable);
        Assert.Empty(plan.ToDisable);
        Assert.False(plan.ChangesAnything);
    }

    /// THE safety property. A profile from another machine will name mods this one doesn't have;
    /// those are reported, never installed, and never mistaken for something else.
    [Fact]
    public void A_mod_that_is_not_installed_is_reported_not_acted_on()
    {
        var installed = new[] { Mod("Scooter", ModType.LogicMod, enabled: false) };
        var plan = _service.Plan(Profile(("SomeoneElsesMod", ModType.LogicMod, true)), installed);

        Assert.Empty(plan.ToEnable);
        Assert.Empty(plan.ToDisable);
        Assert.Single(plan.Missing);
        Assert.Contains("SomeoneElsesMod", plan.Missing[0]);
    }

    /// The other half of the same property: a profile says what it knew about, not that everything
    /// else should be switched off.
    [Fact]
    public void An_installed_mod_missing_from_the_profile_is_left_alone()
    {
        var installed = new[]
        {
            Mod("InProfile", ModType.LogicMod, enabled: false),
            Mod("NotInProfile", ModType.LuaMod, enabled: true)
        };

        var plan = _service.Plan(Profile(("InProfile", ModType.LogicMod, true)), installed);

        Assert.Single(plan.ToEnable);
        Assert.Empty(plan.ToDisable);
        Assert.Single(plan.Extra);
        Assert.Contains("NotInProfile", plan.Extra[0]);
    }

    /// Two-part mods ship two rows sharing a name. Matching on name alone would apply one entry's
    /// state to both halves, so the type has to be part of the match.
    [Fact]
    public void Two_halves_sharing_a_name_are_matched_separately()
    {
        var installed = new[]
        {
            Mod("Ethanol", ModType.LogicMod, enabled: true),
            Mod("Ethanol", ModType.LuaMod, enabled: true)
        };

        var plan = _service.Plan(Profile(("Ethanol", ModType.LogicMod, true), ("Ethanol", ModType.LuaMod, false)), installed);

        Assert.Empty(plan.ToEnable);
        Assert.Single(plan.ToDisable);
        Assert.Equal(ModType.LuaMod, plan.ToDisable[0].Type);
        Assert.Empty(plan.Extra);
    }

    [Fact]
    public void Names_are_matched_ignoring_case()
    {
        var installed = new[] { Mod("Scooter", ModType.LogicMod, enabled: false) };
        var plan = _service.Plan(Profile(("SCOOTER", ModType.LogicMod, true)), installed);

        Assert.Single(plan.ToEnable);
        Assert.Empty(plan.Missing);
    }

    /// The exported text is what somebody pastes into a bug report, so it has to carry the state
    /// and the version rather than just a list of names.
    [Fact]
    public void Shareable_text_records_state_and_version()
    {
        var profile = new ModProfile
        {
            Name = "mine",
            ManagerVersion = "1.0.6",
            GameVersion = "1.2.3",
            Mods =
            {
                new ProfileMod { Name = "Scooter", Type = ModType.LogicMod, Enabled = true, Version = "1.0.0" },
                new ProfileMod { Name = "OldThing", Type = ModType.LuaMod, Enabled = false }
            }
        };

        var text = ModProfileService.ToShareableText(profile);

        Assert.Contains("[on]", text);
        Assert.Contains("[off]", text);
        Assert.Contains("Scooter", text);
        Assert.Contains("1.0.6", text);
        Assert.Contains("v1.0.0", text);
    }

    // ---- the exported Nexus id agrees with what the app is showing --------------------------------

    /// A profile records the id the manager actually resolved, which for a linked mod is the user's
    /// declaration and not the name match. Same precedence as NexusModMatcher.Resolve, so the
    /// exported file cannot disagree with the row it was captured from.
    [Fact]
    public void A_declared_link_is_what_gets_exported()
    {
        var mod = new ModInfo
        {
            Name = "AERR",
            Type = ModType.DllPlugin,
            NexusLink = new NexusModLink { ModId = 79, GameDomain = "drugdealersimulator" }
        };

        var profile = _service.Capture("p", [mod], "1.2.0", "1.0.0");

        Assert.Equal(79, profile.Mods.Single().NexusModId);
    }

    /// "This mod has no Nexus page" carries ModId 0, and falling through to a match there would
    /// export the very guess the user rejected.
    [Fact]
    public void A_no_page_declaration_exports_nothing_even_when_a_match_exists()
    {
        var mod = new ModInfo
        {
            Name = "Bigger Packages",
            Type = ModType.PatchMod,
            NexusInfo = new NexusModPost { ModId = 110, Name = "Bigger Packages" },
            NexusLink = new NexusModLink { Kind = NexusLinkKind.NoPage, GameDomain = "drugdealersimulator" }
        };

        var profile = _service.Capture("p", [mod], "1.2.0", "1.0.0");

        Assert.Equal(0, profile.Mods.Single().NexusModId);
    }

    /// Unchanged for every mod that has no link, which is almost all of them.
    [Fact]
    public void A_name_matched_mod_still_exports_its_matched_id()
    {
        var mod = new ModInfo
        {
            Name = "Bigger Packages",
            Type = ModType.PatchMod,
            NexusInfo = new NexusModPost { ModId = 110, Name = "Bigger Packages" }
        };

        var profile = _service.Capture("p", [mod], "1.2.0", "1.0.0");

        Assert.Equal(110, profile.Mods.Single().NexusModId);
    }
}
