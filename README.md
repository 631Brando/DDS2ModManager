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
- **Two conflict checks**: a fast one that runs automatically after every change, and a
  **Deep Scan** button that re-reads the pak files exactly as they sit installed in the
  game folders — the authoritative check to run before launching.
- **Save Log** button exports the on-screen log to a `.txt` you can attach to bug reports.
- **Windows integration** (in Settings, no admin needed): add an "Open with DDS2 Mod
  Manager" right-click entry for archives, and/or a Start Menu shortcut.
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
3. **Replace `src/Assets/mappings.usmap`** with your real mappings file — it currently
   ships as an empty placeholder. It's embedded into the exe at build time
   (`<EmbeddedResource>` in the `.csproj`), so end users never need to supply one.
4. Set the solution platform to **x64** (Configuration Manager, or `-p:Platform=x64`
   on the CLI) — CUE4Parse's native dependencies require it.
5. Build. NuGet should restore `CUE4Parse` and `CommunityToolkit.Mvvm` automatically.

## Publishing a single portable exe

```
dotnet publish src/DDS2ModManager.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

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
  mod's files next to `global.utoc`, reads what they add, then removes them). This is why
  the game must be installed and the Paks folder must exist. Reading a mod in isolation
  (outside the game) is exactly what produced the earlier 0-files failure.
- **AES-encrypted paks**: DDS2's base game paks might be encrypted; mod-author paks
  almost never are. If CUE4Parse throws while analyzing a specific mod, the analyzer
  falls back to filename heuristics (checks for a literal `ModActor.uasset` on disk)
  rather than blocking the install outright — you'll get a warning in the log instead.
- **"Last mod wins" conflict guess**: real UE pak mount priority isn't guaranteed to be
  alphabetical. The compatibility checker's "likely winner" is a clearly-labeled
  best-effort heuristic, not a guarantee — if it matters, test in-game.
- **LogicMods folder**: doesn't exist until the game has been launched once with UE4SS
  installed. The app detects this and tells the user to launch/close the game first;
  there's also a "Create LogicMods Folder" button if you'd rather create it manually.

## Project layout

```
DDS2ModManager/
  DDS2ModManager.sln
  src/
    DDS2ModManager.csproj
    GlobalUsings.cs
    App.xaml / App.xaml.cs
    MainWindow.xaml / MainWindow.xaml.cs
    Models/          - ModType, ModInfo, GameInstallation, UE4SSInstallInfo, ModConflict, AppSettings
    Services/         - Logging, GameDetection, Oodle, MappingsProvider, GitHubRelease,
                        UE4SSManager, LuaModConfig, ModAnalyzer (CUE4Parse), ModRegistry,
                        ModInstaller, CompatibilityChecker, AppSettings
    Converters/       - WPF value converters for the dark theme UI
    ViewModels/       - MainViewModel (MVVM via CommunityToolkit.Mvvm)
    Views/            - SettingsWindow
    Assets/           - mappings.usmap (replace with your real file)
```
