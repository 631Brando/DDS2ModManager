# Changelog

Each version's section is published verbatim as that release's notes on GitHub, and is what the
in-app "Update available" prompt shows before you agree to install it.

## v1.2.0

### Keyboard shortcuts

The things you reach for repeatedly now have keys:

| | |
|---|---|
| `Ctrl+F` | Search the mod list (again to replace what you typed) |
| `Esc` | Clear the search |
| `Ctrl+O` | Install Mod… |
| `F5` | Re-scan Mod Files |
| `Ctrl+U` | Check Mod Updates |
| `Ctrl+S` | Saves & Config |
| `Ctrl+P` | Profiles |
| `Ctrl+L` | Show/hide the log |
| `Ctrl+,` | Settings |

Each one is repeated in its button's tooltip, since a shortcut you can't discover may as well not
exist. There are deliberately **no shortcuts for uninstalling or resetting** — a key pressed with
the grid focused and the wrong row selected is exactly the mistake a shortcut shouldn't make easy.

### The mod list explains itself when it's empty

A fresh install showed bare column headers and nothing else. It now says how to add a mod — drag
an archive on, or use Install Mod… — and points at **Find Existing Mods** for anyone who already
modded by hand. It appears only when nothing is installed, never when a search simply matched
nothing; that case already has its own answer next to the search box.

### Fixed: disabling a Lua mod could take the app down with it

Enabling and disabling handled failure for pak mods but not for Lua ones, where the write to
`mods.txt` was left unguarded. A read-only or locked `mods.txt` therefore threw straight out
through the app rather than being reported.

For a two-part mod it was worse than a crash: the halves are toggled one at a time, so the pak
half would flip and the lua half would throw, leaving exactly the half-enabled state the manager
goes out of its way to prevent — and no undo recorded, because that only happens once both halves
are done. Both paths now report the failure and leave the mod as it was.

### Fixed: one unreadable Steam file could hide your game

Auto-detection reads Steam's list of library folders to find installs on other drives. That read
was unguarded, and Steam rewrites the file while it runs, so a locked or unreadable copy aborted
detection completely — reporting "could not auto-detect the game" to someone whose game sits in
the default Steam folder the manager had already found. It now warns and carries on with what it
knows.

## v1.1.2

### Trusted Mods

The **Brando's Mods** page under **More** is now **Trusted Mods**, and lists every DDS2 mod
published by any author on a curated list rather than being tied to one account. Each row names
its author and links to their profile, with a filter to narrow the list to one of them.

It's sortable: **most downloaded** (the default), most endorsed, recently updated, newest, name or
author. Downloads leads because the page is for finding mods you haven't heard of, and there
"what is everyone already using" is a more useful opening answer than "what did someone touch most
recently" — sorted by date, a one-line tweak republished this morning sits above a mod with
thousands of users.

Starting authors are brando136, mifsopo and huslaa. The list is fetched at runtime rather than
built into the app, so authors can be added without a new release.

**"Trusted" here means a recommendation, not a safety check**, and it is deliberately a separate
list from the verified-source list the updater uses. That one names GitHub accounts and decides
how a download is described to you; this one names Nexus accounts and only decides whose mods
appear on a browsing page that never downloads anything. Keeping them apart means adding someone
to help people find good mods can never widen what the updater will install. There's a "Browse all
DDS2 mods" button alongside, because a curated list with no way past it quietly suggests nothing
outside it is worth looking at.

## v1.1.1

### The experimental channel could hand you older code and call it an update

An experimental build is numbered above the release it previews — `v1.1.0-exp.1` ships as
`1.1.0.1`, while stable `v1.1.0` ships as `1.1.0`. The updater compared those numbers directly,
so it believed the preview was newer than the finished release.

Two things went wrong as a result. Switching to Experimental after v1.1.0 shipped would have
offered you `v1.1.0-exp.1` as an update, which is *older* code — everything added between the
preview and the release would have been removed. And anyone already on a preview was stranded
there: the stable release that superseded them scored lower, so it was never offered, and they'd
have waited for v1.2.0 to get moved along.

A preview is now correctly treated as coming *before* the release it previews, so the order runs
`v1.1.0-exp.1` → `v1.1.0` → `v1.2.0-exp.1`, and stable catching up moves experimental users
forward automatically the way it was always meant to.

### It now tells you when experimental is behind stable

