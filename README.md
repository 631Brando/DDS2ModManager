# DDS2 Mod Manager

A mod installer/manager/compatibility-checker for Drug Dealer Simulator 2, built with
WPF (.NET 10) and [CUE4Parse](https://github.com/FabianFG/CUE4Parse).

## What it does

- Auto-detects your DDS2 install by scanning Steam library folders (or lets you browse manually).
- Detects UE4SS and warns if it isn't installed, or if it wasn't installed by this tool
  (in which case we can't confirm it's the **experimental** build the game needs).
- Installs UE4SS directly from the `experimental-latest` GitHub release, correctly
  filtering out `zCustomGameConfigs.zip`, `zDEV-*.zip`, `zMapGenBP.zip`, and the source
  archives — only the real `UE4SS_v*.zip` asset is downloaded.
- Installs mods from **.zip, .7z, and .rar** archives (or a plain folder), drag-and-drop,
  or the file picker.
- When an archive contains **multiple self-contained versions** of one mod (e.g. x2/x5/x10
  multiplier folders), it detects that and asks which one to install instead of guessing.
- **View Files**: shows a folder tree of exactly what's inside each installed mod.
- Installs/uninstalls/enables/disables patch mods, logic mods, and lua mods:
  - **Disable** = pak-based mods get moved out of the game folder into
    `%AppData%\DDS2ModManager\DisabledMods\` (UE4 loads any pak it finds, so a config
    flag alone can't disable them). Lua mods just get flipped to `0` in `mods.txt`.
  - **Uninstall** = permanent delete + `mods.txt` cleanup for lua mods.
- Uses CUE4Parse to open each mod's `.pak`/`.ucas`/`.utoc` and read its virtual asset
  paths — that's how it tells LogicMods (has `ModActor.uasset`) apart
  from PatchMods, and how it detects when two mods edit the same file. **If CUE4Parse
  can't verify a mod's type, installation is blocked** rather than guessing (a wrong
  guess would put a LogicMod in the Paks folder where it silently won't load).
- **Conflict checking is automatic** — it re-runs after every install, import, enable,
  disable, uninstall and reset, so the panel always reflects the current state. There's also a
  **Re-scan Mod Files** button that re-reads every pak from disk, for the uncommon case where a
  mod's files changed outside the manager.
- **Mod update checking** — mods can publish their own update address, and the manager
  checks it for newer releases. See [Mod updates](#mod-updates) below.
- **Finds mods you installed by hand** before ever using this manager (automatically on
  startup, or via the **Find Existing Mods** button). It reads each one's pak to work out
  what it actually is, flags anything that's in the wrong folder (a LogicMod sitting in
  `Content\Paks` silently never loads), and offers to import them — which leaves the files
  where they are and just starts tracking them, so they become enable/disable/uninstallable
  and get included in conflict checks. Base-game paks and UE4SS's own built-in mods are
  excluded, so only real user mods show up.
- **Mod auto-updating** for mods whose author opted in. A LogicMod declares a `ModUpdateUrl`
  string variable on its ModActor; lua and patch mods ship a `.dds2mod.json`. Either way the
  manager reads it from what's already installed, so a mod installed long before this feature
  existed still gets picked up — nothing has to be re-downloaded to become updatable. See
  [MODDING.md](MODDING.md) for the author-facing side.
  - **Only `github.com` is accepted.** The URL comes from inside the mod, and installing an update
    means putting executable code on the user's machine, so anything that isn't unambiguously a
    GitHub repository is refused rather than guessed at (`GitHubUrlParser`).
  - **Nothing installs without being asked**, whatever the trust setting. The prompt shows the
    version, the release notes, and *which repository the download comes from*.
  - **Trusted / Verified.** "Trusted" is a local per-user decision, granted per GitHub account
    since whoever controls the account controls every release under it. "Verified" is a curated
    list the maintainers publish in [verified-mods.json](verified-mods.json), fetched on startup
    and cached for offline use. Neither one skips the install prompt — an account can be
    compromised and a curated list can go stale, and either silently installing code would be far
    worse than one click.
- **Brando's Mods** (under **More**): everything `brando136` has published on Nexus, with
  pictures, versions and download counts. It's the Nexus index the app already keeps for the
  hover cards, filtered to one uploader — the public GraphQL API rather than page scraping, so it
  can't break when Nexus restyles a page. It links out rather than installing: Nexus doesn't hand
  download links to automated clients, which is the same constraint that made mod updating use
  each mod's own GitHub releases.
- **Save Log** button exports the on-screen log to a `.txt` you can attach to bug reports.
- **Windows integration** (in Settings, no admin needed): add an "Open with DDS2 Mod
  Manager" right-click entry for archives, and/or a Desktop/Start Menu shortcut.
- **Self-updating**: checks `github.com/631Brando/DDS2ModManager` releases on startup
  (toggleable in Settings) and via a "Check for Updates Now" button. The prompt shows the
  release's changelog so you can see what's changing before agreeing, then it downloads and
  replaces itself in place.
- **Saves & Config** window: lists every save game with its size and last-played time, and
  can clone, delete, or disable/enable them (disabling moves the save out of the game's save
  folder, which is the only reliable way to hide it). Also a raw editor for the game's `.ini`
  config files, which backs up the original the first time you save so you can always revert.
- **Reset Game to Vanilla** (Settings): removes mods from the game itself — tracked mods,
  untracked mod files, optionally UE4SS, optionally the config files. Each part is opt-in and
  **saves are never touched**. Not to be confused with **Reset App Data**, which does the
  opposite: clears only this manager's own settings/tracking and leaves every mod installed.
- **Reset App Data** / **Uninstall** (Settings): recovery paths if something ever gets
  into a bad state, or to cleanly remove the app - see [Uninstalling](#uninstalling) below.
- A Settings page lets you override the game path, the mappings file, the CUE4Parse
  `EGame` version (for future engine updates), and an AES key (for encrypted paks).

## Before you build

This project targets **`net10.0-windows`**, required by `CUE4Parse` `1.2.2.202607`
(that package only ships `net10.0` assets - it will not restore against `net8.0` or
`net9.0`).

> **IDE note:** building a `net10.0` target requires **Visual Studio 2026**, or the
> `dotnet` CLI with the .NET 10 SDK installed. Visual Studio 2022 can *edit* the
> project fine but can only *build* targets up to `net9.0` - if you're on VS 2022,
> use `dotnet build` / `dotnet publish` from a terminal instead, or upgrade to VS 2026.

1. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) if you
   haven't already.
2. Open `DDS2ModManager.sln` in Visual Studio 2026 (with the ".NET desktop development"
   workload), or just work from the CLI.
3. `src/Assets/mappings.usmap` holds the game's type mappings, which CUE4Parse needs to make
   sense of `.pak`/`.utoc` contents. It's embedded into the exe at build time
   (`<EmbeddedResource>` in the `.csproj`), so end users never have to supply one — and it can
   be overridden at runtime from the Settings page.

   Note that this file is generated from Drug Dealer Simulator 2's own data and isn't covered by
   this repository's licence; it belongs to the game's authors. If you fork this for another
   game, replace it with mappings for that game.
4. Build. NuGet should restore `CUE4Parse` and `CommunityToolkit.Mvvm` automatically.

Both projects pin `x64` and `win-x64` themselves, so you don't need to pass `-p:Platform=x64`
or pick a platform in Configuration Manager — CUE4Parse's native dependencies require x64 and
that's already the only option. `Directory.Build.props` also strips the platform and RID folders
out of the output path, so however you build (CLI, solution, or Visual Studio) everything lands
in one place:

```
src/bin/<Configuration>/net10.0-windows/           # build output
src/bin/<Configuration>/net10.0-windows/publish/   # single-file publish
```

## Installing

Download and run `DDS2ModManagerSetup.exe` from the
[latest release](https://github.com/631Brando/DDS2ModManager/releases/latest). It fetches
the current `DDS2ModManager.exe` release asset itself, so the installer rarely needs to be
re-downloaded even across app updates - it always pulls "latest" at install time. It asks
for an install folder (defaults to `%LocalAppData%\Programs\DDS2ModManager`, no admin
needed) and whether to create Desktop/Start Menu shortcuts, then adds a normal Windows
"Apps & Features" entry.

Once installed, the app checks for updates itself (see below) - you generally don't need
to re-run the installer again.

### Uninstalling

Either use Windows Settings → Apps → DDS2 Mod Manager → Uninstall, or the "Uninstall DDS2
Mod Manager..." button in the app's own Settings. Both remove the installed program,
shortcuts, and the Apps & Features entry, but deliberately leave
`%AppData%\DDS2ModManager` (your settings, mod tracking, logs, and any files cached under
`DisabledMods`) and your installed mods untouched, in case you reinstall later. Use
"Reset App Data" first if you also want that folder gone.

## Building from source / publishing a single portable exe

`SelfContained`, `RuntimeIdentifier`, `PublishSingleFile`, and
`IncludeNativeLibrariesForSelfExtract` are already set in `DDS2ModManager.csproj` /
`DDS2ModManagerSetup.csproj`, so publishing either project is just:

```
dotnet publish src/DDS2ModManager.csproj -c Release
dotnet publish setup/DDS2ModManagerSetup.csproj -c Release
```

Each produces a single self-contained `.exe` under `bin/Release/net10.0-windows/publish/`
(native WPF interop DLLs are bundled inside and self-extract to a per-app `%TEMP%` cache
at first run - invisible to the user). The only file that appears alongside
`DDS2ModManager.exe` afterward is `oodle-data-shared.dll`, which the app downloads and
caches there itself on first run (see Oodle, below) - that's expected, not a packaging leftover.

## Releasing (for maintainers)

`.github/workflows/release.yml` builds both exes and publishes them as assets on a
GitHub Release whenever a tag matching `v*` is pushed.

### Two channels

| Channel | Branch | Tag | Published as |
|---|---|---|---|
| Stable | `main` | `v1.2.0` | normal release, becomes GitHub's "Latest" |
| Experimental | `experimental` | `v1.2.0-exp.1` | GitHub **prerelease** |

```
git tag v1.2.0 && git push origin v1.2.0            # stable
git tag v1.2.0-exp.1 && git push origin v1.2.0-exp.1  # experimental
```

**The version comes from the tag**, not from the `.csproj` — the workflow passes
`-p:Version=` when publishing, so the two can't drift and a forgotten bump can't ship a build
that misreports its own version. The `<Version>` in each `.csproj` is only the default for
local builds.

The `-exp.N` suffix becomes the build's **fourth version component**: `v1.2.0-exp.3` builds as
`1.2.0.3`. That's what makes channel switching work with plain version comparison — an
experimental build always sorts above the stable release it came from (`1.2.0.3 > 1.2.0.0`) and
below the next stable one (`1.2.0.3 < 1.2.1.0`), so an experimental user is moved back onto
stable automatically once stable catches up. `AppUpdateService` depends on this; don't change
the tag format without reading the comment there.

Marking experimental builds as prereleases is what keeps the channels apart: GitHub's
`/releases/latest`, which the stable channel asks for, skips prereleases. The experimental
channel lists all releases and takes the highest version.

The workflow **rejects** any tag that isn't `v1.2.0` or `v1.2.0-exp.1` shaped, so a typo fails
the build instead of publishing a mislabelled release.

### Asset naming

`AppUpdateService` (the in-app updater) and `DDS2ModManagerSetup` both compare the
running assembly version against the release tag name and expect an asset named exactly
`DDS2ModManager.exe` - the workflow already produces that name, so don't rename it without
updating `AssetName` in both `src/Services/AppUpdateService.cs` and
`setup/MainWindow.xaml.cs` to match.

## Mod updates

Mods are distributed through Nexus, but they can declare **their own update address**, and
the manager checks that rather than the Nexus API. That means no API key to enter, no
2,500-request daily cap, and no premium account — Nexus only issues download links through
its API to premium members, which would have made one-click updates a paid feature.

### For mod authors: how to opt in

Two ways, depending on what your mod is.

**LogicMods** — add a **string variable called `ModUpdateUrl`** to your `ModActor`, with your
repository as its default value. Add `ModVersion` too: without it the manager has nothing to
compare a release against and will never offer an update. No extra files — you ship the same
`.pak`/`.ucas`/`.utoc` as before.

```
ModUpdateUrl  =  https://github.com/yourname/YourMod
ModVersion    =  1.2.0
```

**Patch mods and lua mods** have no `ModActor`, so ship a `<YourMod>.dds2mod.json` next to
the mod instead:

```json
{
  "schema": 1,
  "name": "Your Mod",
  "author": "yourname",
  "version": "1.2.0",
  "updateUrl": "https://github.com/yourname/yourmod"
}
```

An earlier version of this guide spelled the key `modUpdateUrl`. That is still read, so
manifests already published keep working, but new ones should use `updateUrl`. Field names
are matched case-insensitively. See [MODDING.md](MODDING.md) for every field.

For lua mods, put it anywhere in your mod's folder. For pak mods, the file **must** be named
after the mod (`MyMod.dds2mod.json`) — pak mods all share `Content\Paks\LogicMods`, and
without the name match the manager could pick up a neighbouring mod's manifest and offer
updates from someone else's repository.

**The address itself** may be written several ways — the full `https://github.com/you/YourMod`,
with `.git` or a deep link on the end, without the scheme, or as the short `you/YourMod`. They
all resolve to the same repository, and only `github.com` is accepted. Two sharp edges are worth
knowing: the short form breaks if the name contains a dot, and reformatting a working address in
a later release reads as the address having *moved*. All of it is spelled out in
[What the address may look like](MODDING.md#what-the-address-may-look-like).

Publish releases with the version as the tag (`v1.2.0` or `1.2.0`), and attach the mod as a
single `.zip`/`.7z`/`.rar`. The release description is shown to users as the changelog. A bare
`.pak` is recognised as a new version but can't be unpacked, so users are told an update exists
and given a link to fetch it themselves.

### Trusting an author

Each mod has its own **Trusted** tick in the mod list, and the update prompt offers the same
thing. What it grants is trust in the **GitHub account**, not in that one mod — whoever holds
the account holds every release published under it, so trusting one of an author's mods but
not another would be a distinction without a difference. Ticking one row therefore lights up
that author's other mods too, and the tooltip says so rather than letting it look like a bug.

**Trust never skips the prompt.** There is no setting that makes it, and that is deliberate:
an account can be compromised and the curated Verified list can go stale, and either of those
installing code silently would be far worse than one extra click. What trust changes is how
much the prompt has to explain about where the download is coming from.

A mod whose update address has **changed** since you installed it overrides all of this. The
tick is disabled, the prompt leads with the warning, and no update is offered until you have
confirmed the move was expected — that is what a hijacked update channel looks like, and it
is the one situation where trust would be worth stealing. Trust cannot follow a moved address
in any case: it is keyed to the account, and a new address is not the account you trusted.

### Limits and safety

- **GitHub only.** Any other host is rejected, and so is plain `http`. An arbitrary URL field
  would be a malware delivery channel; a `github.com`-only field is a public repo anyone can
  read before running it.
- **These updates do not pass through Nexus's virus scanning.** That is the real trade for
  avoiding the Nexus API. So nothing is ever downloaded without a prompt that shows the source
  URL and the release notes, and the new file is downloaded *before* the old version is
  removed — an interrupted update leaves your mod working.
- The URL is pinned at install time. If a later version points somewhere different, the
  manager flags it rather than following it quietly.
- Results are cached for six hours. Unauthenticated GitHub allows 60 requests an hour per IP.
- A mod that declares no version is never reported as out of date — "we can't tell" is not
  the same as "up to date".

## Known gotchas

- **Archive formats**: .zip/.7z/.rar are handled via SharpCompress. Password-protected
  archives aren't supported (mod authors almost never use them) - extract those manually
  first, then point the manager at the folder.

- **Oodle**: UE5 `.ucas`/`.pak` content is Oodle-compressed. CUE4Parse needs
  `oo2core_9_win64.dll` to decompress it. Some games ship a loose copy; DDS2 links Oodle
  into its exe, so there's no loose DLL to grab. On first run the manager copies one from
  the game if present, otherwise **downloads the correct DLL automatically** via CUE4Parse
  and initializes it. If Oodle isn't available, mods mount but report **0 files** — which is
  the "found 0 files / installation blocked" error. Check the startup log for "Oodle
  decompression ready."
- **IoStore mods need the game present**: modern `.utoc`/`.ucas` mods don't carry their own
  name table — they reference the game's `global.utoc` in `Content\Paks`. The manager
  therefore analyzes each mod **in the context of the real game** (it briefly stages the
  mod's files next to `global.utoc`, mounts everything together, then reads back only the
  paths that came from the mod's own archive reader(s) - not a diff against the rest of the
  mount, since a mod overriding an already-installed path, or two mods genuinely colliding
  on the same path, would otherwise look identical to "nothing new"). This is why the game
  must be installed and the Paks folder must exist.
- **AES-encrypted paks**: DDS2's base game paks might be encrypted; mod-author paks
  almost never are. If CUE4Parse can't read a mod's pak (wrong EGame, Oodle unavailable,
  wrong/missing AES key, unsupported container format), installation is **blocked** rather
  than guessing the mod's type from its filename - a wrong guess would put a LogicMod in
  the Paks folder where it silently never loads.
- **"Last mod wins" conflict guess**: real UE pak mount priority isn't guaranteed to be
  alphabetical. The compatibility checker's "likely winner" is a clearly-labeled
  best-effort heuristic, not a guarantee — if it matters, test in-game.
- **LogicMods folder**: `Content\Paks\LogicMods` doesn't exist until the game has been launched
  once with UE4SS installed. Nothing needs doing about it — the manager creates it when it
  installs a logic mod, and UE4SS creates it itself on first run.

## License

[MIT](LICENSE). Do what you like with it, keep the copyright notice, no warranty.

The dependencies are all permissive and compatible with that:
[CUE4Parse](https://github.com/FabianFG/CUE4Parse) (Apache-2.0), CommunityToolkit.Mvvm,
SharpCompress and Microsoft.Bcl.Memory (MIT).

The MIT license covers the code in this repository. It does **not** cover
`src/Assets/mappings.usmap`, which is generated from Drug Dealer Simulator 2's own type
information and belongs to the game's authors — see the note in the build steps above.

## Project layout

```
DDS2ModManager/
  LICENSE
  DDS2ModManager.sln
  .github/workflows/release.yml  - builds + uploads both exes on a "v*" tag push
  src/
    DDS2ModManager.csproj
    GlobalUsings.cs
    App.xaml / App.xaml.cs
    MainWindow.xaml / MainWindow.xaml.cs
    Models/          - ModType, ModInfo, GameInstallation, UE4SSInstallInfo, ModConflict, AppSettings
    Services/         - Logging, GameDetection, Oodle, MappingsProvider, GitHubRelease,
                        UE4SSManager, LuaModConfig, ModRegistry, ModInstaller, AppSettings,
                        GameMountService (shared CUE4Parse mount used by all three readers),
                        ModAnalyzer / CompatibilityChecker / UnmanagedModScanner,
                        SaveGameService / GameConfigService / GameResetService,
                        ShortcutCreator (shared .lnk creation) / ShortcutService,
                        AppUpdateService / SelfReplaceHelper / AppUninstaller
    Converters/       - WPF value converters for the dark theme UI
    ViewModels/       - MainViewModel (MVVM via CommunityToolkit.Mvvm)
    Views/            - SettingsWindow
    Assets/           - mappings.usmap (game type mappings; see License)
  setup/
    DDS2ModManagerSetup.csproj  - the installer; links a few dependency-free Services
                                  files from src/ instead of referencing the main project
    App.xaml(.cs) / MainWindow.xaml(.cs)
```
