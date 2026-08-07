# Changelog

Each version's section is published verbatim as that release's notes on GitHub, and is what the
in-app "Update available" prompt shows before you agree to install it.

## v1.0.4

### Conflict detection now understands what LogicMods actually do

The compatibility checker used to compare file paths only. That works for patch mods, which
replace whole files, but it's blind to how LogicMods work: they ship their own DataTables and
merge them into the game's tables at runtime, so two LogicMods never share a file path even when
they're both rewriting the same values.

It now reads each mod's ModActor Blueprint to find which tables it merges into and which rows it
contributes, then judges conflicts on what actually collides:

- **Two patch mods replacing the same file** — always a total conflict; only one version loads.
- **Two LogicMods contributing the same row** — a real conflict, and the exact rows are named.
- **Two LogicMods touching the same table but different rows** — correctly treated as compatible.
  This is the common case, and it previously looked identical to a real conflict.
- **A patch mod replacing a table a LogicMod adds rows to** — flagged as worth checking, since the
  outcome depends on load order.

Conflicts are colour-coded by severity and grouped into one card per pair of mods, listing the
contested rows. Pairs that were checked and found compatible aren't shown at all — they're noted
in the log instead of filling the panel.

### Import mods that were installed before the manager

If you modded by hand before using this, those mods were invisible to it — they couldn't be
enabled, disabled, uninstalled, or conflict-checked. It now finds them automatically on startup
(or via **Find Existing Mods**), reads each one to determine what it actually is, and offers to
import them. Importing leaves the files exactly where they are and just starts tracking them.

It also flags mods sitting in the wrong folder — a LogicMod in `Content\Paks` silently never
loads — and can move them somewhere correct. Base-game paks and UE4SS's own built-in mods are
excluded, so only real user mods appear.

### Save manager

New **Saves & Config** window listing every save with its size and last-played time:

- **Clone** — files named after the save are renamed to match the copy, so the game recognises it.
  A plain folder copy often won't load.
- **Back Up** — a timestamped copy kept outside the game's save folder, so it can't appear in-game
  as a duplicate and isn't exposed to whatever corrupted the original. Plus an **Open Backups
  Folder** button.
- **Disable / Enable** — moves the save out of the game's save folder entirely, which is the only
  reliable way to hide one, since the game reads whatever is in that folder.
- **Delete**, with confirmation.

Handles both save layouts — a folder per save (what DDS2 uses, nested under a container folder)
and single `.sav` files.

### Config editor

Edit the game's `.ini` files from the same window. The original is backed up automatically the
first time you save one, with a **Restore Original** button to undo. The empty placeholder `.ini`
files Unreal generates are hidden so the ones that matter aren't buried.

### Reset Game to Vanilla

New in Settings. Removes mods from the game itself, with each part opt-in: mods it tracks,
untracked mod files, UE4SS, and the config files. **Your saves are never touched.**

Not to be confused with **Reset App Data**, which does the opposite — it clears only this
manager's own settings and mod list, and deliberately leaves every mod installed.

### Update prompt shows the changelog

Instead of a bare "a new version is available", the prompt now shows the release notes for the
version you're about to install, with a link to the release page.

### Works with other Unreal Engine games

The game's project name is now detected from disk instead of assumed, and the save and config
folders are derived from it using the standard Unreal layout. Combined with the format-agnostic
save handling, the save manager, config editor and reset features work against other UE4SS-based
UE games, not just DDS2.

### Other changes

- Larger default window (1560x980), and the app now remembers the size you leave it at, including
  maximized — so you don't have to resize it every launch. Clamped to your screen's work area so
  it can't open larger than the display.
- Conflict data now refreshes automatically. Previously some results only appeared after manually
  pressing **Deep Scan**, because the row data was only ever collected there. It's now captured
  when a mod is installed or imported, and anything still missing it is refreshed on startup.
- The compatibility panel leads with a single status line ("No conflicts found" / "N conflicts
  need attention") rather than making you read every card to find out.

### Internal

- The CUE4Parse mount logic was duplicated across install-time analysis and conflict checking, and
  the new scanning would have made a third copy. It's now one shared `GameMountService` used by
  all of them, so they can't drift apart.
- Added this changelog, and the release workflow now publishes the matching section as the release
  notes — which is what the in-app update prompt reads.

## v1.0.3

- Fixed the UE4SS status showing "up to date" even when an update was available. The label was
  reporting "installed and verified as the experimental build", never whether it actually matched
  the latest release.

## v1.0.2

- Added an application icon.

## v1.0.1

- Added a UE4SS build picker: choose between the standard experimental build and the zDEV build,
  which opens a console window showing live logs. Both work identically for mods.

## v1.0.0

First public release.

- Auto-detects the game from Steam library folders, with manual browse as a fallback.
- Installs and updates UE4SS from the official experimental release.
- Installs mods from .zip/.7z/.rar archives, folders, drag-and-drop, or a right-click
  "Open with" entry, detecting multi-variant archives and asking which version you want.
- Enable, disable and uninstall per mod. Disabling physically moves pak files out of the game
  folder, because Unreal loads any pak it finds.
- Compatibility checking by asset path, plus a Deep Scan that re-reads the installed paks in place.
- Ships as a single self-contained .exe, with an installer and in-app self-updating from GitHub.
- Desktop and Start Menu shortcut options, and a right-click context menu entry for archives.
- Reset App Data for clearing the manager's own cached state.
