namespace DDS2ModManager.Tests;

/// Telling a two-part mod apart from two variants of one mod.
///
/// Get this wrong in one direction and the player is asked to choose between "UE4SSMods" and
/// "LogicMods" and installs half a mod. Get it wrong in the other and an x2/x5/x10 archive
/// silently installs all three multipliers at once. Both directions are tested.
public class ArchiveLayoutTests : IDisposable
{
    private readonly List<string> _temp = new();

    private string NewRoot()
    {
        var d = Path.Combine(Path.GetTempPath(), "DDS2MMLayout_" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(d);
        _temp.Add(d);
        return d;
    }

    public void Dispose()
    {
        foreach (var d in _temp) try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }
    }

    private static void Touch(string path, string content = "x")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// The layout this project's own releases ship.
    [Fact]
    public void Two_destination_archive_is_two_parts()
    {
        var root = NewRoot();
        Touch(Path.Combine(root, "UE4SSMods", "MyMod", "Scripts", "main.lua"), "-- lua");
        Touch(Path.Combine(root, "LogicMods", "MyMod", "MyMod.pak"));
        Touch(Path.Combine(root, "INSTALL.txt"));

        var parts = ModArchiveLayoutService.DetectParts(root);

        Assert.Equal(2, parts.Count);
        Assert.Contains(parts, p => ModArchiveLayoutService.KindOf(p) == ModType.LuaMod);
        Assert.Contains(parts, p => ModArchiveLayoutService.KindOf(p) == ModType.LogicMod);
    }

    /// The game-root-relative form, which the older Nexus packages use.
    [Fact]
    public void Game_root_relative_layout_is_detected()
    {
        var root = NewRoot();
        Touch(Path.Combine(root, "Content", "Paks", "LogicMods", "MyMod", "MyMod.pak"));
        Touch(Path.Combine(root, "Binaries", "Win64", "ue4ss", "Mods", "MyMod_Lua", "Scripts", "main.lua"), "-- lua");

        Assert.Equal(2, ModArchiveLayoutService.DetectParts(root).Count);
    }

    /// The regression that matters most: making destination layouts work must not cost the
    /// variant picker, or players silently get every damage multiplier at once.
    [Fact]
    public void Variant_archive_is_not_multi_destination()
    {
        var root = NewRoot();
        foreach (var v in new[] { "x2", "x5", "x10" })
            Touch(Path.Combine(root, v, "Mod.pak"));

        Assert.Empty(ModArchiveLayoutService.DetectParts(root));
        Assert.Equal(3, ModVariantDetectionService.DetectCandidates(root).Count);
    }

    [Fact]
    public void A_plain_single_mod_is_untouched()
    {
        var root = NewRoot();
        Touch(Path.Combine(root, "Simple.pak"));

        Assert.Empty(ModArchiveLayoutService.DetectParts(root));
        Assert.Single(ModVariantDetectionService.DetectCandidates(root));
    }

    /// One destination folder is a normal mod that happens to sit in a named folder. Requiring
    /// two also means a single stray folder called "Mods" cannot hijack an install.
    [Fact]
    public void A_single_destination_folder_is_not_a_multi_part_archive()
    {
        var root = NewRoot();
        Touch(Path.Combine(root, "LogicMods", "MyMod", "MyMod.pak"));

        Assert.Empty(ModArchiveLayoutService.DetectParts(root));
    }
}
