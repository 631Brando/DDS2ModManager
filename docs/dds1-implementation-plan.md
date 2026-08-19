# DDS1 multi-game: reconciled implementation plan

**Status:** plan of record for `feat/multi-game-dds1`. Generated 2026-08-18 by reconciling eight
independent subsystem analyses against each other and against the built tree.

Companion to [multi-game-architecture.md](multi-game-architecture.md), which holds the *decision*;
this holds the *sequencing*. Read section 5 for the step order and section 6 for what can hurt
existing DDS2 users.

**Amendments since generation** (these override the text below where they conflict):
- **Step 0 is DONE.** The solution build break is fixed: the `GameKey(GameInstallation)` overload was
  removed, both call sites take `game.RootPath`, and `AppPaths.cs` is linked into the setup project.
- **Steps 11 and 12 are DEFERRED.** Andre: "lets defer the rebranding, but just changing the naming
  thats visible for now." Only user-visible display strings change on this branch. The repo name, the
  `%AppData%` folder, the release asset name, namespaces and registry key names all stay. R1 below is
  therefore neutralised for this branch, but the dual-publish obligation still applies whenever the
  rename does happen.
- **MF-7 resolved:** the tab strip is always visible, per Andre'''s "clear discriminator" requirement.
- **X4 resolved:** DDS1 is UE 4.21. The pak footer reads version 7, and pak v8 arrived in 4.22, so the
  container format alone rules out 4.27. The 4.27 stamps in the install are artifacts of a manual
  `[EngineVersionOverride]`. Do not relitigate.

---

## 0. GROUND TRUTH I VERIFIED BEFORE RECONCILING

I built and tested the tree. Three plan assumptions are wrong and one is a hard blocker:

- **`dotnet build DDS2ModManager.sln` FAILS today.** `src/Services/LoggingService.cs:52` reads `AppPaths.Logs`, but `setup/DDS2ModManagerSetup.csproj:34-36` links `LoggingService.cs` without linking `AppPaths.cs`. Error: `CS0103: The name 'AppPaths' does not exist` in the setup project. P8 called this correctly. **Every plan's "verify after each step" premise is void until this is fixed.**
- **`AppPaths.GameKey` DOES exist** (`src/Services/AppPaths.cs:52-55`, exactly `Convert.ToHexString(SHA256.HashData(...ToLowerInvariant()))[..12]`). P8's "BLOCKING: GameKey does not exist" is stale. Both call sites (`ModRegistryService.cs:29`, `SaveGameService.cs:24`) resolve.
- **`dotnet test` = 227 passed / 0 failed** against `src/`. That is the baseline gate.
- **P8's proposed fix to the setup project is itself broken.** `AppPaths.cs:52` has an overload taking `Models.GameInstallation`, which pulls in `GameProfile` → `CUE4Parse.UE4.Versions.EGame`. Linking `AppPaths.cs` into setup either fails to compile or forces a CUE4Parse dependency into the installer, which `setup/DDS2ModManagerSetup.csproj:30-33` explicitly exists to avoid. **Correct fix: delete the `GameKey(GameInstallation)` overload, keep only `GameKey(string rootPath)`, and change the two call sites to `AppPaths.GameKey(game.RootPath)`.** Then the link is trivial and AppPaths stays dependency-free.

---

## 1. MUST-FIX — plans that violate a stated fixed requirement

**MF-1 — Three competing per-game settings containers on one file. (P2 vs P4 vs P7)**
- P2: `AppSettings.Games : Dictionary<string, GameSettings>` keyed by `GameProfile.Id`, holding all ten per-game fields.
- P4: `AppSettings.EGameVersionByGame : Dictionary<string,string>` (`src/Models/AppSettings.cs:27-29`).
- P7: `AppSettings.GamePaths : Dictionary<string,string>` + `ActiveGameId` (`src/Models/AppSettings.cs:31-32`).

Three dictionaries keyed by the same id, storing fields P2 already owns. This is exactly "never invent parallel systems". **Ruling: P2's `Games`/`GameSettings` is the single scheme. P4 deletes its `EGameVersionByGame` entry entirely. P7 deletes its `GamePaths` entry and reads `AppSettingsService.Instance.ForGame(profile).GamePathOverride`. `ActiveGameId` is a single global on `AppSettings`, written by exactly one writer (`SetupForGameAsync`), which both P2 and P7 already agree on.**

**MF-2 — Two parse/mount option types for the same five call sites. (P2 vs P4)**
P2 proposes `src/Models/GameParseOptions.cs` with `GameParseOptions.For(game)`. P4 proposes `GameMountService.MountOptions` + `OptionsFor(game)`. Both replace *the identical five duplications*: `MainViewModel.cs:612-616`, `:857-860`, `:920-923`, `:1054-1057`, `GameResetService.cs:105-110`. **Ruling: one type, `GameMountService.MountOptions` + `GameMountService.OptionsFor(GameInstallation)`** — `GameMountService.cs:9-12` already documents itself as "one place that knows how to mount", and a new `Models/` file for a service-resolution concern is the weaker home. It resolves via `AppSettingsService.Instance.ForGame(game.Profile)` (P2's rules) and gates mappings on `game.Profile.NeedsMappings` (P4's rule). Delete `src/Models/GameParseOptions.cs` from P2.

**MF-3 — Two mutually exclusive fixes for the same Disable/Enable flattening bug. (P1 vs P6)**
P1 proposes ordinal subfolders `cacheDir\0000\Name.uasset` so `Enable()`'s `Path.GetFileName(f)` at line 525 still recovers the name. P6 proposes mirroring the relative tree (`Path.Combine(cacheDir, rel)`) driven by `ModInfo.LooseRelativePaths`. **These cannot coexist**: P1's Enable keeps `Path.GetFileName`, P6's needs the relative path. **Ruling: P6's relative-tree mirroring, applied to ALL mod types, not just LooseAsset.** It subsumes P1's case (a pak triple's relative paths are bare filenames, so behaviour is byte-identical for DDS2) and it is the only one that survives `Content\<Category>\` subfolders. P1 drops its `ModInstallerService.cs:463-466` entry; P1 KEEPS its `ModInstallerService.cs:523-531 / 463-470` empty-result guard, which is orthogonal and load-bearing.

**MF-4 — Two game-switch entry points. (P2 vs P7)**
P2 adds `MainViewModel.SwitchToGameAsync(GameInstallation)` at `MainViewModel.cs:537`. P7 adds `SwitchGameAsync(GameTabViewModel)` in a new `MainViewModel.Games.cs`. **Ruling: P7 owns it, in the new partial.** P2 drops that change entry; the settings side is already satisfied because P2 makes `SetupForGameAsync` the single `SetActiveGame` writer, which P7's switch calls.

**MF-5 — P4 scopes `ModInstallerService.cs:41` without a migration.**
P1 and P4 both propose `_disabledCacheDir = <DisabledMods>\<GameKey>`. P4 has no migration; landing it alone orphans every currently-disabled DDS2 mod (P4 acknowledges this as a "medium" risk — it is not, see §6 R2). **Ruling: P1 owns `ModInstallerService.cs:41`. P4 deletes that entry.**

**MF-6 — P8's optional renames must be made binding NO's.**
P8 lists these as changes with an "if renamed" caveat. Make them hard rules, because each has a silent-destruction path already spelled out in P8's own risk section:
- **DO NOT rename `AppPaths.AppDataFolderName`** (`AppPaths.cs:17`). It is invisible to users, appears in three pieces of prose, and renaming it requires rewriting absolute paths inside `registry_*.json` — the same paths `ModInstallerService.cs:461-473` persists. Not renaming also deletes the entire "two %AppData% migrations compound" risk that P1 and P2 both flag.
- **DO NOT rename `GameConfigService.BackupSuffix` (`.dds2mm.bak`)**. `UE4SSManagerService.cs:107-111` reads its presence as "the user edited this, preserve it".
- **DO NOT rename `.dds2modmanager_manifest.json`** (`UE4SSManagerService.cs:63-64`).
- **DO NOT rename the GitHub repository.** `ModTrustService.cs:45` and `TrustedNexusAuthorService.cs:22` fetch `raw.githubusercontent.com` on every launch and fail silently.
- **`.dds2mod.json` (`ModManifest.cs:65`) and `ModUpdateUrl`/`ModVersion`/`ModAuthor` (`ModUpdateSourceResolver.cs:25-27`) are frozen by SDK agreement.** No unscoped find-and-replace of `dds2` at any point; scope every sweep to the exact tokens `DDS2ModManager`, `DDS2ModManagerSetup`, `DDS2 Mod Manager`, `DDS2MM`.

**MF-7 — MUST CONFIRM WITH ANDRE: P7 hides the tab strip when only one game is installed.**
`GameTabViewModel.BuildStrip` returns `Show = count(Installed or Remembered) > 1`. The stated fixed requirement is "a real TAB AT THE VERY TOP of the main window, a clear unmistakable DDS1/DDS2 discriminator". A strip that is invisible for the majority of users is not an unmistakable discriminator. P7 flags this itself as UNCERTAIN. **Default to always-visible** (both tabs always present, the uninstalled one rendered in the `Missing` state whose click action is Browse). It is a one-character change in P7's factory and no other code moves.

**MF-8 — P1's migration key-resolution reads a property P2 deletes.**
`LegacyStateMigrationService` step (1) resolves the key from `settings.GamePathOverride`. P2 removes that property from `AppSettings` (`src/Models/AppSettings.cs:32`) into `GameSettings`. If P2 lands first, P1's step (1) silently falls to step (2)/(3) and either mis-attributes or strands the user's history/profiles/backups — with no error. **Ruling: P1's migration lands BEFORE P2, and P2's commit updates that one line to `ForGame(GameProfiles.Dds2).GamePathOverride` so it still compiles.** `StateLayoutVersion` stays a global on `AppSettings` (P2 only moves the ten named per-game fields), so the stamp survives.

---

## 2. CONTRADICTIONS (design-level, resolved)

| # | Contradiction | Ruling |
|---|---|---|
| X1 | `docs/multi-game-architecture.md:153` says AppSettingsService "must stop being a Lazy<> singleton"; P2 argues it must not (the game path is read *out of* settings at `MainViewModel.cs:508` before any GameInstallation exists). P1 meanwhile *does* kill the `ModHistoryService`/`ModBackupService` singletons. | Both are right and not in conflict. **AppSettingsService keeps its singleton + private parameterless ctor and grows `ForGame()`/`SetActiveGame()`. ModHistory/ModBackup/ModProfile become per-game constructed objects.** P2's commit amends the doc line and records the chicken-and-egg reason. |
| X2 | Where "which game is active" is authoritative. P2: `GameInstallation.Profile` inferred from disk always wins, `ActiveGameId` is derived. P7: tabs matched by `Profile.Id`, `ActiveGameId` written in the switch. | **Single rule: disk inference (`GameInstallation.cs:13-18`) beats stored id; `SetupForGameAsync` is the only writer of `ActiveGameId` and of `ForGame(profile).GamePathOverride`; `ActiveGameId` is derived state, never a second source of truth.** Both plans already say this; write it once in the doc. |
| X3 | P3 proves the save container variant must be detected **per file** (DDS1 has a compressed `saveSlot-0` and an uncompressed `saveSlot-1` in the same folder) and must NOT come from `GameProfile`. No plan disagrees. | No conflict. **Explicitly forbid adding a save-header field to `GameProfile`** so nobody "tidies" it in later. |
| X4 | P3 flags that DDS1's own `saveSlotsFull.sav` and `saveSlot-1.save` stamp engine 4.27.2 / CL 18319896 / package 522, contradicting the brief's verified 4.21. P4's whole pak story rests on `GameProfile.cs:142 EGame.GAME_UE4_21`. | **Not a plan contradiction — a ground-truth flag.** P3's own code derives UE4-vs-UE5 from the file, so P3 is correct either way. P4 measured that 5.3 lists all 35,955 files but throws on deserialize; the brief states 4.21 deserializes 25/25. **Do not relitigate. Do spend ten minutes before step 5 confirming the pak footer version is 7 (v8 arrived in 4.22) — that single number settles it and is cheaper than the argument.** |
| X5 | P4 removes the staging copy in `ModAnalyzerService.cs:96-121` entirely; P8 wants a sweeper for leftover `__DDS2MM_analyze__*` files and considers renaming the prefix. | **P4 wins on removal. P8 must NOT rename the prefix** — nothing writes it after step 6, but ≤v1.2.0 builds left files using the old name in users' `Content\Paks`, and UE mounts anything it finds there. P8's item becomes: keep a one-time sweep of the *legacy* prefix, permanently. |
| X6 | `NexusGameDomain` de-hardcoding: P7 claims `MainViewModel.cs:78` and `TrustedModsWindow.xaml.cs:58`; P2 declares it a dependency for its per-game Nexus timestamps; P7 also claims the banner strings at `MainViewModel.Nexus.cs:54-56`. | **P7 owns `MainViewModel.cs:78`, `TrustedModsWindow.xaml.cs:58,142,326` and `Nexus.cs:54-56`. P2 owns `Nexus.cs:32-41` and `:96-97` (the timestamps).** Different lines; no textual conflict, but P2's Nexus edits are *inert until* P7's domain property lands, so P2's step must not claim per-game Nexus behaviour works yet. |
| X7 | `VanillaResetOptions.RemoveUE4SS` (bool) → P5's `ModLoaders LoadersToRemove`, vs P2/P4 both editing `GameResetService.cs:104-110`. | Different regions of the same file. **Order: MF-2's options refactor (step 4) → P4's scan/delete guards (step 5) → P5's `RemoveLoaders` rewrite (step 8).** |

---

## 3. COLLISIONS — one owner per file/region

| File | Region | Plans wanting it | Owner / order |
|---|---|---|---|
| `src/Services/AppPaths.cs` | 33-41 scoped accessors | P1 | **P1 (step 1).** Also delete the `GameKey(GameInstallation)` overload at :52 (step 0). |
| | identity consts (`ExeName`, `TempPrefix`, `SaveBackups`…) | P8 | **P8 (step 11)**, additive only. |
| `src/Models/AppSettings.cs` | :25,29,32,59,70,82-84,105 → `GameSettings` | P2 | **P2 (step 3).** |
| | `ActiveGameId` | P2, P7 | **P2 defines it; P7 consumes it.** |
| | `GamePaths` dict | P7 | **DELETED** (MF-1). |
| | `EGameVersionByGame` dict | P4 | **DELETED** (MF-1). |
| | `StateLayoutVersion` | P1 | **P1 (step 2)** — stays global. |
| `src/Services/AppSettingsService.cs` | 6-18 `ForGame`/`SetActiveGame`, 20-31 Load+salvage, 44-45 shared JsonOptions | P2 | **P2 (step 3).** |
| | `ResetAllAppData` :56-71 scope decision | P1 | **P1 (step 2)** — recommend leaving `mod-history_*.json` alone and saying so in the comment at :50-55. |
| `src/Services/ModInstallerService.cs` | :41 disabled cache scoping | P1, P4 | **P1 (step 1).** |
| | :523-531 / :463-470 empty-result guard | P1 | **P1 (step 1)** — must precede the migration. |
| | :459-482 / :506-542 flatten fix | P1, P6 | **P6's relative mirroring (step 10)**, applied to all types. |
| | :339 container extensions, rename `InstallPakTriple`→`InstallPakFiles` (:321,163,167,171) | P4 | **P4 (step 5).** |
| | :162-179 install switch, `InstallLooseAssets`, `InferModName` :309-321, uninstall :404-430, import :247-282 | P6 | **P6 (step 10).** |
| | :321-334 / :504-519 LogicMods subfolder | P4 (OPEN) | **Blocked pending verification — see GAP-6.** |
| `src/ViewModels/MainViewModel.cs` | 537-606 `SetupForGameAsync` | P1, P2, P5, P7 | Four-way. **Order: P1 (build `_history`/`_backups`/`_profiles`) → P2 (`SetActiveGame` first statement, per-game path write at :566) → P5 (loader detection replacing :551-561) → P7 (`_gameContextVersion` token, unsubscribe before :548 `Clear`, `RebuildGameServices()` extraction).** |
| | 608-620, 856-864, 919-927, 1053-1065 parse trio | P2, P4 | **Single edit in step 4** (MF-2). |
| | 489-535 `InitializeAsync` | P1 (RunOnce), P2 (active-game resolution), P7 (`RefreshGameTabs`) | **P1 → P2 → P7**, in that order; each is additive to a different part of the method. |
| | :78 `NexusGameDomain`, 624-630 `ReapplySettings`, 641-685 `BrowseGameFolderAsync` | P7 | **P7 (step 9).** |
| | :417 backup, :450 history | P1 | **P1 (step 1).** |
| | :952, :969, :1013 strings | P8 | **P8 (step 11).** |
| `src/ViewModels/MainViewModel.Tools.cs` | :23 `_profiles`, :74, :136-137 | P1 | **P1 (step 1).** |
| | :42 `BundleRequest` shape | P1 (pass-through), P5 (reshape) | **P5 (step 8b).** |
| `src/MainWindow.xaml` | `RowDefinitions` 154-160 + `Grid.Row` on the five direct children **163, 191, 398, 891, 909** (verified — the others at 414/444/458/474/497/773 are nested and must NOT move) | P7 | **P7 (step 9), as an isolated first hunk of that commit.** |
| | 326-394 UE4SS card | P5 | **P5 (step 8b)** — keep the enclosing `<Border Grid.Column="1">` wrapper so P7's later renumber is a one-line edit. |
| | :5, :171 title/header strings | P7 (binding), P8 (text) | **P7 owns the `Title` binding + deleting `MainWindow.xaml.cs:18`; P8 owns the literal.** |
| `src/Services/GameResetService.cs` | 104-115 | P2, P4 | **Step 4 (single edit).** |
| | 123-141 delete loop + base-pak guard | P4, P6 | **P4 (step 5) adds the guard; P6 (step 10) adds directory pruning.** |
| | 16-18, 76-77, 85, 146-174 | P5 | **P5 (step 8b).** |
| `src/Services/SaveGameService.cs` | 27-30, 73-96, 299-340 | P3 | **P3 (step 7).** Heed P3's `BackupsPath` trap at :29 — do NOT put the root segment into `_disabledDir`. |
| `src/Services/UnmanagedModScannerService.cs` | 19-21, 152, 157, 231 | P4 | **P4 (step 5).** |
| | 26-31, 308-309 (enabled.txt) | P5 | **P5 (step 8).** |
| | 46-59, 270-317 `ScanLooseAssets` | P6 | **P6 (step 10).** |
| `src/Views/SettingsWindow.xaml(.cs)` | 15-33, 175-191, 193-220, 246-261; XAML 35-45 | P2 | **P2 (step 3).** |
| | 168-173 Open Disabled Mods | P1 | **P1 (step 1).** |
| | strings :69/:97/:130, 233-239 | P8 | **P8 (step 11).** |
| `src/Converters/Converters.cs` | append after :119 | P7 (GameIdToBrush), P6 (ModType badge at 11-14) | Both additive, no overlap. **P6 edits 11-14; P7 appends.** |
| `tests/.../ModProfileTests.cs:10` | ctor change | P1 | **P1 (step 1)** — add the explicit-directory ctor. |
| `tests/.../TrustGateTests.cs:25` | scan `GameSettings` too | P2 | **P2 (step 3).** Non-negotiable: splitting the settings object opens a hole the trust gate cannot see. |
| `tests/.../ConfigListingTests.cs:58` | `"Mod loader (UE4SS)"` literal | P5 | **P5 (step 8).** |

---

## 4. GAPS — nothing in any plan covers these

**GAP-1 (HIGH, confirmed by reading the file).** `src/Services/GameVersionWatchService.cs:19` hardcodes
`private const string GameExeRelative = @"DrugDealerSimulator2\Binaries\Win64\DrugDealerSimulator2-Win64-Shipping.exe";`
On DDS1 `Read()` returns null at :41, so `CheckGameVersionChanged` (`MainViewModel.Updates.cs:31-33`) returns immediately. **Every bit of P2's per-game `LastSeenGame*` work is dead on DDS1, and the "the game was patched, check your mods" warning — the single most useful diagnostic when a DDS1 mod breaks — can never fire.** `MainViewModel.Tools.cs:119` and `:61` also report `"unknown"` in every DDS1 profile export and shared mod list. Fix: `Path.Combine(game.Win64Path, $"{game.ProjectName}-Win64-Shipping.exe")`, falling back to a `*-Win64-Shipping.exe` glob in `Win64Path`. **Assign to P2, step 3.** One line, one test.

**GAP-2 (MEDIUM, confirmed).** `MainViewModel.cs:542` calls `OodleHelper.EnsureOodleAvailable(game)` unconditionally. DDS1's pak is v7/zlib — Oodle is irrelevant. `OodleHelper.cs:46` does `Directory.GetFiles(game.RootPath, "oo2core_*_win64.dll", SearchOption.AllDirectories)` over an 11.3 GB install, then CUE4Parse downloads a native DLL over the network. The `_initialized` latch (`OodleHelper.cs:12,24`) means a DDS2-first session masks it, but a DDS1-only user pays a full recursive tree walk plus a network download at every first launch, for a codec their game never uses. Gate on `game.Profile.PakLayout == PakLayout.IoStoreTriple`. **Assign to P4, step 5.**

**GAP-3 (MEDIUM, confirmed).** `src/Services/NexusIndexService.cs:66` caches to one `nexus-index.cache.json`, and `ReadCache` at :220 returns null whenever the stored `GameDomain` differs. **Every game switch discards the other game's catalogue and re-fetches the whole thing** (183 DDS1 / 98 DDS2 mods over paged GraphQL). P2 flags it out-of-scope; P7 does not claim it. Fix: `nexus-index.<domain>.cache.json`. **Assign to P7, step 9.**

**GAP-4 (MEDIUM).** `src/Views/TrustedModsWindow.xaml.cs:58` `private const string GameDomain = "drugdealersimulator2"`, used at :142 and :326. P7 flags it but explicitly declines ownership ("the Nexus subsystem may prefer to own this"). **Nobody owns it. Assign to P7, step 9.** Without it, a DDS1 user opening "Browse trusted mods" gets DDS2's catalogue.

**GAP-5 (CRITICAL).** P4 proposes a second, independent guard in `GameResetService` against deleting the base pak — but only inside a *risk mitigation*, not as a change entry, so it would be lost in implementation. **Make it an explicit change:** `GameResetService.cs:123-141` must refuse to `File.Delete` any file sitting directly in `Content\Paks` whose base name starts with `{game.ProjectName}-`, and must name any file over ~1 GB in the confirmation dialog. The scanner filter and the deleter must not share a single point of failure for an irreversible 11.3 GB loss. **Assign to P4, step 5, with the DDS2-direction regression test.**

**GAP-6 (BLOCKING FOR A DDS1 RELEASE, not for the branch).** Two questions nothing in these plans can close from this machine:
1. **Does UnrealModLoader recurse into `Content\Paks\LogicMods\<Mod>\` subfolders?** (`ModInstallerService.cs:321-334` and `:504-519`). If it does not, every DDS1 logic mod this manager installs **silently never loads** — the worst available failure mode. P4 correctly refuses to guess.
2. **UnrealModLoader's on-disk fingerprint** — P5 searched the whole DDS1 install and found zero `ModLoaderInfo.ini`. Its proxy DLL name, install root and file set are all unverified.

Neither blocks the branch (P5's backend refuses to install or remove UML; P4 changes nothing here). Both block calling DDS1 supported. **Track them as named pre-release verification items, not as code.**

**GAP-7 (PROCESS).** `docs/` is untracked. P2 amends `multi-game-architecture.md:149-153`, P8 adds a compatibility-obligations section, and P1/P3/P5/P6 all record decisions with nowhere to put them. Per the standing rule, **each step below updates `docs/multi-game-architecture.md` in its own commit**; P8's compatibility section lands last and must name the permanent obligations (legacy release asset, legacy shell verb / uninstall key / `.lnk` names, `.dds2mod.json`, `ModUpdateUrl`/`ModVersion`/`ModAuthor`, `.dds2mm.bak`, `.dds2modmanager_manifest.json`).

**GAP-8 (LOW, no action).** No plan covers `GameDataWindow`/`ExistingModsWindow`/`ModFilesWindow` being open across a switch. They are all `ShowDialog()` (`MainViewModel.Tools.cs:74`, `MainViewModel.cs:634-638`), so the modal blocks the strip. Worth one sentence in the doc; no code.

---

## 5. RECOMMENDED SEQUENCING

Gate after **every** step: `dotnet build DDS2ModManager.sln` (the solution, not `src/`) **and** `dotnet test` ≥ 227 passing. Steps 5, 6, 9 and 10 additionally need a manual DDS2 pass (install a pak mod → disable → enable → uninstall; Find Existing Mods; Deep Scan).

**Step 0 — Make the tree build.** `setup/DDS2ModManagerSetup.csproj`: add `<Compile Include="..\src\Services\AppPaths.cs" Link="Shared\AppPaths.cs" />`. **First** remove the `GameKey(GameInstallation)` overload at `AppPaths.cs:52` and change `ModRegistryService.cs:29` / `SaveGameService.cs:24` to `AppPaths.GameKey(game.RootPath)` — otherwise the link drags `GameInstallation`→`GameProfile`→CUE4Parse into the installer, which `setup/…csproj:30-33` exists to prevent. Nothing else in this commit.
*Why first:* nothing below can be verified until the solution compiles.

**Step 1 — P1 foundation (no migration).** AppPaths scoped accessors; de-singleton `ModHistoryService`/`ModBackupService`; `ModProfileService` per-game + test ctor; `ModInstallerService.cs:41`; **the Enable/Disable empty-result guards (`:523-531`, `:463-470`)**; `ModBackup.GameRootPath`; `ModProfile.GameId` + `ToShareableText` (schema stays 1); VM wiring at `MainViewModel.cs:417/:450`, `Tools.cs:23/74/136-137`, `ModHistoryWindow` ctor, `SettingsWindow.xaml.cs:168-173`; `ModProfileTests.cs:10`.
*Why here:* `AppPaths` is the foundation four later steps consume, and the guards must exist before any migration can move a file (MF-3, R2).

**Step 2 — P1 migration.** `LegacyStateMigrationService.RunOnce()` + `AppSettings.StateLayoutVersion` + the call in `InitializeAsync` before any game is chosen; `ResetAllAppData` scope decision.
*Why separate from step 1:* a migration bug must be bisectable, and step 1's guards are already the net underneath it.
*Why before step 3:* MF-8 — the key resolution reads `settings.GamePathOverride` in its pre-P2 shape.

**Step 3 — P2 settings sectioning.** `GameSettings`; `AppSettings.Games` + `ActiveGameId`; `AppSettingsService.ForGame`/`SetActiveGame`/shared `JsonOptions`/salvage copy; `AppSettingsMigration` (including nulling a legacy `GAME_UE5_3` that equals the profile default); `InitializeAsync` active-game resolution; `SetupForGameAsync` `SetActiveGame` + per-game path write; `Updates.cs:28-45`; `Nexus.cs:32-41,96-97`; SettingsWindow + XAML combo; `TrustGateTests.cs:25`; **GAP-1** (`GameVersionWatchService.cs:19`); update P1's migration key line; amend `docs/…:149-153`.
*Why before 4-10:* every subsequent step reads a per-game setting through `ForGame`.

**Step 4 — The single options type.** `GameMountService.MountOptions` + `OptionsFor`, replacing `MainViewModel.cs:612-616/856-864/919-927/1053-1065` and `GameResetService.cs:104-110`; drop `ModAnalyzerService.cs:51`'s `= EGame.GAME_UE5_3` default; guard `GameMountService.cs:33` on an empty mappings path.
*Why its own step:* pure refactor, zero DDS2 behaviour change, and it is the seam steps 5, 6, 8 and 10 all sit on. Verifying it alone proves DDS2's analyzer output is unchanged.

**Step 5 — P4 correctness (no staging removal).** `GameProfile.ContainerExtensions`; the three hardcoded triples (`ModInstallerService.cs:339`, `ModAnalyzerService.cs:92`, `UnmanagedModScannerService.cs:152`); **`IsBaseGameArchive` (`:19-21`, `:157`) keyed on `game.ProjectName`**; **GAP-5** guard in `GameResetService`; **GAP-2** Oodle gate; de-DDS2 the diagnostics wording (`ModAnalyzerService.cs:87,104,133,176-179`, `UnmanagedModScannerService.cs:231`).
*Why before the strip:* this is the 11.3 GB-redownload fix. It must be in before DDS1 is reachable.

**Step 6 — P4 staging removal, alone.** `ModAnalyzerService.cs:96-121, 181-191, 199-206`; path-based (not name-based) reader set-difference. Requires P4's acceptance run: re-analyze every mod in the user's own `Content\Paks` from a copy **outside** the game folder and diff `Type` / `AssetPaths.Count` / `DataTableAppends` against the registry. If one mod disagrees, revert this commit and keep staging.
*Why its own commit:* it is the riskiest single change to DDS2's most load-bearing path and must be revertable without losing step 5.

**Step 7 — P3 saves.** Independent of steps 3-6; could equally run right after step 1. Its only shared file is `SaveGameService.cs`, settled in step 1. Ship the container/base/header probe, the `+4`→`+base` threading (`RamaSaveReader.cs:129,132,254,256,257,598`), `GvasSaveReader.cs:51-58`, `GvasNameRewriter.cs:152-156`, `SaveEntry.RootName`, multi-root listing with the legacy two-level fallback, `ISaveRules`/`Dds1SaveRules` (Clone **refuses** on DDS1), `SteamCloudService.cs:119-132`.
*Trap to carry forward verbatim:* do not put the root segment into `_disabledDir` (`SaveGameService.cs:29`) or `BackupsPath` silently relocates every backup.

**Step 8a — P5 loader model + backends.** `ModLoaderInstallation` (with `RemovableRoot` **null** for Legacy/Flat), `IModLoaderBackend`, `LoaderFingerprints`, `ModLoaderService`, `UE4SSManagerService.Detect`, `UnrealModUnlockerService`, `UnrealModLoaderService`. Consumers untouched; `Detect` still answers modern-only for DDS2. 227 stays green.

**Step 8b — P5 consumer cutover.** `GameResetService.RemoveLoaders` + `VanillaResetOptions`, `ResetGameWindow`, `GameConfigService`, `InjectedDllCheck`, `LuaModConfigService` (enabled.txt), `DiagnosticsBundleService`, `MainViewModel.Loaders.cs` partial, `MainWindow.xaml:326-394`, `Tools.cs:42`, `ConfigListingTests.cs:58`.
*Why before step 9:* with today's code DDS1 reports UE4SS absent, so the Install button is live and would drop the modern experimental build on a working legacy 3.0.1 install (two `UE4SS.dll`, two settings files, two Mods folders). That must be impossible before DDS1 is reachable.
*Non-negotiable test:* `RemovableRoot` is null for the legacy layout, asserted as `!= Win64Path` as well as `== null`; no removal plan from any backend ever contains `Win64Path` or a path outside it. `GameInstallation.UE4SSRootPath` (`:52-58`) and `GameProfileTests.cs:144-160` stay untouched.

**Step 9 — P7 tab strip.** Commit the `RowDefinitions` insert + the five `Grid.Row` renumbers (**163, 191, 398, 891, 909** only) as the first hunk, alone, then the strip. Then `GameTabViewModel` (**always-visible per MF-7**), `Converters.cs` append, `MainViewModel.Games.cs` with `ClearPerGameState` (`UndoService.Invalidate`, empty selection, banners), `_gameContextVersion` in `SetupForGameAsync`, the explicit unsubscribe before `MainViewModel.cs:548`, `RebuildGameServices()` shared with `ReapplySettings` (`:624-630`), `NexusGameDomain` property (`:78`), **GAP-3** and **GAP-4**, `WindowTitle` binding replacing `MainWindow.xaml.cs:18`, `BrowseGameFolderAsync(GameProfile?)`.
*Why here:* this is the step that makes DDS1 reachable at runtime. Everything DDS1-correctness must already be in.

**Step 10 — P6 loose assets.** `ModType.LooseAsset` appended (=4, with the numeric-pinning test); `ModInfo` fields; `ModArchiveLayoutService.DetectLooseAssets` + `KindOf` (**do NOT add `Content` to the marker arrays at `:30-31`** — `IsInstallableRoot(Content\Paks)` is true via `ModVariantDetectionService.cs:31`'s `AllDirectories` search and would break `ArchiveLayoutTests.cs:49-56`); `ModVariantDetectionService` `allowLooseAssets`; analyzer branch; installer install/uninstall/import; **the relative-tree Disable/Enable mirroring for all types (MF-3)**; `ModFileStateService` relative keying; `ModConflict.LooseAssetOverwrite`; `CompatibilityCheckerService`; `ScanLooseAssets`; `ModUpdateSourceResolver.cs:185-186`; `Converters.cs:11-14`.
*Why last of the functional work:* it consumes step 8's loader detection (to warn when the unlocker is absent) and step 5's base-pak filter.

**Step 11 — P8 identity + compatibility pairs.** The **dual-asset publish in `.github/workflows/release.yml:120-123`** and the ordered candidate list in `AppUpdateService.cs:25,200-201` and `setup/MainWindow.xaml.cs:11,55-61`; read-old/write-new for `AppUninstaller.cs:12,24-25`, `ShellIntegrationService.cs:12,15-17,19-55`, `ShortcutService.cs:8,16-23,38-42`; the remaining `%AppData%` literals through AppPaths (`ModProfileService.cs:53`, `ModTrustService.cs:55`, `TrustedNexusAuthorService.cs:34`, `NexusIndexService.cs:66`, `NexusImageCache.cs:49-50`); the legacy `__DDS2MM_analyze__` sweeper; all user-facing strings; `RebrandCompatibilityTests`; README/docs compatibility section.
*Why before step 12:* the dual publish must exist before any release is cut from this branch, and it must not be entangled with a 110-file rename.

**Step 12 — P8 mechanical rename, alone.** `git mv` for the three projects, the `.sln`, the tests directory; one scripted regex sweep over 110 `namespace` declarations, 21 `x:Class` + 4 `clr-namespace`, `GlobalUsings.cs:10-11`, both workflows, the five fully-qualified refs in `setup/MainWindow.xaml.cs`. **Nothing else in this commit — no strings, no logic.** Verify with a clean solution build *and* `dotnet test`.
*Why last:* a half-applied namespace rename does not compile, so bisect is useless, and landing it early guarantees a conflict with every subsequent commit.

---

## 6. RISK TO EXISTING DDS2 USERS, RANKED

**R1 — CRITICAL. Silent, permanent, unfixable stranding of every installed copy.** `AppUpdateService.cs:200-201` matches the release asset by exact name; `:110` turns "no matching asset" into `Succeeded=true, NewerRelease=null`, which the UI renders as *"you're on the latest version"*. On the experimental channel `GetChannelStatusAsync:146` filters such releases out entirely. Publish one release with only `DDSModManager.exe` and every v1.2.0 install stops updating, with no error, and the fix can only ship through the channel that is broken. → **Step 11 must land before any release is cut. Dual-publish permanently.**

**R2 — CRITICAL. `DisabledMods` scoped without its migration = permanent loss of mod tracking.** A disabled mod's registry entry holds absolute paths into the flat `%AppData%\DDS2ModManager\DisabledMods\<modId>`. Move the folder without rewriting the registry and `Enable()`'s `.Where(File.Exists)` at `:523` yields nothing, `:530` writes an empty `InstallFiles`, and `:536` logs **Success**. Only recoverable by hand-editing JSON. → **Steps 1+2 may be separate commits but must never be separate releases; the empty-result guard lands in step 1; move-then-`Save()` per mod; never delete an unclaimed GUID folder.**

**R3 — CRITICAL. Base-game pak deletion.** DDS2 is protected today only by the `pakchunk*`/`global` clauses at `UnmanagedModScannerService.cs:19-21`, feeding an unconditional `File.Delete` at `GameResetService.cs:131`. The danger in step 5 is *regressing DDS2's filter* while adding DDS1's. → **Test both directions, plus GAP-5's independent size/location guard in the deleter.**

**R4 — HIGH. Settings migration silently loses the game path, AES key, mappings override, version stamps and Nexus history.** Presents as "the app forgot everything", not as an error. → **`GameSettings` keeps byte-identical property names so the migration is one `Deserialize<GameSettings>` with no hand-written mapping; the test drives a verbatim v1.2.0 `settings.json` and asserts each of the ten fields individually; `Load()` writes the migrated shape once and logs a named line.**

**R5 — HIGH. Every existing user gets permanently pinned to UE 5.3.** `SettingsWindow.xaml.cs:250` writes `"GAME_UE5_3"` on *every* save, so it is in every existing `settings.json`. Migrated faithfully, it becomes an explicit override that survives any future profile bump. Failure is silent: paks still list every path, only deserialisation fails. → **Null it during migration when it equals `GameProfiles.Dds2.EngineVersion`; keep only a deliberate change. Tested both ways.**

**R6 — HIGH. Staging removal alters DDS2's most load-bearing path.** A wrong `Type` puts a LogicMod in `Content\Paks` where it silently never loads. Compounded by the measured fact that name-based `TryGetArchive` returns the *installed* copy when a mod is being reinstalled. → **Step 6 alone, behind the registry-diff acceptance run, revertable without losing step 5.**

**R7 — HIGH (post-step-9). Cross-game file deletion and registry corruption.** `SelectedMods` (`Mods.cs:67`) and the `UndoService` closure (`Mods.cs:198-214`) survive a switch and capture the *outgoing* `_installer` and ModInfo — `BulkUninstall` (`Mods.cs:132-167`) and `UndoLast` (`FileState.cs:76-83`) then delete or move files using the wrong game's paths. Separately, the four fire-and-forget tasks at `MainViewModel.cs:570/576/580/584` call `_registry?.Save()` (`:273`) and write `mod.NexusInfo` on resume. → **`ClearPerGameState` before `Game` is reassigned; `_gameContextVersion` token; strip `IsEnabled` bound through the existing `InverseBool`; `IsBusy` re-check in the change handler under the `_switchingGame` guard.**

**R8 — MEDIUM. Stale subscriptions write into the wrong registry.** `ObservableCollection.Clear()` at `MainViewModel.cs:548` raises Reset with `OldItems == null`, so the unsubscribe at `:182-183` never runs; every ModInfo the user ever loaded keeps firing `OnModAnnotationChanged` → `_registry?.Upsert` (`:198`). Latent today (Clear happens once), live after step 9. Two-line fix.

**R9 — MEDIUM. Global backup trim.** `ModBackupService.cs:43` `MaxBackups = 8` and `Trim()` at `:200-214` are app-wide today. Not a DDS2 regression, but eight DDS2 updates would delete every DDS1 rollback copy the moment DDS1 ships. Fixed by step 1. Note that `Restore` has **no caller anywhere in the app today** — backups are write-only — so attribution imprecision costs nothing right now.

**R10 — MEDIUM. Step 8b is a simultaneous compile break across `MainWindow.xaml` bindings (`:334,339-340,355,360-361,374-375,384,392`), `MainViewModel.cs:15/551-561/1128-1131/1174/1219`, `Tools.cs:42` and `DiagnosticsBundleService.cs:26,87-90`.** All runtime-only — `UE4SSManifest` (`UE4SSInstallInfo.cs:18-23`) is the one on-disk shape and stays byte-identical. → **The 8a/8b split is what keeps a verifiable point between them.** Also pin: a modern install *with* `.dds2modmanager_manifest.json` still reports `IsManagedByUs=true`, so today's DDS2 users' state does not flip and no spurious update prompt appears.

**R11 — MEDIUM. Downgrade.** After step 2 an older build sees empty history/profiles/backups; after step 3 it sees a settings file whose per-game fields moved. Data is intact in both cases. → **Release-note wording; do not build a copy-instead-of-move shim (two live profile lists that both accept edits is worse than a documented one-way step).**

**R12 — LOW. `ModType` is serialised as an INTEGER** by `ModProfileService.Save` (`:124`) and `ModBackupService.Save` (`:238`) — neither passes a `JsonStringEnumConverter`, unlike `ModRegistryService.cs:14-21`. Appending `LooseAsset = 4` is safe; inserting anywhere else silently remaps every saved profile and backup entry. → **Numeric-value test in step 10. Adding the converter to those two services is a separate migration and must not ride along.**

**R13 — LOW. UserAgent changes** on `NexusFeedService.cs:32`, `NexusImageCache.cs:42`, `NexusIndexService.cs:40` could trip a WAF. Keep all UA edits in one release so a Nexus failure is attributable and independently revertable.