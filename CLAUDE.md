# DDS Mod Manager — project rules

A WPF (.NET 10) mod manager for **both** Drug Dealer Simulator games, built on
[CUE4Parse](https://github.com/FabianFG/CUE4Parse). These rules are specific to this repo and add
to the global engineering standards.

## The two games are not variants of each other

|                 | DDS1                                              | DDS2                                            |
|-----------------|---------------------------------------------------|-------------------------------------------------|
| Engine          | **UE 4.21** default branch; a 4.27 branch exists  | UE 5.3.2                                        |
| CUE4Parse EGame | `GAME_UE4_21` default, from the exe per install   | `GAME_UE5_3`                                    |
| Steam app id    | `682990`                                          | `1708850`                                       |
| Paks            | one `.pak` (v7 on 4.21), unencrypted, no IoStore  | `.pak`+`.ucas`+`.utoc` + `global.utoc`          |
| usmap           | **not needed** (versioned properties)             | required                                        |
| Config dir      | `Saved\Config\WindowsNoEditor`                    | `Saved\Config\Windows`                           |
| Real saves      | `Saved\Serialized\saveSlot-N.save`                | `Saved\SaveGames\Cartels\<name>\`               |
| Public loader   | UnrealModLoader + UnrealModUnlocker               | UE4SS `experimental-latest`                     |
| Loose `.uasset` | **yes — how most DDS1 mods ship**                 | impossible (IoStore)                            |
| DLL plugins     | **yes** — a native .dll + a data folder           | no (UE4SS is the extension mechanism)           |

**DDS1 ships on two Steam branches: 4.21 (the default) and 4.27.** The profile's `GAME_UE4_21` is
only the default — `GenericGameProfiles.ForInstall` takes the installed branch's version from the
exe's version resource (Explorer's Properties → Details → File version, which Unreal stamps with the
engine version; the 4.21 branch reads `4.21.2`). Never hardcode a built-in's version where the
install can say. The 4.21 branch's exe build string reads `++UE4+Release-4.21-CL-4753647` and its
pak is v7, which only 4.21 and earlier write.

**The exe's version resource is the first engine-version signal, for every game.** It is exact and
cheap (unlike the build-string scan). `UnrealEngineProbe.IsConsistentWithPak` vetoes it only when the
pak format contradicts it, since a studio can stamp its own number. The pak table is a range, not a
version: v11 is written by 4.26.x as well as 4.27 (MORDHAU is 4.26.2 on v11).

## Rules that exist because breaking them is silent

**Verify with `dotnet build DDS2ModManager.sln`, never `src/DDS2ModManager.csproj`.** The setup
project *link-compiles* shared source files rather than referencing the main project (to keep
CUE4Parse out of the installer). `src/` can build perfectly clean while the solution is broken.
Anything the setup project links — `AppPaths.cs`, `LoggingService.cs`, `GitHubReleaseService.cs`,
`ShortcutCreator.cs` — must stay dependency-free: no `Models`, no CUE4Parse.

**Never offer to install UE4SS on DDS1.** Stock and experimental UE4SS both crash it immediately;
it needs a custom `LessEqual421` build that ships no prebuilt asset. DDS1 gets UnrealModLoader
instead. DDS2 is the mirror image — stable v3.0.1 crashes it, so the picker offers stable there only
behind `UE4SSStableCaveat`. `GameProfile.InstallableLoaders` is the gate and is deliberately
separate from `SupportedLoaders`: recognising a loader is not permission to install it. A generic
profile computes it from the engine version read off disk (`LoaderCompatibility.InstallableFor` —
experimental 4.7–5.8, stable 4.12–5.3, UML UE4 only, each loader's own published range), and every
loader install on an anti-cheat game is confirmed, every time.

**UE4SS stable installs in its older layout and is only ever touched through its manifest.** v3.0.1
ships `UE4SS.dll`, its settings and `Mods\` loose in `Binaries\Win64`, beside the game exe. The
manifest there (`.dds2modmanager_manifest.json`) lists every file it wrote; removal and the move to
experimental take exactly those (never an `.exe`, never outside Win64). An older-layout UE4SS with no
manifest was installed by hand and nothing is installed over it or moved out of it. Experimental →
stable is refused; stable → experimental moves `Mods\` into `ue4ss\` and re-links lua mod rows.

**UnrealModLoader needs a profile named after the game exe, or it does nothing.** It reads
`Profiles\<exe>.profile` beside itself and logs "Profile Not Detected!" otherwise. When UML ships
none, one is written from the engine version (`BuildUnrealModLoaderProfile`); a profile already on
disk is never overwritten, and Remove keeps the game's own. Both halves of the AutoInjector read one
`ModLoaderInfo.ini` in Win64, and its `LoaderPath` is absolute. Its launcher `.exe` is never copied
into the game, and a UML setup we didn't make (ini, dll or launcher present, no manifest) is never
installed over. It is downloaded only from `github.com/RussellJerome/UnrealModLoader/releases/download/`
(`IsOfficialDownload`). With UnrealModUnlocker also present, DLL plugins still go to UnrealModUnlocker
— UML core mods can't be told apart by their file, and DDS1's frameworks target the unlocker.

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
UnrealModUnlocker reads `Binaries\Win64\UnrealModPlugins`, UnrealModLoader reads `Content\CoreMods`
(derived from the game exe's path — not a `coremods` folder beside the loader, which it never reads). Resolve
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
subfolders for UE4SS. Gated by `GameProfile.LogicModsUseSubfolders` in both install and enable — and
overridden to flat wherever UML is installed (`ModInstallerService.LogicModsNested`), since UE4SS's
BPModLoaderMod reads both the top level and one folder down.

**A fingerprint may only advance when the analysis it describes does.** `RefreshFileState` must not
re-arm on the pass that detected drift; `DeepScan` is the writer, because it is what re-read the mod.

**Nexus mod ids restart per game.** Anything keyed on a mod id — image cache, "this app" badge,
a user's declared `NexusModLink` — needs the domain too, or one game's data shows on the other's card.
**85 ids exist in both live catalogues and not one shares a title**: 79 is "AE Revolutions Reloaded"
on DDS1 and "Gh0sted - Rebalance" on DDS2. A link whose stored domain isn't the active game resolves
to nothing — never to whatever that number happens to mean here. See `docs/nexus-identification.md`.

**An unrecognised Unreal game gets a generic profile, never a built-in's.** Falling back to DDS2's
profile handed other games DDS2's experimental UE4SS, its usmap (a wrong usmap fails silently: paths
list, values are garbage) and its Nexus feed, and saved their folder into DDS2's slot.
`GenericGameProfiles.Resolve` is the one resolver for every route. A generic profile answers "no" to
anything it can't read from disk, and every DDS-only feature must fail closed on it — check
`IsBuiltIn`, `HasNexus`, `InstallableLoaders`, `HasEmbeddedMappings`, never "not DDS1". A blank
`NexusDomain` is not "no filter" to Nexus: it returns the newest mods across the whole site.

**WPF: visual state goes in `ControlTemplate.Triggers` with `TargetName`,** never a `<X.Style>` on an
element whose properties the template already sets as attributes. Template values outrank style
triggers, so those setters are discarded in silence.

## Architecture

- **`GameProfile`** (`src/Models/GameProfile.cs`) holds everything that differs per game. The rule:
  a *value* that changes per game goes in a profile; a *mechanism* stays in the services, which are
  game-agnostic. `GameProfiles.All` is the single place a new game is added. Generic profiles for
  other Unreal games are registered separately and must never enter `All` or `ById`, which callers
  read as "built-in".
- **`GameInstallation`** derives every path from a detected install. It resolves the UE project
  folder *from disk*, not from the profile, so a renamed or repacked install still works.
- **`AppPaths`** is the one place this app's own storage is named. Per-game state is keyed by
  `AppPaths.GameKey(rootPath)` — `SHA256(path.ToLowerInvariant())[..12]`. **Do not change that
  algorithm**: it names files already on users' disks, and do not invent a second scheme.
- **`MainViewModel`** is a partial class across nine files, split so unrelated features don't land
  in the same 2,000-line file. `MainViewModel.Loaders.cs` holds UnrealModLoader.
- **Loader installs are split at `ApplyExtracted`** (UE4SS and UML both): download and unpack, then
  apply. Tests drive `ApplyExtracted` with payloads built on disk, so every file rule is covered
  without the network.
- **State files are written with `AtomicFile.WriteAllText`** (temp file, then rename), never
  `File.WriteAllText` — a crash mid-write must leave the old file, not an empty registry.

## Switching games

`ClearPerGameState()` runs *before* the new game is assigned, and it is load-bearing. The mod list,
the multi-select, the undo closure and several fire-and-forget tasks all hold references to the
outgoing game's services and file paths. `_gameContextVersion` discards background results that
land after a switch. `DetachModSubscriptions()` exists because `ObservableCollection.Clear()` raises
a Reset whose `OldItems` is null, so the collection-changed unsubscribe never runs for a Clear.

Every transition holds `_transitionGate`: startup takes it directly, and a pick in the game picker or
an added folder goes through `OpenGameAsync`. The picker window only *chooses*; the main window runs
the choice through the view model, so no second route can open a game with different rules or
alongside a running switch.

## Releasing

Tag-driven. Pushing `vX.Y.Z` or `vX.Y.Z-exp.N` runs `.github/workflows/release.yml`: it gates on the
unit tests, publishes both exes, and creates the GitHub release with the CHANGELOG section whose
heading is exactly the tag as its notes — which is also what the in-app update prompt shows. An
`-exp.N` tag becomes a prerelease (Experimental channel only) and builds as `X.Y.Z.N`.

- Work lands on `experimental`. `main` holds the last stable release and only ever fast-forwards to
  `experimental` when one is cut. Tags are lightweight, like every earlier one.
- **Experimental:** add `## vX.Y.Z-exp.N` at the top of CHANGELOG.md, commit, push `experimental`,
  then tag that commit and push the tag.
- **Stable:** a `release: vX.Y.Z` commit folds the experimental sections into one `## vX.Y.Z`
  describing the final state — a bug that only ever existed in an experimental build isn't a "Fixed"
  for stable users — and bumps `<Version>` in both `src/` and `setup/` csproj. Fast-forward `main`,
  push both branches, tag.
- Before tagging, run what the workflow will: `dotnet test tests/DDS2ModManager.Tests/DDS2ModManager.Tests.csproj
  -c Release --filter "Category!=Live"`, `dotnet publish` both projects with `-p:Version=X.Y.Z.N`, and
  check the notes extraction finds the section. A mistyped heading falls back to generated notes
  in silence.
- After the run, `gh release view <tag>` must list `DDS2ModManager.exe` and `DDS2ModManagerSetup.exe`,
  and `isPrerelease` must match the channel.

## Checking UI changes

`dotnet run --project tools/RenderHarness [folder]` renders MainWindow and the game picker offscreen
to PNGs against this PC's real catalog, and prints the WPF binding and resource errors the running
app swallows. It never shows a window or runs the app's startup, and restores `settings.json` on
exit. Two traps it exists to avoid:

- **Never construct `App` outside the real app.** WPF's `Application` constructor queues `OnStartup`
  on the dispatcher, so *any* message loop — not only `Application.Run` — opens the real main window
  and runs the whole startup: update checks, Nexus requests, settings writes.
- **Launching the app writes the developer's real `%AppData%\DDS2ModManager`** — settings, logs,
  the mod grid's sort order. Snapshot `settings.json` before a test launch and restore it after.

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
