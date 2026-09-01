# DDS Mod Manager — project rules

A WPF (.NET 10) mod manager for **both** Drug Dealer Simulator games, built on
[CUE4Parse](https://github.com/FabianFG/CUE4Parse). These rules are specific to this repo and add
to the global engineering standards.

## The two games are not variants of each other

|                 | DDS1                                              | DDS2                                            |
|-----------------|---------------------------------------------------|-------------------------------------------------|
| Engine          | **UE 4.21.0** (CL 4753647)                        | UE 5.3.2                                        |
| CUE4Parse EGame | `GAME_UE4_21`                                     | `GAME_UE5_3`                                    |
| Steam app id    | `682990`                                          | `1708850`                                       |
| Paks            | one `.pak`, v7, unencrypted, **no IoStore**       | `.pak`+`.ucas`+`.utoc` + `global.utoc`          |
| usmap           | **not needed** (versioned properties)             | required                                        |
| Config dir      | `Saved\Config\WindowsNoEditor`                    | `Saved\Config\Windows`                           |
| Real saves      | `Saved\Serialized\saveSlot-N.save`                | `Saved\SaveGames\Cartels\<name>\`               |
| Public loader   | UnrealModLoader + UnrealModUnlocker               | UE4SS `experimental-latest`                     |
| Loose `.uasset` | **yes — how most DDS1 mods ship**                 | impossible (IoStore)                            |
| DLL plugins     | **yes** — a native .dll + a data folder           | no (UE4SS is the extension mechanism)           |

**DDS1 is 4.21, not 4.27.** The install contains a `4.27.2` usmap and a UE4SS log claiming 4.27;
both are artifacts of a manual `[EngineVersionOverride]` in `UE4SS-settings.ini`. The exe's own
build string reads `++UE4+Release-4.21-CL-4753647`, and the pak is version 7 — v8 arrived in 4.22,
so the container format alone rules out anything newer. Do not relitigate this.

## Rules that exist because breaking them is silent

**Verify with `dotnet build DDS2ModManager.sln`, never `src/DDS2ModManager.csproj`.** The setup
project *link-compiles* shared source files rather than referencing the main project (to keep
CUE4Parse out of the installer). `src/` can build perfectly clean while the solution is broken.
Anything the setup project links — `AppPaths.cs`, `LoggingService.cs`, `GitHubReleaseService.cs`,
`ShortcutCreator.cs` — must stay dependency-free: no `Models`, no CUE4Parse.

**Never offer to install UE4SS on DDS1.** Stock and experimental UE4SS both crash it immediately;
it needs a custom `LessEqual421` build that ships no prebuilt asset. DDS2 is the mirror image — it
needs experimental specifically. `GameProfile.InstallableLoaders` is the gate and is deliberately
separate from `SupportedLoaders`: recognising a loader is not permission to install it.

**Never publish a release without a `DDS2ModManager.exe` asset.** `AppUpdateService` matches the
asset by exact filename and turns "no match" into `Succeeded=true, NewerRelease=null`, which the UI
renders as *"you're on the latest version"*. One release missing it strands every installed copy
permanently, and the fix can only ship through the channel that is broken.

**`GameInstallation.UE4SSRootPath` must never become layout-aware.** `GameResetService` deletes it
recursively. Under UE4SS's legacy layout that folder is `Binaries\Win64` itself — which holds the
game executable. `UE4SSModsPath` is layout-aware; `UE4SSRootPath` deliberately is not.

**`ModType` values are pinned.** `ModProfileService` and `ModBackupService` serialise it as an
**integer** (neither passes a `JsonStringEnumConverter`). Append only — inserting a member remaps
every saved profile and backup already on disk, silently.

**Validating an `EGame` guess by listing pak paths proves nothing.** The pak index is
version-agnostic and lists every file under the wrong setting. Only deserialisation fails.

**DDS1 has FIVE mod shapes, not three.** Pak, logic mod, lua, loose `.uasset` — and **native DLL
plugins**: a .dll dropped into a loader's plugin folder, which then creates its own data folder
beside itself on first launch. The destination is loader-specific with no shared convention —
UnrealModUnlocker reads `Binaries\Win64\UnrealModPlugins`, UnrealModLoader reads `coremods`. Resolve
it from what is installed; refuse outright when nothing present can load a DLL. Only the DLLs get
placed — a framework's data folder has a layout only its own docs describe, so it is reported, not
guessed at.

**Two halves of one mod are not two versions of it.** An archive with several installable
sibling folders is a part set only when BOTH hold: every folder name reduces to the same key
under `NexusModMatcher.KeyForInstalled`, AND every folder classifies as a different kind
(pak-bearing vs lua-bearing). Neither alone is safe — `MyMod` + `MyMod_P` where both carry a
pak passes the name test and is two alternatives, not two halves. Never use the COUNT: a
two-folder variant set is ordinary. Parts go in `DestinationParts`, never `VariantCandidates`,
because each needs its own root — one root spanning both trees hands `InstallPakTriple` a `.pak`
and a `.ucas` from different halves. See `docs/two-part-vs-variant-archives.md`.

**`Content\Paks\DisabledMods` does not disable anything.** Unreal enumerates `Content\Paks`
recursively. Never tell a user a mod parked there is switched off — the game is loading it.

**UnrealModLoader scans `LogicMods` flat.** DDS1 logic mods install flat; DDS2's go in per-mod
subfolders for UE4SS. Gated by `GameProfile.LogicModsUseSubfolders` in both install and enable.

**A fingerprint may only advance when the analysis it describes does.** `RefreshFileState` must not
re-arm on the pass that detected drift; `DeepScan` is the writer, because it is what re-read the mod.

**Nexus mod ids restart per game.** Anything keyed on a mod id — image cache, "this app" badge,
a user's declared `NexusModLink` — needs the domain too, or one game's data shows on the other's card.
**85 ids exist in both live catalogues and not one shares a title**: 79 is "AE Revolutions Reloaded"
on DDS1 and "Gh0sted - Rebalance" on DDS2. A link whose stored domain isn't the active game resolves
to nothing — never to whatever that number happens to mean here. See `docs/nexus-identification.md`.

**WPF: visual state goes in `ControlTemplate.Triggers` with `TargetName`,** never a `<X.Style>` on an
element whose properties the template already sets as attributes. Template values outrank style
triggers, so those setters are discarded in silence.

## Architecture

- **`GameProfile`** (`src/Models/GameProfile.cs`) holds everything that differs per game. The rule:
  a *value* that changes per game goes in a profile; a *mechanism* stays in the services, which are
  game-agnostic. `GameProfiles.All` is the single place a new game is added.
- **`GameInstallation`** derives every path from a detected install. It resolves the UE project
  folder *from disk*, not from the profile, so a renamed or repacked install still works.
- **`AppPaths`** is the one place this app's own storage is named. Per-game state is keyed by
  `AppPaths.GameKey(rootPath)` — `SHA256(path.ToLowerInvariant())[..12]`. **Do not change that
  algorithm**: it names files already on users' disks, and do not invent a second scheme.
- **`MainViewModel`** is a partial class across eight files, split so unrelated features don't land
  in the same 2,000-line file.

## Switching games

`ClearPerGameState()` runs *before* the new game is assigned, and it is load-bearing. The mod list,
the multi-select, the undo closure and several fire-and-forget tasks all hold references to the
outgoing game's services and file paths. `_gameContextVersion` discards background results that
land after a switch. `DetachModSubscriptions()` exists because `ObservableCollection.Clear()` raises
a Reset whose `OldItems` is null, so the collection-changed unsubscribe never runs for a Clear.

## Naming

The app displays **"DDS Mod Manager"** (`AppPaths.AppDisplayName`). The assembly, namespaces, the
`%AppData%` folder, the GitHub repository, the release asset and every registry **key** keep their
original `DDS2ModManager` names — those are identifiers existing installs depend on.
`RebrandCompatibilityTests` pins each one. `.dds2mod.json` and the `ModUpdateUrl` / `ModVersion` /
`ModAuthor` Blueprint variables are frozen by agreement with the published SDK.

## Docs

- `docs/multi-game-architecture.md` — the decision, and why a fork was rejected.
- `docs/dds1-implementation-plan.md` — the reconciled 12-step sequencing and its risk ranking.
- `MODDING.md` — author-facing: how a mod opts into update checking.
- `docs/nexus-identification.md` — the two routes to a Nexus page, and why the matcher stays strict.
- `docs/two-part-vs-variant-archives.md` — halves vs variants, and why the rule needs both halves.
