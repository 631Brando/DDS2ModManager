# Architecture decision: supporting Drug Dealer Simulator 1

**Status:** Implemented on `feat/multi-game-dds1`. All functional steps are done; 284 tests passing.
**Date:** 2026-08-18

Landed: `GameProfile` + `GameProfiles` registry; a profile-aware `GameInstallation`; multi-game
detection; per-game state (registry, disabled mods, history, backups, profiles) with a one-time
migration; `AppSettings` split into app-wide and per-game halves; a single `GameMountService.MountOptions`;
UE4-correct pak handling and an independent base-pak deletion guard; a per-file save header probe plus
DDS1's second save root; loader detection with an install gate; the game tab strip; and the loose
`.uasset` mod type. Deferred by decision: the structural rename (repository, assembly, `%AppData%`,
release asset) — only the visible name changed, to "DDS Mod Manager".

See [dds1-implementation-plan.md](dds1-implementation-plan.md) for the step-by-step sequencing this
followed and the ranked risk list it was built around.

## The question

Should DDS1 support be a **separate fork/repo**, or a **game switcher inside this app**?

## Decision

**One app, multi-game.** Do not fork.

---

## Evidence

### The two games are near-identical modding targets

Verified by inspecting both installs on disk, parsing file headers, and probing CUE4Parse against the real
DDS1 pak — not from forum claims.

