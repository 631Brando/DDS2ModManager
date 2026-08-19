using System.Text.Json;
using System.Text.Json.Serialization;

namespace DDS2ModManager.Tests;

/// Loading a registry written by an older build.
///
/// ModInfo.UpdateSource changed from an enum ("ModActor") to an object during the merge of the
/// two independently-built update features. ModRegistryService catches deserialisation failures
/// and starts with an empty list, so a shape change that throws does not surface as an error -
/// it surfaces as every tracked mod silently disappearing from the list.
///
/// These use the same JsonSerializerOptions ModRegistryService does, so they fail if that
/// configuration ever drops the migration converter.
public class RegistryMigrationTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(), new ModUpdateSourceJsonConverter() }
    };

    /// The exact shape the previous build wrote: updateSource as an enum NAME, plus the flat
    /// fields that have since become derived.
    private const string OldFormat = """
    [
      {
        "id": "abc123",
        "name": "DriveableScooter",
        "type": "LogicMod",
        "isEnabled": true,
        "isInstalled": true,
        "installPath": "C:\\Game\\Content\\Paks\\LogicMods",
        "installFiles": ["C:\\Game\\Content\\Paks\\LogicMods\\DriveableScooter.pak"],
        "hasModActor": true,
        "modUpdateUrl": "https://github.com/631Brando/DriveableScooter",
        "updateSource": "ModActor",
        "installedVersion": "1.0.0",
        "updateAuthor": "631Brando",
        "trustedAuthor": true,
        "updateUrlChanged": false
      }
    ]
    """;

    [Fact]
    public void An_old_registry_still_loads_every_mod()
    {
        var mods = JsonSerializer.Deserialize<List<ModInfo>>(OldFormat, Options);

        Assert.NotNull(mods);
        var mod = Assert.Single(mods!);

        // The parts that must survive - this is what "the user keeps their mods" means.
        Assert.Equal("DriveableScooter", mod.Name);
        Assert.Equal(ModType.LogicMod, mod.Type);
        Assert.True(mod.IsInstalled);
        Assert.Single(mod.InstallFiles);
    }

    /// The old enum recorded only the declaration KIND - never the owner, repo or version the new
    /// type needs. Null is the honest result, and the next check re-reads it from the mod on disk.
    [Fact]
    public void The_old_enum_shaped_update_source_becomes_null_rather_than_throwing()
    {
        var mod = JsonSerializer.Deserialize<List<ModInfo>>(OldFormat, Options)!.Single();

        Assert.Null(mod.UpdateSource);
        Assert.False(mod.HasUpdateSource);
    }

    /// A stale trustedAuthor in an old file must not grant trust on load. Trust now lives in
    /// ModTrustService, keyed by account; a bool in a hand-editable registry is not a grant.
    [Fact]
    public void A_persisted_trust_flag_is_not_honoured()
    {
        var mod = JsonSerializer.Deserialize<List<ModInfo>>(OldFormat, Options)!.Single();

        // No source means no owner to trust, so this is false regardless of what the file said.
        Assert.False(mod.TrustedAuthor);
    }

    /// Older still: some builds wrote enums as ordinals.
    [Fact]
    public void An_ordinal_update_source_is_also_tolerated()
    {
        const string json = """[{ "name": "X", "type": "PatchMod", "updateSource": 2 }]""";

        var mod = JsonSerializer.Deserialize<List<ModInfo>>(json, Options)!.Single();

        Assert.Equal("X", mod.Name);
        Assert.Null(mod.UpdateSource);
    }

    /// The current shape has to round-trip, or the migration would be a one-way door.
    [Fact]
    public void The_current_shape_round_trips()
    {
        var original = new ModInfo
        {
            Name = "Current",
            Type = ModType.LogicMod,
            InstalledUpdateUrl = "https://github.com/owner/repo",
            UpdateSource = new ModUpdateSource
            {
                Declaration = ModUpdateDeclaration.BlueprintVariable,
                Owner = "owner",
                Repo = "repo",
                DeclaredUrl = "https://github.com/owner/repo",
                Version = "1.2.0"
            }
        };

        var restored = JsonSerializer.Deserialize<List<ModInfo>>(
            JsonSerializer.Serialize(new List<ModInfo> { original }, Options), Options)!.Single();

        Assert.NotNull(restored.UpdateSource);
        Assert.Equal("owner", restored.UpdateSource!.Owner);
        Assert.Equal("repo", restored.UpdateSource.Repo);
        Assert.Equal("1.2.0", restored.InstalledVersion);
        Assert.Equal(ModUpdateDeclaration.BlueprintVariable, restored.UpdateSource.Declaration);
        Assert.False(restored.UpdateUrlChanged);
    }

    /// The user's declared Nexus link has to survive a restart, through a REAL ModRegistryService.
    ///
    /// This is the one test that catches the two silent ways to lose it: declaring NexusModLink's
    /// members as fields (System.Text.Json ignores public fields unless IncludeFields is set, and
    /// the registry's options do not set it, so the record would round-trip as {}), and copying a
    /// [property: JsonIgnore] onto ModInfo.NexusLink from the runtime-only fields beside it. Both
    /// leave the link working all session and gone next launch, with no error anywhere.
    [Fact]
    public void A_declared_nexus_link_survives_a_round_trip_through_the_registry()
    {
        var path = Path.Combine(Path.GetTempPath(), "dds_reg_" + Guid.NewGuid().ToString("N")[..8] + ".json");

        try
        {
            var registry = new ModRegistryService(path);
            registry.Upsert(new ModInfo
            {
                Name = "AERR",
                Type = ModType.DllPlugin,
                NexusLink = new NexusModLink
                {
                    ModId = 79, GameDomain = "drugdealersimulator", Kind = NexusLinkKind.Linked
                }
            });

            // The enum must be written by NAME. ModRegistryService passes a JsonStringEnumConverter;
            // if that ever goes, the value becomes a pinned ordinal and appending a member remaps
            // every link on disk - the ModType hazard, arriving somewhere new.
            Assert.Contains("\"Linked\"", File.ReadAllText(path));

            var restored = new ModRegistryService(path).Mods.Single();

            Assert.NotNull(restored.NexusLink);
            Assert.Equal(79, restored.NexusLink!.ModId);
            Assert.Equal("drugdealersimulator", restored.NexusLink.GameDomain);
            Assert.Equal(NexusLinkKind.Linked, restored.NexusLink.Kind);
            Assert.True(restored.HasExplicitNexusLink);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    /// Every registry already on a user's disk has no such member. It must load as "no link", not
    /// as an empty one - the registry has no schema and degrades by dropping unknown members, so a
    /// downgrade is silent in the other direction too.
    [Fact]
    public void A_registry_written_before_links_existed_loads_with_no_link()
    {
        const string json = """
        [ { "Id": "abc", "Name": "Old", "Type": "LogicMod", "IsEnabled": true } ]
        """;

        var restored = JsonSerializer.Deserialize<List<ModInfo>>(json, Options)!.Single();

        Assert.Null(restored.NexusLink);
        Assert.False(restored.HasExplicitNexusLink);
        Assert.Equal("Old", restored.Name);
    }
}
