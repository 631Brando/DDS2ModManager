namespace DDS2ModManager.Tests;

/// Reading a mod's declared update source from a .dds2mod.json.
///
/// The important cases are the refusals: a manifest is a file inside a mod, so it is attacker-
/// controlled in exactly the way a mod is, and a corrupt one must not take the manager down.
public class ManifestReadingTests : IDisposable
{
    private readonly List<string> _temp = new();

    private string Dir(params string[] parts)
    {
        var d = Path.Combine(Path.GetTempPath(), "DDS2MMTest_" + Guid.NewGuid().ToString("N")[..10]);
        if (parts.Length > 0) d = Path.Combine(new[] { d }.Concat(parts).ToArray());
        Directory.CreateDirectory(d);
        _temp.Add(d);
        return d;
    }

    public void Dispose()
    {
        foreach (var d in _temp)
        {
            // Walk up to the generated root so nested fixtures are removed too.
            var root = d;
            while (Path.GetFileName(root)?.StartsWith("DDS2MMTest_") == false && Path.GetDirectoryName(root) != null)
                root = Path.GetDirectoryName(root)!;
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void No_manifest_is_not_an_error()
    {
        var d = Dir();
        Assert.Equal(ModUpdateSource.None, ModUpdateSourceReader.ReadFromManifest(d).Source);
    }

    [Fact]
    public void Reads_a_nested_manifest()
    {
        var root = Dir();
        var nested = Path.Combine(root, "Scripts");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "MyMod" + ModUpdateSourceReader.ManifestSuffix),
            """{ "modUpdateUrl": "https://github.com/mifsopo1/MifBridge", "version": "1.4.0" }""");

        var d = ModUpdateSourceReader.ReadFromManifest(root);
        Assert.Equal(ModUpdateSource.Manifest, d.Source);
        Assert.Equal("https://github.com/mifsopo1/MifBridge", d.UpdateUrl);
        Assert.Equal("1.4.0", d.Version);
    }

    [Fact]
    public void Property_names_are_case_insensitive()
    {
        var root = Dir();
        File.WriteAllText(Path.Combine(root, "a" + ModUpdateSourceReader.ManifestSuffix),
            """{ "ModUpdateUrl": "https://github.com/a/b", "Version": "2.0" }""");
        Assert.Equal("https://github.com/a/b", ModUpdateSourceReader.ReadFromManifest(root).UpdateUrl);
    }

    [Fact]
    public void A_non_github_url_is_refused()
    {
        var root = Dir();
        File.WriteAllText(Path.Combine(root, "evil" + ModUpdateSourceReader.ManifestSuffix),
            """{ "modUpdateUrl": "https://evil.example.com/payload.zip", "version": "9.9" }""");
        Assert.Equal(ModUpdateSource.None, ModUpdateSourceReader.ReadFromManifest(root).Source);
    }

    [Fact]
    public void Malformed_json_is_survivable()
    {
        var root = Dir();
        File.WriteAllText(Path.Combine(root, "x" + ModUpdateSourceReader.ManifestSuffix), "{ this is not json");
        Assert.Equal(ModUpdateSource.None, ModUpdateSourceReader.ReadFromManifest(root).Source);
    }

    // ---- installed mods: the shared-folder hazard -----------------------------------------

    [Fact]
    public void A_lua_mod_owns_its_folder_so_any_manifest_inside_is_its_own()
    {
        var root = Dir();
        Directory.CreateDirectory(Path.Combine(root, "Scripts"));
        File.WriteAllText(Path.Combine(root, "anything" + ModUpdateSourceReader.ManifestSuffix),
            """{ "modUpdateUrl": "https://github.com/owner/luamod", "version": "1.0" }""");

        var mod = new ModInfo { Name = "LuaMod", InstallPath = root, InstallFiles = new List<string> { root } };
        Assert.Equal("https://github.com/owner/luamod", ModUpdateSourceReader.ReadForInstalledMod(mod).UpdateUrl);
    }

    /// Pak mods all share Content\Paks\LogicMods. Claiming a neighbour's manifest would mean
    /// offering a player an update from a stranger's repository.
    [Fact]
    public void A_pak_mod_does_not_adopt_a_neighbours_manifest()
    {
        var shared = Dir();
        File.WriteAllText(Path.Combine(shared, "NeighbourMod" + ModUpdateSourceReader.ManifestSuffix),
            """{ "modUpdateUrl": "https://github.com/someone-else/theirmod", "version": "9.9" }""");

        var mod = new ModInfo
        {
            Name = "MyPakMod",
            InstallPath = shared,
            InstallFiles = new List<string> { Path.Combine(shared, "MyPakMod.pak") }
        };

        Assert.Equal(ModUpdateSource.None, ModUpdateSourceReader.ReadForInstalledMod(mod).Source);
    }

    [Fact]
    public void A_pak_mod_does_use_its_own_name_matched_manifest()
    {
        var shared = Dir();
        File.WriteAllText(Path.Combine(shared, "MyPakMod" + ModUpdateSourceReader.ManifestSuffix),
            """{ "modUpdateUrl": "https://github.com/owner/mypakmod", "version": "2.0" }""");

        var mod = new ModInfo
        {
            Name = "MyPakMod",
            InstallPath = shared,
            InstallFiles = new List<string> { Path.Combine(shared, "MyPakMod.pak") }
        };

        Assert.Equal("https://github.com/owner/mypakmod", ModUpdateSourceReader.ReadForInstalledMod(mod).UpdateUrl);
    }

    [Fact]
    public void A_missing_install_folder_is_survivable()
    {
        var mod = new ModInfo { Name = "Gone", InstallPath = Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid()) };
        Assert.Equal(ModUpdateSource.None, ModUpdateSourceReader.ReadForInstalledMod(mod).Source);
    }
}