|                 | DDS1                                              | DDS2                                            |
|-----------------|---------------------------------------------------|-------------------------------------------------|
| Engine          | UE **4.21.0** (CL 4753647)                        | UE **5.3.2**                                    |
| CUE4Parse EGame | **`GAME_UE4_21`**                                 | `GAME_UE5_3`                                    |
| Steam App ID    | `682990`                                          | `1708850`                                       |
| Steam folder    | `common/DrugDealerSimulator`                      | `common/Drug Dealer Simulator 2`                |
| UE project dir  | `DrugDealerSimulator/`                            | `DrugDealerSimulator2/`                         |
| Paks            | one `.pak`, v7, **unencrypted**, no IoStore       | `.pak`+`.ucas`+`.utoc` (IoStore), `global.utoc` |
| usmap           | **not needed** (versioned properties)             | required                                        |
| Config dir      | `Saved/Config/WindowsNoEditor`                    | `Saved/Config/Windows`                          |
| GVAS saves      | `Saved\SaveGames\` (slot index + settings only)   | `Saved\SaveGames\Cartels\<name>\`               |
| Real saves      | **`Saved\Serialized\saveSlot-N.save`** (RamaSave) | RamaSave `_Progress.save`                       |
| Public loader   | **UnrealModLoader + UnrealModUnlocker**           | UE4SS `experimental-latest`                     |
| LogicMods       | `Content\Paks\LogicMods\`                         | `Content\Paks\LogicMods\`                       |
| Loose `.uasset` | **`Content\<Category>\` — most DDS1 mods**        | impossible (IoStore)                            |

Same Paks folder, same `LogicMods` convention, same `ModActor.uasset` marker, same RamaSave save container,
same `.ini` config. **Nearly every difference is a value, not a different mechanism** — the two real
exceptions are the loose-`.uasset` mod type and the loader backend, both called out below.

Cheap engine tell for any UE game: DDS1 has `UE4CC-` crash folders and `Config/WindowsNoEditor`; DDS2 has
`UECC-` and `Config/Windows`.

> **Engine-version trap — read this before touching `EGame`.** DDS1's install contains a file named
> `DrugDealerSimulator-4.27.2-...usmap`, and its `UE4SS.log` reports "engine version: 4.27". **Both are
> artifacts of a manual `[EngineVersionOverride]` in `UE4SS-settings.ini`.** The real version is **4.21.0**,
> proven three independent ways: the exe's UTF-16 build string `++UE4+Release-4.21-CL-4753647`; the GVAS
> engine stamp in `graphicsSettingsFull.sav`; and **pak version 7** (v8 arrived in 4.22, so v7 proves <= 4.21).
> That usmap is spoofed and must not be shipped or used.

### The biggest technical risk is cleared

DDS1's pak footer parses as version 7, `bEncryptedIndex = 0`, null encryption key GUID — **CUE4Parse opens it
with no AES key.** Probed against the exact CUE4Parse version this project references (`1.2.2.202607`) on the
real 11 GB pak:

```
GAME_UE4_21 : 35955 files, deserialize ok=25 fail=0   (no usmap attached)
GAME_UE5_3  : 35955 files, deserialize ok=0  fail=25  (Invalid FString length)
```

Both settings *list* all 35,955 files — the pak index is version-agnostic. **Only deserialization proves the
setting is right**; validation that merely counts paths will silently pass with the wrong `EGame`.

### The codebase is already ~70% game-agnostic

Roughly **11,000 of the 15,067 C# lines need no logic change**. Already parameterized or generic:
`SteamVdf`, `GameMountService` (already takes `paksPath, mappingsPath, egame, aesKeyHex`), `OodleHelper`,
`GvasNameRewriter`, `SaveGameService`, `ShortcutCreator`, `NexusIndexService`/`NexusFeedService`,
`CompatibilityCheckerService`, `ModArchiveLayoutService`.

`GameInstallation` already detects the UE project folder *from disk* rather than hardcoding it, and its own
comment says this was done so paths "resolve correctly if this is ever pointed at another UE game". Only two
of its members are DDS2-specific: `UE4SSRootPath` (line 36) and `ConfigPath` (line 46).

`Dds2SaveRules.Applies(game)` is an existing, deliberate per-game quarantine — a runtime gate on
`ProjectName` so that pointing the manager at another Unreal title runs none of it.

### It is worth doing on demand grounds

| | DDS1 | DDS2 |
|---|---|---|
| Nexus slug | `drugdealersimulator` | `drugdealersimulator2` |
| Mods | **183** | 98 |
| Activity | uploads within the last day | last upload ~6 days prior |

DDS1 has **1.9x DDS2's mod count despite being six years older**, and its best available manager is a
menu-driven `.bat` script (last updated Apr 2024, 1,269 downloads) with no conflict detection, no
enable/disable, no pak introspection and no update checking. The community guide warns that mods touching the
same file "need to be manually merged" — exactly what `CompatibilityCheckerService` and
`DataTableAppendScanner` already solve automatically.

### Why not fork

1. ~15k lines maintained twice — every Nexus API change, loader change, and bug fix done in two places.
2. Contradicts the standing rule: *never invent parallel systems that do the same thing; find the existing
   one and extend it*.
3. **Two installed apps would collide on Windows state** — the same shell verb under
   `HKCU\Software\Classes\{.zip,.7z,.rar}\shell\DDS2ModManager` and the same uninstall key. A fork has to be
   rebranded anyway, so it buys nothing.

---

## Design

Add a `GameProfile` beside `GameInstallation`. Today `GameInstallation` is a *path bundle*; it is not a
*game identity*. Those fields currently live as `const`s scattered across `GameDetectionService`,
`MainViewModel`, `TrustedModsWindow`, `AppSettings`, `UE4SSManagerService`, `Dds2SaveRules`, and
`GameVersionWatchService`.

```
GameProfile
  Id                  "dds1" | "dds2"          // key for all per-game state
  DisplayName         "Drug Dealer Simulator"  // UI
  SteamAppId          682990 | 1708850         // steam://rungameid
  SteamFolderName     "DrugDealerSimulator" | "Drug Dealer Simulator 2"
  ProjectFolderName   fallback only; still detected from disk first
  ConfigPlatformDir   "WindowsNoEditor" | "Windows"
  EngineVersion       GAME_UE4_21 | GAME_UE5_3
  NeedsMappings       false | true
  SaveRoots           [Saved\SaveGames, Saved\Serialized] | [Saved\SaveGames]
  PakLayout           SinglePak | IoStoreTriple
  SupportsLooseAssets true | false
  LoaderBackend       UnrealModLoader+Unlocker | UE4SS(experimental-latest)
  NexusDomain         "drugdealersimulator" | "drugdealersimulator2"
  SaveRules           strategy object, generalizing Dds2SaveRules
