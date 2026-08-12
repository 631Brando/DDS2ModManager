namespace DDS2ModManager.Tests;

/// Conflict detection between lua mods.
///
/// The signal here was chosen by measurement, not by guessing: hooking the same UFunction was
/// evaluated and REJECTED (on a real 17-mod install it produced 9 pairs of noise and 0 real
/// conflicts, because UE4SS runs every registered callback and sharing a hook target is
/// normal). What survived is duplicate console commands, duplicate keybinds, and two mods in
/// one folder. These tests pin that scope down - a future signal with a worse false-positive
/// rate should have to break a test to get in.
public class LuaConflictTests : IDisposable
{
    private readonly List<string> _temp = new();
    private readonly CompatibilityCheckerService _checker = new();

    /// A lua mod on disk, with its script, wired up the way the registry stores one.
    private ModInfo LuaMod(string name, string luaSource)
    {
        var root = Path.Combine(Path.GetTempPath(), "DDS2MMLua_" + Guid.NewGuid().ToString("N")[..10], name);
        Directory.CreateDirectory(Path.Combine(root, "Scripts"));
        File.WriteAllText(Path.Combine(root, "Scripts", "main.lua"), luaSource);
        _temp.Add(Path.GetDirectoryName(root)!);

        return new ModInfo
        {
            Name = name,
            Type = ModType.LuaMod,
            IsInstalled = true,
            IsEnabled = true,
            InstallPath = root,
            InstallFiles = new List<string> { root },
            ContainedAssetPaths = new List<string> { "Scripts/main.lua" }
        };
    }

