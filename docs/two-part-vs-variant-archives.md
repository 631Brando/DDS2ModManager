# Two halves of one mod, or two versions to pick between?

An archive with several self-contained mod folders inside it is one of two things, and the installer
has to decide which without asking a question the user can't answer:

- **Two halves of one mod.** A script half and a pak half, bound for two different game folders.
  Install both. Installing one gives a script calling into a pak that was never loaded.
- **Alternatives.** `x2` / `x5` / `x10` multiplier folders, all bound for the same folder. Install
  exactly one. Installing several mixes files from different versions of the same mod.

Getting it backwards in either direction is silent.

## What went wrong

Reported on Nexus, 2026-09-01: installing **DDS2 In-Game Wiki** offered `EddieWiki` and
`EddieWiki_P` as "versions" to choose between. They are the two halves —
`EddieWiki` → `Binaries\Win64\ue4ss\Mods\`, `EddieWiki_P` → `Content\Paks\LogicMods\`. Whichever the
user picked, they got half a mod and nothing on screen said so.

`ModArchiveLayoutService` exists to prevent exactly this and its header says so — but it recognises
a two-part archive only by **destination marker folder** (`UE4SSMods\`, `LogicMods\`, `Mods\`). This
archive names its folders after the *mod* instead, so no marker exists, and
`ModVariantDetectionService.DetectCandidates` fell through to a bare count of installable siblings.

Present in shipped `v1.2.0` — both services were unchanged since that tag — and it affects **any**
mod shipping a lua half and a pak half without marker folders, not just this one.

## The rule

`ModVariantDetectionService.DetectTwoPartSiblings`. Two conjuncts, ANDed. **Both are required.**

1. **One mod identity.** Every qualifying sibling's *folder name* reduces to the same key under
   `NexusModMatcher.KeyForInstalled` — the same reduction the mod list uses to group installed rows,
   so what the installer calls "one mod" and what the grid calls "one mod" cannot disagree.
2. **A destination partition.** Every part classifies as a *different* kind (pak-bearing vs
   lua-bearing), distinct-count equal to member-count.

| archive | (1) name | (2) kind | result |
|---|---|---|---|
| `EddieWiki` + `EddieWiki_P` | both `eddiewiki` ✓ | Lua, Pak ✓ | **both install** |
| `x2` / `x5` / `x10` | `x2`≠`x5`≠`x10` ✗ | all Pak ✗ | dialog |
| `Mod_x2_P` + `Mod_x5_P` | `modx2`≠`modx5` ✗ | both Pak ✗ | dialog |
| `MyMod` + `MyMod_P`, **both paks** | both `mymod` ✓ | both Pak ✗ | dialog |
| `MyMod` + `MyMod_Lua`, **both lua** | both `mymod` ✓ | both Lua ✗ | dialog |

**Why neither conjunct can be dropped.** `MyMod` beside `MyMod_P` where both carry a pak passes (1)
and is caught only by (2) — that's the `_P` chunk-priority convention, two alternatives bound for one
folder. A pak variant folder beside an unrelated lua helper passes (2) and is caught only by (1).

**Why there is no count rule.** A two-folder variant set (`Normal` / `Hardcore`) is ordinary, so
"exactly two means halves" would install both. The at-most-two cap falls out of (2) for free: there
are only two destination families, so three same-named folders can never all differ. **If
`IsInstallableRoot` ever grows DDS1's loose-asset or DLL-plugin shapes, that cap disappears and this
rule must be re-derived in the same commit.**

**Why `Mod_x2_P` survives.** `StripPackagingSuffixes` removes exactly one trailing suffix and then
stops, so the `x2`/`x5` discriminator stays inside the retained stem. `Normalise` keeps digits.
Both pinned by tests in `ModGroupingTests` — that key now decides what gets written into the game
folder, not just which Nexus card shows.

## Parts go in `DestinationParts`, never `VariantCandidates`

Each part needs its **own** `chosenRoot`. `InstallPakTriple` takes the first file it finds per
extension across every subdirectory, so one root spanning both trees would pick a `.pak` from one
half and a `.ucas` from the other — the exact file mixing this whole area exists to prevent.

## One name for the set

A manifest names the *mod*, and both halves **are** that mod — but only one half ships the file.
`EddieWiki` declares `"name": "DDS2 In-Game Wiki"` in its lua half and nothing beside its pak.

Without propagating it, the rows install as `DDS2 In-Game Wiki` and `EddieWiki_P`, whose grouping
keys differ. They'd never link, and enabling one would toggle half a mod — a worse outcome than the
bug being fixed. So `SharedPartName` reads the one name the parts agree on and both parts install
under it; the existing cross-type clash branch renames the second to `… (LogicMod)`, a suffix the
grouping key already strips.

Null when nothing declares a name (folder and pak names already reduce to the same key) or when two
parts declare *different* names — that's an archive saying they are not one mod.

A declared name is deliberately **not** a matching signal: a copy-pasted manifest across two genuine
alternatives would silence the check that correctly refuses them. Propagating a name across an
already-decided set is a different thing.

## Not done, on purpose

- **No persisted group key on `ModInfo`.** The grouping is derived precisely so it cannot go stale,
  and a stored key would only help pairs installed *after* the change — never the reporter's.
- **No rollback in the parts loop.** On a reinstall the already-present half legitimately fails the
  same-type clash check *because it is correctly installed*, while the other half succeeds as a real
  update. Rollback would delete the freshly updated half.
- **`ModArchiveLayoutService.KindOf` is not used here.** It walks *up* looking for a marker and
  returns `Unknown` for exactly this archive shape, which the installer turns into "couldn't
  determine a mod type" — it would refuse to install the mod this was written to install. It has no
  production callers; leave it alone or bound its walk in its own commit.

## Known, still open

`ModArchiveLayoutService` adds every installable child of a marker and only requires two parts
overall, so `LogicMods\x2\Mod.pak` + `LogicMods\x5\Mod.pak` is read as two parts and installs **both
multipliers**. That predates this fix and is not made reachable more often by it. The safe fix needs
to tell "several mods under one marker" (legitimate, and documented in that file) from "variants of
one mod under one marker", and to surface that choice from *inside* a marker — which the current
fall-through cannot do. **Do not add a test pinning the current behaviour as correct.**

An archive naming its halves `EddieWiki` and `EddieWikiPak` still reaches the dialog: the names don't
reduce to the same key. The dialog now tells the user to install the archive a second time and pick
the other folder, which does work.

## Accepted false positive

An author shipping `HUDTweak_P` and `HUDTweak_Lua` as genuine *either/or* alternatives passes both
conjuncts and gets both installed. That is structurally indistinguishable from a two-part mod by
filesystem inspection — `ModGroupingTests` already pins `BotanistExpansion_P` + `BotanistExpansion_Lua`
as a real pair. The failure is bounded: two rows, two destinations, no file blending, individually
disableable, and grouped so one toggle handles both. Better than a silently non-functional mod.