```

**Per-game state keying.** `ModRegistryService` already does this correctly —
`SHA256(game.RootPath.ToLower())[..12]` in the filename — and `SaveGameService`'s `DisabledSaves\<hash>\`
copies it, citing the registry as precedent. That is the house pattern. Apply it to the stores that lack it
rather than inventing a second scheme.

---

## Work breakdown

**1 — Mechanical, large but trivial.** Behind constants, then find-and-replace:
- 14x the `%AppData%\DDS2ModManager` literal, no shared constant today
- 7x a duplicated `EGame.GAME_UE5_3` fallback
- 5x a duplicated mappings-resolution ternary
- 2x an independently-declared Nexus domain (`MainViewModel.cs:76`, `TrustedModsWindow.xaml.cs:58`)

**2 — Structural (~2-3 days, the real cost).**
- `AppSettings` gains a per-game section. ~10 fields collide today: `GamePathOverride`, `EGameVersion`,
  `MappingsOverridePath`, `AesKeyHex`, the three `LastSeenGame*` version-watch fields, and the two Nexus
  timestamps. (`ModListSortColumn`, window size etc. stay global — correctly.)
- `AppSettingsService` must stop being a `Lazy<>` singleton with a private parameterless constructor.
- `ModHistoryService`, `ModBackupService`, `ModProfileService` and `DisabledMods` gain game-hash scoping.

**3 — Data model.** Introduce `GameProfile`; generalize `Dds2SaveRules` into a strategy.

**4 — Small surgical ports.**
- `RamaSaveReader`: UE4 saves have **no `PackageVersionUE5` int32**, so everything from offset 12 shifts by
  4 bytes. One branch, not a rewrite. (`RamaSaveReader.cs:42`'s `0x9E2A83C1` tag and `format = 7` are identical.)
- `GameConfigService`: `Windows` -> `WindowsNoEditor`.
- A second save root: DDS1's real saves are in `Saved\Serialized\`, not `Saved\SaveGames\`.
- `ModInstallerService`: a single-`.pak` install path with no `.ucas`/`.utoc` sibling.

**5 — The one genuinely new feature: loose `.uasset` mods.** DDS1's fourth mod type, enabled by
UnrealModUnlocker, installs loose assets into `Content\<Category>\` (DataTables, Drugs, WorldControlers...).
**Most DDS1 mods ship this way** and the manager models nothing like it. Needs its own install/uninstall and
file-level overwrite tracking against the base pak's 32,316 assets — which CUE4Parse can enumerate, so
conflict detection is genuinely achievable here. Additive rather than disruptive, but it is real feature work.

**6 — UI. A tab strip at the very top.** Decided: a real tab at the top of the main window, an unmistakable
DDS1/DDS2 discriminator — not a subtle header combo. Switching rebinds the whole game context (registry,
analyzer, installer, mod list, conflicts, Nexus domain, saves and config).

The implementation caveat still stands and shapes *how*, not *whether*: a naive `TabControl` holding two
copies of the full mod-list view doubles the visual tree and every binding. Prefer tab **headers** over a
single shared content host, so only the bound context changes. Only detected games get a tab, and the strip
must not clutter the window for someone who owns just one game.

---

## Traps

- **The 4.27 usmap in the DDS1 install is spoofed** (see above). DDS1 needs no usmap at all — the mappings
  machinery becomes a harmless no-op, which `GameMountService` already tolerates since it try/catches the
  mappings load and only warns.
- `GameVersionWatchService.cs:19` hardcodes the full exe path and **bypasses `GameInstallation.ProjectName`
  entirely** — easy to miss because everything around it is already abstracted.
- `MappingsProviderService` resolves the embedded usmap by `.EndsWith("mappings.usmap")` (first match wins)
  and extracts to one fixed filename. Two games would silently overwrite each other's mappings.
- ~~`NexusIndexService` single cache file~~ — FIXED: caches per domain (`nexus-index.<domain>.cache.json`).
- ~~`DisabledMods` is flat~~ — FIXED: hashed per install via `AppPaths.DisabledModsForKey`.

- **`Content\Paks\DisabledMods` disables NOTHING.** Different folder, different problem, still live for
  anyone who created it by hand: Unreal enumerates `Content\Paks` recursively
  (`FPakPlatformFile::FindPakFilesInDirectory` -> `IterateDirectoryRecursively`), so a pak parked there
  mounts and loads normally. The only in-place hand-disable that works is renaming the file so it no
  longer ends in `.pak`. The manager now says so, and importing such a mod moves its files out of the
  game folder for real.

- **UnrealModLoader scans `LogicMods` FLAT**, with a non-recursive iterator, and that scan is the sole
  producer of its mod list. A logic mod in a per-mod subfolder still *mounts* but its `ModActor` never
  spawns — assets present, logic dead, nothing logged. Gated on `GameProfile.LogicModsUseSubfolders`, in
  the install path **and** the enable path; fixing only one re-breaks a working mod on the next toggle.

- **In a WPF `ControlTemplate`, visual state belongs in `ControlTemplate.Triggers` with `TargetName`.**
  A property set as an attribute in the template markup (`BaseValueSource.ParentTemplate`) outranks a
  `<X.Style>` trigger (`StyleTrigger`), so the trigger is silently discarded. This is what made the game
  tabs switch correctly while looking completely inert.
- Branded strings are written into *the game's own folders*, not just the app's: `.dds2mm.bak` config
  backups, `.dds2modmanager_manifest.json` in `ue4ss\`, `__DDS2MM_analyze__` staging in `Content\Paks`.
- `SettingsWindow.xaml`'s EGame dropdown offers only `GAME_UE5_0`..`GAME_UE5_5` — **no valid entry for a UE4
  title**. Better answer: drive it from the profile and keep the override for emergencies.
- Validating an `EGame` guess by *listing* pak paths proves nothing (see the probe above).

## Compatibility constraints — do not break

- `ModUpdateSourceResolver`'s `ModUpdateUrl` / `ModVersion` / `ModAuthor` Blueprint variable names are fixed
  by agreement with the published SDK's ModActor template. Renaming breaks every mod already shipped.
- `.dds2mod.json` (`ModManifest.FileName`) is likewise author-facing and already in the wild.
- v1.2.0 is published; existing installs self-update from `github.com/631Brando/DDS2ModManager`.

Any rename must keep these reading the old names, whatever new ones get added.

## Decisions taken

All three open questions were answered by Andre on 2026-08-18:

1. **Rename to `DDSModManager`.** Approved. Affects the self-update source, `%AppData%` folder, shell
   ProgIDs, shortcuts and the uninstall key. GitHub permanently redirects renamed repos so self-update
   survives, but local state needs a one-time migration, and the release **asset name** change
   (`DDS2ModManager.exe` -> `DDSModManager.exe`) has to be checked against clients running older builds.
   Highest-risk item in the whole project.
2. **Support whichever loader is installed.** Not one or the other — detect UE4SS (both the modern
   `ue4ss\` layout and DDS1's legacy `Binaries\Win64` layout), UnrealModLoader, and UnrealModUnlockerBasic,
   and offer the right one per game. `GameProfile.SupportedLoaders` declares what is plausible per game;
   what is actually present is resolved at runtime against the install.
3. **Tabs at the very top** — see the UI item above.

## Amendment, 2026-08-18: a fifth mod type

The breakdown above counted four DDS1 mod shapes. There is a fifth, found only when a real mod
(AE Revolutions Reloaded) was refused with *"couldn't determine mod type for this archive"*: a **native
DLL plugin**. The archive holds a `.dll`, a guide, and a folder of JSON content — no pak, no lua, no
cooked assets. The refusal was correct behaviour for a shape the model did not describe; the gap was
that the shape was not modelled at all.

`ModType.DllPlugin = 5`, appended (see R12 — these are serialised as integers).

**The destination is loader-specific and there is no shared convention.** UnrealModUnlocker loads from
`Binaries\Win64\UnrealModPlugins`; UnrealModLoader loads from `coremods`; UE4SS loads Lua mods, not
arbitrary native DLLs, so it has no plugin folder at all. So `ModLoaderInstallation.PluginFolder` is
nullable, resolved per detected loader, and **null is a refusal reason, not a fallback** — placing a
native DLL somewhere the game never reads is indistinguishable, from the user's side, from the mod
being broken.

**Only the DLLs are placed.** These frameworks create a data folder next to their own DLL on first
launch and read settings and content from it; where a bundled example pack goes inside that folder is
described only by the mod's own docs. Guessing is as likely to land one level off as right, so the
remaining archive contents are named in a warning rather than copied somewhere plausible. Uninstall
deletes only the DLLs the manager placed — the data folder holds the user's own settings.

Gated on `GameProfile.SupportsDllPlugins`: true for DDS1, false for DDS2, where UE4SS is the extension
mechanism and a loose DLL has nothing to load it.

## Amendment, 2026-08-18: identifying a mod on Nexus

Name matching reaches 13 of 26 DDS2 mods and 0 of 9 DDS1 ones, and the shortfall is almost entirely
unpublished local work rather than a defect. The one genuine failure, AERR, is unreachable by any
name: it is published as "AE Revolutions Reloaded". The fix is a user-declared link that outranks
matching, not a looser matcher — full reasoning, precedence rules and the rejected alternatives are
in `docs/nexus-identification.md`.

## Still open

- Whether DDS1 should eventually be nudged toward UE4SS as well. It *does* run there (a working v3.0.1 with
  15 Lua mods is installed on this machine) and would share far more code, but it wants the `LessEqual421`
  build definition for correct container alignment on UE<=4.21 and no prebuilt asset ships for that. Not a
  blocker: detection covers the scene as it exists today.