Between a stable release and the next preview, the experimental channel has nothing newer than
stable — it's behind, not ahead. You can't work that out from the two version numbers, since the
preview carries the bigger one.

Settings now shows where both channels actually stand, and says plainly when experimental is
behind and that switching would gain you nothing. If you're already on experimental, it explains
why no update ever arrives, rather than leaving it looking broken.

Moving from a preview onto the release that superseded it also no longer calls itself a
downgrade. The version number gets shorter (`1.1.0.1` → `1.1.0`) while the code moves forward, so
it says that instead of warning you that features are about to disappear.

Version numbers are also written the way the release is named — `v1.1.0` rather than `v1.1.0.0`.

### Also

- Mifsopo's Nexus profile is linked on the credits page.

## v1.1.0

The largest release so far, and the stable version of the work that went out as `v1.1.0-exp.1`
plus the fixes that came back from it. If you're on the experimental channel you can switch back
to Stable in Settings and stay on this build; it's the same code.

### Hover a mod to see what it is

Hovering a mod in the list now shows its Nexus picture, description, author, version and download
count. Nothing is fetched while you hover — the list of the game's mods is downloaded once, cached
locally, and refreshed every few days, so the cards work offline and cost nothing to open.

Only mods published on Nexus get a card, matched by name. Roughly half a typical setup is mods
that were never published — your own work, test mods, one half of a two-part mod — and those
simply show nothing, which is normal rather than a failure. The matching is deliberately strict:
it would rather show you nothing than show you a stranger's mod on your own row.

### The mod list

- **Search** across names, type, author, version, your tags and notes, and Nexus descriptions.
- **Two-part mods are linked.** A mod that installs both a pak and a lua script is now marked as
  one thing. Enabling or disabling it applies to both halves — half a mod enabled is the worst
  possible state and gives you no error to work from. Uninstalling asks first.
- **Select several mods** and enable, disable or uninstall them together.
- **Star** mods to keep them at the top.
- **Notes and tags** of your own on any mod. They're yours, not the mod's: updating or
  reinstalling a mod never wipes them, which is the point if the note says "breaks saves".
- The list remembers how you sorted it.

### Updates

- **Update All**, which still asks about each mod. They come from different authors' repositories,
  so being taken through them is the point.
- **History** of what changed, keeping the release notes from each update you applied. Those were
  previously shown once and thrown away.
- **Re-check a single mod** immediately, ignoring the six-hour cache — useful if you've just
  published something.
- **A warning when the game itself has been patched** since you last opened the manager. Nothing is
  disabled; a game update can recook the content a pak mod replaces, and if something breaks today
  that's the first thing worth knowing.

### Safety and diagnostics

- **A copy of a mod is kept before an update replaces it.** Downloads already happened before
  anything was removed, but that never helped when the update installed fine and the new version
  was simply worse — and authors delete old releases.
- **Files changed outside the manager are flagged**, so a mod you edited by hand (or a half-finished
  copy) is visible rather than mysterious.
- **Undo** for enabling and disabling.
- **Diagnostics** — one zip with your logs, mod list, conflicts and versions, for bug reports. It
  contains no save games and no config files.
- **Profiles** — save which mods you had on, and come back to it later. Applying a profile only
  switches mods you already have on and off; it never downloads, installs or deletes anything.
- **Find which mods contain a file**, for when one specific thing is broken.
- Mod sizes, a **Play** button, and a Nexus link on each row.

---

The rest of this release is the work described below, which merged two independent implementations
of mod auto-updating and fixed several problems found along the way.

### Mod updates: two implementations merged into one

Mod auto-updating was built twice, independently and at the same time, and this merges them. The
discovery mechanism was the same on both sides — a `ModUpdateUrl` variable on a LogicMod's
ModActor, or a `.dds2mod.json` for everything else — so what differed was everything around it.
The result keeps the stronger half of each:

- **URL parsing** now refuses anything that isn't unambiguously a GitHub repository, including
  lookalike hosts and `https://github.com@evil.example.com/x/y`, and validates the owner and repo
  characters rather than pasting whatever it found into an API path.
- **Rate-limit discipline**: results cached for six hours, and a run stops after three consecutive
  failures instead of turning one outage into thirty log lines. Unauthenticated GitHub allows 60
  requests an hour, and "the check succeeded" stays distinct from "there is an update".
- **Asset selection**: a release with no single obvious file is skipped rather than guessed at.
  Authors can name one with the `asset` field.
