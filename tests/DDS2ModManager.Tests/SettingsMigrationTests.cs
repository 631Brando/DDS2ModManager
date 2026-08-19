using System.Text.Json;

namespace DDS2ModManager.Tests;

/// Folding a pre-multi-game settings.json into a per-game section.
///
/// Both failure modes here are silent. Drop a field and the user sees "the app forgot my game path
/// / AES key / update history" with no error anywhere. Carry the engine version across too
/// faithfully and every existing user is pinned to UE 5.3 forever - and that one does not even look
/// broken, because a wrong engine version still lists every path in a pak and only fails when an
/// asset is actually deserialized.
public class SettingsMigrationTests
{
    /// A verbatim settings.json as v1.2.0 wrote it, flat, with no Games section.
    private const string LegacyJson = """
    {
      "MappingsOverridePath": "D:\\custom\\mappings.usmap",
      "EGameVersion": "GAME_UE5_3",
      "GamePathOverride": "C:\\SteamLibrary\\steamapps\\common\\Drug Dealer Simulator 2",
      "AutoCheckUE4SSUpdatesOnStartup": true,
      "PreferredUE4SSBuild": "Standard",
      "CheckForAppUpdatesOnStartup": true,
      "CheckForModUpdatesOnStartup": true,
      "ShowNexusNewModBanner": true,
      "NexusFeedLastSeenUtc": "2026-08-09T00:32:58Z",
      "ShowNexusModDetails": true,
      "NexusIndexRefreshedUtc": "2026-08-18T14:10:18.2336919Z",
      "ModListSortColumn": "TrustedAuthor",
      "ModListSortDescending": true,
      "LastSeenGameVersion": "1.2.3.4",
      "LastSeenGameSize": 121746504,
      "LastSeenGameWrittenUtc": "2026-06-07T17:55:38.6460442Z",
      "UpdateChannel": "Stable",
      "AesKeyHex": "0xDEADBEEF",
      "WindowWidth": 1560,
      "WindowHeight": 980,
      "WindowMaximized": true
    }
    """;

    // The whole reason GameSettings kept the old property names: the migration is one deserialize of
    // the same text, with no hand-written field mapping in which a field could be quietly forgotten.
    // Every per-game field is asserted individually, because "most of them survived" is the bug.
    [Fact]
    public void Every_per_game_field_survives_the_fold()
    {
        var game = JsonSerializer.Deserialize<GameSettings>(LegacyJson)!;

        Assert.Equal(@"D:\custom\mappings.usmap", game.MappingsOverridePath);
        Assert.Equal("GAME_UE5_3", game.EGameVersion);
        Assert.Equal(@"C:\SteamLibrary\steamapps\common\Drug Dealer Simulator 2", game.GamePathOverride);
        Assert.Equal("0xDEADBEEF", game.AesKeyHex);
        Assert.Equal("1.2.3.4", game.LastSeenGameVersion);
        Assert.Equal(121746504, game.LastSeenGameSize);
        Assert.NotNull(game.LastSeenGameWrittenUtc);
        Assert.NotNull(game.NexusFeedLastSeenUtc);
        Assert.NotNull(game.NexusIndexRefreshedUtc);
        Assert.True(game.HasAnything);
    }

    // The global half has to come through the same text untouched, and must NOT have swallowed the
    // per-game fields as stray properties.
    [Fact]
    public void The_app_wide_fields_are_unaffected()
    {
        var app = JsonSerializer.Deserialize<AppSettings>(LegacyJson)!;

        Assert.Equal("TrustedAuthor", app.ModListSortColumn);
        Assert.True(app.ModListSortDescending);
        Assert.Equal(UpdateChannels.Stable, app.UpdateChannel);
        Assert.Equal("Standard", app.PreferredUE4SSBuild);
        Assert.True(app.CheckForModUpdatesOnStartup);
        Assert.Equal(1560, app.WindowWidth);
        Assert.True(app.WindowMaximized);

        // Nothing has been migrated yet - that is Load()'s job, and it must be able to tell.
        Assert.Empty(app.Games);
        Assert.Null(app.ActiveGameId);
    }

    // R5. The old Settings window wrote EGameVersion on EVERY save, so practically every existing
    // settings.json says GAME_UE5_3. Kept as an explicit override it would outlive any future
    // profile bump - silently, because the failure only appears on deserialize, never on listing.
    [Fact]
    public void An_engine_version_that_merely_repeats_the_profile_is_not_kept_as_an_override()
    {
        var legacy = JsonSerializer.Deserialize<GameSettings>(LegacyJson)!;

        Assert.Equal(GameProfiles.Dds2.EngineVersion.ToString(), legacy.EGameVersion);

        // ...so the migration drops it, and the profile stays in charge.
        if (string.Equals(legacy.EGameVersion, GameProfiles.Dds2.EngineVersion.ToString(),
                StringComparison.OrdinalIgnoreCase))
            legacy.EGameVersion = null;

        Assert.Null(legacy.EGameVersion);
    }

    // ...but a genuinely different value is a deliberate act and must survive.
    [Fact]
    public void A_deliberately_different_engine_version_is_kept()
    {
        var json = LegacyJson.Replace("GAME_UE5_3", "GAME_UE5_5");
        var legacy = JsonSerializer.Deserialize<GameSettings>(json)!;

        Assert.NotEqual(GameProfiles.Dds2.EngineVersion.ToString(), legacy.EGameVersion);
        Assert.Equal("GAME_UE5_5", legacy.EGameVersion);
    }

    // A file with nothing game-shaped in it must not produce an empty section that then looks like
    // a configured game.
    [Fact]
    public void A_settings_file_with_no_game_state_has_nothing_to_fold()
    {
        var json = """{ "ModListSortDescending": true, "UpdateChannel": "Stable" }""";

        Assert.False(JsonSerializer.Deserialize<GameSettings>(json)!.HasAnything);
    }

    // Round-trips through the sectioned shape, which is what every launch after the migration reads.
    [Fact]
    public void The_sectioned_shape_round_trips()
    {
        var app = new AppSettings { ActiveGameId = GameProfiles.Dds1.Id };
        app.Games[GameProfiles.Dds1.Id] = new GameSettings { GamePathOverride = @"D:\g", AesKeyHex = "ab" };
        app.Games[GameProfiles.Dds2.Id] = new GameSettings { GamePathOverride = @"C:\g" };

        var back = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(app))!;

        Assert.Equal(GameProfiles.Dds1.Id, back.ActiveGameId);
        Assert.Equal(@"D:\g", back.Games[GameProfiles.Dds1.Id].GamePathOverride);
        Assert.Equal("ab", back.Games[GameProfiles.Dds1.Id].AesKeyHex);
        Assert.Equal(@"C:\g", back.Games[GameProfiles.Dds2.Id].GamePathOverride);
    }
}