    public void Dispose()
    {
        foreach (var d in _temp) try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }
    }

    /// THE trap, and the reason the checker splits pak mods from lua mods rather than just
    /// widening its type filter.
    ///
    /// Every lua mod's ContainedAssetPaths contains "Scripts/main.lua". Feeding them through
    /// the existing file-overlap rule would make EVERY pair of lua mods a Critical "both
    /// replace the same file" - nine mods would produce thirty-six red cards, none of them
    /// real. A comment says this split is load-bearing; this is the test that keeps it so.
    [Fact]
    public void Two_lua_mods_do_not_conflict_merely_for_both_having_main_lua()
    {
        var a = LuaMod("AlphaMod", "RegisterConsoleCommandHandler('alpha.hello', function() end)");
        var b = LuaMod("BetaMod", "RegisterConsoleCommandHandler('beta.hello', function() end)");

        Assert.Empty(_checker.CheckConflicts(new[] { a, b }));
    }

    [Fact]
    public void Two_lua_mods_registering_the_same_command_conflict()
    {
        var a = LuaMod("AlphaMod", "RegisterConsoleCommandHandler(\"shared.cmd\", function() end)");
        var b = LuaMod("BetaMod", "RegisterConsoleCommandHandler('shared.cmd', function() end)");

        var conflicts = _checker.CheckConflicts(new[] { a, b });

        var pair = Assert.Single(conflicts);
        Assert.Equal(ConflictKind.LuaConsoleCommandClash, pair.Kind);
        Assert.Contains("shared.cmd", string.Join(" ", pair.AssetPaths));
    }

    [Fact]
    public void Two_lua_mods_binding_the_same_key_conflict()
    {
        var a = LuaMod("AlphaMod", "RegisterKeyBind(Key.F7, function() end)");
        var b = LuaMod("BetaMod", "RegisterKeyBind(Key.F7, function() end)");

        var pair = Assert.Single(_checker.CheckConflicts(new[] { a, b }));
        Assert.Equal(ConflictKind.LuaKeybindClash, pair.Kind);
    }

    /// Modifier ORDER is not meaningful - {CONTROL,SHIFT} and {SHIFT,CONTROL} are the same
    /// chord, and UE4SS treats them as one.
    [Fact]
    public void Modifier_order_does_not_change_a_keybind()
    {
        var a = LuaMod("AlphaMod", "RegisterKeyBind(Key.F5, {ModifierKey.CONTROL, ModifierKey.SHIFT}, function() end)");
        var b = LuaMod("BetaMod", "RegisterKeyBind(Key.F5, {ModifierKey.SHIFT, ModifierKey.CONTROL}, function() end)");

        Assert.Single(_checker.CheckConflicts(new[] { a, b }));
    }

    [Fact]
    public void A_different_modifier_is_a_different_bind()
    {
        var a = LuaMod("AlphaMod", "RegisterKeyBind(Key.F5, {ModifierKey.CONTROL}, function() end)");
        var b = LuaMod("BetaMod", "RegisterKeyBind(Key.F5, {ModifierKey.ALT}, function() end)");

        Assert.Empty(_checker.CheckConflicts(new[] { a, b }));
    }

    /// Commented-out registrations are not registrations. A mod that removed a command three
    /// revisions ago must not still be blamed for it.
    [Fact]
    public void Commented_out_registrations_are_ignored()
    {
        var a = LuaMod("AlphaMod", """
            -- RegisterConsoleCommandHandler("ghost.cmd", function() end)
            --[[ RegisterKeyBind(Key.F11, function() end) ]]
            RegisterConsoleCommandHandler("real.cmd", function() end)
            """);
        var b = LuaMod("BetaMod", """
            RegisterConsoleCommandHandler("ghost.cmd", function() end)
            RegisterKeyBind(Key.F11, function() end)
            """);

        Assert.Empty(_checker.CheckConflicts(new[] { a, b }));
    }

    /// Two rows sharing one folder is the filesystem case: one copy of the files, one
    /// mods.txt entry, and the later install physically overwrote the earlier.
    [Fact]
    public void Two_mods_in_the_same_folder_is_a_critical_clash()
    {
        var a = LuaMod("SameFolder", "RegisterConsoleCommandHandler('a.cmd', function() end)");
        var b = new ModInfo
        {
            Name = "SameFolderOther",
            Type = ModType.LuaMod,
            IsInstalled = true,
            IsEnabled = true,
            InstallPath = a.InstallPath,          // the collision
            InstallFiles = new List<string> { a.InstallPath },
            ContainedAssetPaths = new List<string> { "Scripts/main.lua" },
            InstalledAt = a.InstalledAt.AddMinutes(5)
        };

        var pair = Assert.Single(_checker.CheckConflicts(new[] { a, b }));
        Assert.Equal(ConflictKind.ModFolderNameClash, pair.Kind);
        Assert.Equal(ConflictSeverity.Critical, pair.Severity);
    }

    /// A disabled mod loads nothing, so it cannot contest anything.
    [Fact]
    public void A_disabled_lua_mod_does_not_conflict()
    {
        var a = LuaMod("AlphaMod", "RegisterConsoleCommandHandler('shared.cmd', function() end)");
        var b = LuaMod("BetaMod", "RegisterConsoleCommandHandler('shared.cmd', function() end)");
        b.IsEnabled = false;

        Assert.Empty(_checker.CheckConflicts(new[] { a, b }));
    }

    /// A pair clashing on BOTH a command and a key is folded into one card by the existing
    /// per-pair merge, which keeps only one Kind. The card has to describe both halves - it
    /// previously said "1 console command" above a list headed "Contested registrations (2)".
    [Fact]
    public void A_card_covering_both_a_command_and_a_key_describes_both()
    {
        var a = LuaMod("AlphaMod", """
            RegisterConsoleCommandHandler("shared.cmd", function() end)
            RegisterKeyBind(Key.F7, function() end)
            """);
        var b = LuaMod("BetaMod", """
            RegisterConsoleCommandHandler("shared.cmd", function() end)
            RegisterKeyBind(Key.F7, function() end)
            """);

        var pair = Assert.Single(_checker.CheckConflicts(new[] { a, b }));

        Assert.Equal(2, pair.AssetPaths.Count);
        Assert.Contains("console command", pair.Summary);
        Assert.Contains("key", pair.Summary);
        Assert.False(pair.ShowsWinner, "UE4SS resolves a duplicate keybind the opposite way to the pak rule");
    }

    /// A registry path is JSON on disk, so it is external input. GetFullPath THROWS on an
    /// embedded NUL rather than returning something a containment check could reject, and
    /// CheckConflicts runs synchronously on the UI thread - so the throw escaped as an
    /// unhandled-exception dialog with the panel never updating.
    [Fact]
    public void A_registry_path_with_an_invalid_character_does_not_throw()
    {
        var a = LuaMod("AlphaMod", "RegisterConsoleCommandHandler('a.cmd', function() end)");
        var b = LuaMod("BetaMod", "RegisterConsoleCommandHandler('b.cmd', function() end)");
        b.ContainedAssetPaths = new List<string> { "Scripts/ma\0in.lua" };

        var ex = Record.Exception(() => _checker.CheckConflicts(new[] { a, b }));
        Assert.Null(ex);
    }

    /// Same reasoning, for the traversal case the containment check was written for.
    [Fact]
    public void A_registry_path_escaping_the_mod_folder_is_not_followed()
    {
        var a = LuaMod("AlphaMod", "RegisterConsoleCommandHandler('shared.cmd', function() end)");
        var b = LuaMod("BetaMod", "RegisterConsoleCommandHandler('unrelated.cmd', function() end)");
        b.ContainedAssetPaths = new List<string> { "../../AlphaMod/Scripts/main.lua" };

        // If traversal were followed, BetaMod would appear to register AlphaMod's command.
        Assert.Empty(_checker.CheckConflicts(new[] { a, b }));
    }

    [Fact]
    public void One_lua_mod_alone_conflicts_with_nothing()
    {
        var a = LuaMod("AlphaMod", "RegisterConsoleCommandHandler('a.cmd', function() end)");
        Assert.Empty(_checker.CheckConflicts(new[] { a }));
    }
}