- **A curated Verified list**, published by the maintainers and cached for offline use, alongside
  the per-user trust tick.

### Trust is per account, describes the address, and never skips the prompt

The **Trusted** tick now grants trust to the GitHub *account* rather than to one mod — whoever
holds the account holds every release under it, so ticking one of an author's mods lights up
their others too. The tooltip says so rather than letting it look like a bug.

What Trusted and Verified actually mean is *where the download comes from*, not who wrote the mod.
A mod declares its own update address and nothing is signed, so the manager never learns an
author's identity — it reads a string the mod handed it, and any mod can name any account. The
claim gains an impostor nothing, since updates would then be fetched from the real account's
repository, which they don't control. But the wording used to read as though authorship had been
checked, and now says only what is known.

The setting that let trusted mods update without asking has been **removed**, not defaulted off.
A mod update is executable content from the author's own repository that Nexus never scanned, and
a lua mod runs code in the game's process. An account can be compromised and a curated list can
go stale, and either of those installing silently would be far worse than one click. Trust now
changes how much the prompt has to explain, never whether there is one.

A mod whose update address has changed since it was installed still overrides all of it: nothing
is offered until you confirm the move was expected.

### Fixed: upgrading could silently empty your mod list

Loading a registry written by an earlier build threw, and that failure was caught by a handler
that started with an empty list — so every tracked mod disappeared with no message, and the next
save overwrote the file that still held them. The mod files themselves were never touched, but
the manager forgot all of them.

Old registries now load. One that genuinely can't be read is reported rather than silently
discarded, and is kept alongside the new one instead of being overwritten.

### Also in this release

- **Mod loader DLL warning.** A UE4SS `dwmapi.dll` sitting beside the exe gets loaded into the
  manager instead of into the game, and its hooks stop WPF drawing — which presents as a blank
  white window above a perfectly healthy log. Nothing can prevent the load once it has happened,
  but the app now names the cause and explains the fix.
- **Stable and experimental update channels.** `v1.2.0-exp.1` builds as `1.2.0.1`, so an
  experimental build sorts above the stable release it came from and below the next one.
- **Brando's Mods** (under **More**): everything the maintainer has published on Nexus, with
  pictures, versions and download counts, and a badge on the ones you already have. It links out
  rather than installing — Nexus only hands download links to premium members, so an Install
  button here would promise something that can't work.
- **Credits**, also under More: who made this, and the projects it's built on.
- Manifest field names are read case-insensitively, and the earlier `modUpdateUrl` spelling is
  still accepted, so manifests already published keep working.
- Releases are now gated on the test suite.

### Fixed since v1.1.0-exp.1

- **Hover cards came up with an empty band where the picture should be.** The picture has to be
  downloaded, and the binding that asked for it could only answer immediately — so it answered
  "nothing" and the image that arrived a moment later had nowhere to go. Pictures now appear.

## v1.0.6

### Cloned saves didn't show up in game

The most important fix in this release. Cloning treated a save as just a folder of files, but a
DDS2 cartel also records its own name *inside* `CartelDefaults.sav`, and the game uses that name
to find the progress file. A copy therefore kept pointing at the original's progress file, which
isn't in the new folder — so the game found nothing, skipped the cartel, and the copy never
appeared in the load list at all. No error, it was simply missing.

Clones now have their internal name updated to match, and are checked afterwards against the rule
every working cartel follows: the folder name, the name recorded inside the save, and the
`<name>_Progress.save` file all have to agree. If they somehow don't, it says so rather than
leaving you to find out at the load screen.

If you already have clones that won't load, they stay broken — this fixes new ones. The quickest
repair is to clone the original again now.

The copy also gets its own name in the game's load menu. Previously a clone could show up under
the original's name, which made the two impossible to tell apart.

### Steam Cloud warnings

DDS2 syncs its save folder with Steam Cloud, which quietly works against everything on this
screen: Steam reconciles the whole folder each time the game starts or stops, and it can copy in
either direction. Anything you change here — cloning, deleting, disabling, editing — can simply be
undone on the next launch, with no indication that it happened.

The Saves tab now detects this from Steam's own files (it checks that Steam has actually synced
files from this folder, rather than assuming it from the game merely supporting cloud saves) and
says so up front, with the exact steps to turn it off. Delete, Disable and Clone each repeat the
warning at the moment it matters.

