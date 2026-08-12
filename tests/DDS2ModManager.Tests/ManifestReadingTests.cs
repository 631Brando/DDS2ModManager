namespace DDS2ModManager.Tests;

/// Reading a mod's declared update source from a .dds2mod.json.
///
/// The important cases are the refusals: a manifest is a file inside a mod, so it is attacker-
/// controlled in exactly the way a mod is, and a corrupt one must not take the manager down.
public class ManifestReadingTests : IDisposable
{
    private readonly List<string> _temp = new();
    private readonly ModUpdateSourceResolver _resolver = new();

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
        Assert.Null(_resolver.FromManifestFolder(Dir(), "Whatever"));
    }

    [Fact]
    public void Reads_a_nested_manifest()
    {
        var root = Dir();
        var nested = Path.Combine(root, "Scripts");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "MyMod" + ModManifest.FileName),
            """{ "updateUrl": "https://github.com/mifsopo1/MifBridge", "version": "1.4.0" }""");

        var source = _resolver.FromManifestFolder(root, "MyMod");

        Assert.NotNull(source);
        Assert.Equal(ModUpdateDeclaration.Manifest, source!.Declaration);
        Assert.Equal("mifsopo1", source.Owner);
        Assert.Equal("MifBridge", source.Repo);
        Assert.Equal("1.4.0", source.Version);
    }

    [Fact]
    public void Property_names_are_case_insensitive()
    {
        var root = Dir();
        File.WriteAllText(Path.Combine(root, "a" + ModManifest.FileName),
            """{ "UpdateUrl": "https://github.com/a/b", "Version": "2.0" }""");

        Assert.Equal("a", _resolver.FromManifestFolder(root, "a")?.Owner);
    }

    /// Two spellings of the URL key went out in two different guides, so both have to keep
    /// working. An author who followed the older in-app guide must not silently stop publishing
    /// updates the day they update the manager.
    [Fact]
    public void The_legacy_modUpdateUrl_key_is_still_read()
    {
        var root = Dir();
        File.WriteAllText(Path.Combine(root, "old" + ModManifest.FileName),
            """{ "modUpdateUrl": "https://github.com/owner/legacy", "version": "1.0" }""");

        var source = _resolver.FromManifestFolder(root, "old");

        Assert.NotNull(source);
        Assert.Equal("owner", source!.Owner);
        Assert.Equal("legacy", source.Repo);
    }

    [Fact]
    public void A_non_github_url_is_refused()
    {
        var root = Dir();
        File.WriteAllText(Path.Combine(root, "evil" + ModManifest.FileName),
            """{ "updateUrl": "https://evil.example.com/payload.zip", "version": "9.9" }""");

        Assert.Null(_resolver.FromManifestFolder(root, "evil"));
    }

    [Fact]
    public void Malformed_json_is_survivable()
    {
        var root = Dir();
        File.WriteAllText(Path.Combine(root, "x" + ModManifest.FileName), "{ this is not json");

        Assert.Null(_resolver.FromManifestFolder(root, "x"));
    }

    /// A manifest written for a later version of the manager is refused rather than guessed at -
    /// a field that changes meaning between schema versions would otherwise be misread.
    [Fact]
    public void A_manifest_from_a_newer_schema_is_refused()
    {
        var root = Dir();
        File.WriteAllText(Path.Combine(root, "future" + ModManifest.FileName),
            $$"""{ "schema": {{ModManifest.SupportedSchema + 1}}, "updateUrl": "https://github.com/a/b" }""");

        Assert.Null(_resolver.FromManifestFolder(root, "future"));
    }

    // ---- installed mods: the shared-folder hazard -----------------------------------------

    [Fact]
    public void A_lua_mod_owns_its_folder_so_any_manifest_inside_is_its_own()
    {
        var root = Dir();
        Directory.CreateDirectory(Path.Combine(root, "Scripts"));
        File.WriteAllText(Path.Combine(root, ModManifest.FileName),
            """{ "updateUrl": "https://github.com/owner/luamod", "version": "1.0" }""");

        var mod = new ModInfo { Name = "LuaMod", InstallPath = root, InstallFiles = new List<string> { root } };

        Assert.Equal("luamod", _resolver.FromManifest(mod)?.Repo);
    }

    /// Pak mods all share Content\Paks\LogicMods. Claiming a neighbour's manifest would mean
    /// offering a player an update from a stranger's repository.
    [Fact]
    public void A_pak_mod_does_not_adopt_a_neighbours_manifest()
    {
        var shared = Dir();
        File.WriteAllText(Path.Combine(shared, "NeighbourMod" + ModManifest.FileName),
            """{ "updateUrl": "https://github.com/someone-else/theirmod", "version": "9.9" }""");

        var mod = new ModInfo
        {
            Name = "MyPakMod",
            InstallPath = shared,
            InstallFiles = new List<string> { Path.Combine(shared, "MyPakMod.pak") }
        };

        Assert.Null(_resolver.FromManifest(mod));
    }

    [Fact]
    public void A_pak_mod_does_use_its_own_name_matched_manifest()
    {
        var shared = Dir();
        File.WriteAllText(Path.Combine(shared, "MyPakMod" + ModManifest.FileName),
            """{ "updateUrl": "https://github.com/owner/mypakmod", "version": "2.0" }""");

        var mod = new ModInfo
        {
            Name = "MyPakMod",
            InstallPath = shared,
            InstallFiles = new List<string> { Path.Combine(shared, "MyPakMod.pak") }
        };

        Assert.Equal("mypakmod", _resolver.FromManifest(mod)?.Repo);
    }

    [Fact]
    public void A_missing_install_folder_is_survivable()
    {
        var mod = new ModInfo { Name = "Gone", InstallPath = Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid()) };

        Assert.Null(_resolver.FromManifest(mod));
    }
}