Disable gets a proper confirmation, because it's the riskiest of the three: it moves the save out
of the synced folder, which Steam can read as a deletion and drop from the cloud — and therefore
from your other machines.

Nothing here changes your Steam settings. Steam owns that sync and rewrites its own config while
running, so the app reports and explains rather than fiddling behind your back. Backups are
deliberately kept outside the synced folder, so they're the one thing Steam can't touch.

### Select more than one save

The saves list now supports multi-select — ctrl-click, shift-click, Ctrl+A. **Back Up**,
**Delete** and enable/disable all work across a whole selection, with one confirmation and one
summary instead of a dialog per save. Deleting several lists exactly what's going, with sizes,
because a bare count is too easy to misread.

Mixed selections behave sensibly: saves already in the state you asked for are skipped rather than
counted as failures, and if some of a batch fail the rest still complete.

### Saves screen tidied up

- **Enable** and **Disable** are now one button that shows which way it will go for what you've
  selected.
- Buttons that act on the selection grey out when nothing is selected, instead of answering a
  click with "select a save first".
- Consistent button sizing, and the actions that act on a selection are separated from the general
  ones.

### More of a save is readable

- **`UserSettings.sav` can be inspected.** It isn't a RamaSave file — it's a plain Unreal GVAS
  save — so it needed a separate reader. It holds your graphics and audio settings, DLSS mode and
  achievement progress. `CartelDefaults.sav` and `CartelLocalData.sav` are readable for the same
  reason, so each cartel's own settings can be viewed too.
- **Text is decoded properly.** Names, SMS bodies and quest overrides showed as a raw byte count
  before; now they show their contents.
- **Map keys and values are decoded** using the types the save declares, instead of being inferred.

## v1.0.5

### Save inspector

New **Inspect** button on the Saves tab of **Saves & Config**. It opens a searchable, read-only
view of what the game actually stored in a save — every persistent actor and every variable it
saved, as an expandable tree — plus **Export as Text** to dump the whole thing to a file.

DDS2 doesn't use Unreal's standard `GVAS` save format. It uses RamaSave, which writes its own
container and its own property records, which is why general-purpose Unreal save editors can't
open these files at all. The reader here works the format out directly, so things like a
hideout's stored substances, deployed equipment and property stats come out as readable values
rather than a wall of bytes.

Two things worth being clear about:

- **It never writes to your saves.** Writing one back would mean reproducing the compression,
  the record offsets and the internal markers byte-exactly, and getting that wrong corrupts a
  playthrough. This is a viewer.
- **It doesn't guess.** Every actor record in the file states where it ends, so each one is
  checked against its own declared end offset rather than assumed correct — if a record doesn't
  line up, the window says so instead of showing partial data as though it were complete. Where a
  value's type genuinely can't be known (eight zero bytes are identical whether they're the
  number 0 or an empty container), it's labelled as such rather than shown as a confident number.

Actor classes with many instances are grouped into one row, so a world with forty quest boxes in
it doesn't bury everything else.

### Tooltips were invisible

Hovering over a button showed a blank white bar with no text. Tooltips render in their own popup
using WPF's default light chrome, but still inherited the dark theme's light text colour — white
text on a white background. They're now styled to match the rest of the app.

### Three buttons that shouldn't have been there

- **Create LogicMods Folder** is gone. Installing a logic mod creates the folder if it's missing,
  and UE4SS creates it itself on first run. Relatedly, installing a LogicMod is no longer
  *blocked* when that folder doesn't exist yet — that was friction over a directory the manager
  can simply create.
- **Check Compatibility** is gone. Conflict checking already runs automatically after every
  install, import, enable, disable, uninstall and reset, so the button only ever re-did work the
  app had already done.
- **Deep Scan** is now **Re-scan Mod Files**, which describes what it actually does: re-read every
  installed mod's pak from disk. You only need it if a mod's files changed outside the manager.

### Internal

- Build output used to land in up to three different folders depending on how you built it
  (`bin\Debug\...`, `bin\x64\Debug\...`, each with an extra `win-x64\`), which made it easy to run
  a stale executable and conclude a change hadn't worked. Both projects hardcode x64 and win-x64,
  so those path segments carried no information; `Directory.Build.props` now pins a single output
  path per configuration. The README and the release workflow had drifted onto two different ones
  of those paths — both now point at the real one, and the workflow checks each asset exists
  before creating the release rather than publishing an empty one.

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
