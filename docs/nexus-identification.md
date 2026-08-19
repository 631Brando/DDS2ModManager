# How a mod gets tied to its Nexus page

Two routes, in a fixed order: **a link the user declared**, then **exact name matching**. Nothing
else. This file exists because the second route has a hard limit that is not obvious from reading it,
and because the first route was described in a comment for months before it was written.

## Why name matching is not enough

`NexusModMatcher` compares an installed mod's name to a Nexus title by exact normalised key
equality — lowercase, strip everything that is not `a-z0-9`, drop packaging suffixes (`_P`, `_Lua`,
`(LogicMod)`), then a dictionary lookup. A Nexus title contributes one or two keys: the whole title,
and the "head" before the first *spaced* dash, because authors append a marketing tail.

Measured against the real installs on 2026-08-18:

| | matched | not matched |
|---|---|---|
| DDS2 | 13 of 26 | 13 — all unpublished local work |
| DDS1 | 0 of 9 | 8 unpublished, **1 genuine failure** |

Every DDS2 hit works because the pak is named after the Nexus title: `BiggerPackages_P` → strip
`_P` → `biggerpackages` → mod 110 "Bigger Packages". That is a naming convention holding, not a
lookup that knows anything.

The genuine failure is **AERR**, and it fails twice over:

- `Normalise("AERR")` is `"aerr"` — 4 characters, below `MinimumKeyLength = 6`, so `Match` returns
  null before the dictionary is touched.
- Lowering that gate would change nothing. `"aerr"` is not among the 182 keys at all: mod 79 is
  titled *"AE Revolutions Reloaded"* and contributes only `aerevolutionsreloaded`. The author's
  acronym appears nowhere in their own page title.

**Fuzzy matching stays rejected.** It was measured at six similarity thresholds (0.75 down to 0.50)
against the real install and the real catalogue and found **zero** additional correct matches at
every one, while inventing wrong ones below 0.60. The mods it fails to match are simply not
published. See `NexusModMatcher`'s header. Do not lower `MinimumKeyLength` either — it buys nothing
here and un-pins a documented test.

## The declared link

`NexusModLink { ModId, GameDomain, Kind }`, stored on `ModInfo.NexusLink`, persisted in the per-game
registry beside `Notes` / `Tags` / `IsFavourite`. The user sets it in `LinkNexusModWindow`, by
pasting the mod's address (or just its number) or picking from the cached catalogue.

**Precedence, exactly as `NexusModMatcher.Resolve` evaluates it:**

1. `NexusLink == null` → name matching. Today's behaviour, and what every existing registry
   deserialises to.
2. `Kind == NoPage` → **null**. Matching is suppressed; the user said this mod has no page, which is
   a stronger statement than any name guess.
3. `IsUsable` but `GameDomain != activeDomain` → **null**. The link stays on disk untouched; it is
   simply not this game's.
4. `IsUsable` and the domain matches → the catalogue entry with that id, **or null if absent**.

**When both exist and disagree, the link wins and the match is never computed.** A user links a mod
*because* the name match was absent or wrong, so falling back to it on an unresolved link would
silently restore the exact thing being corrected.

The assignment in the refresh pass is unconditional, **including null**. That one detail is what
makes unlinking, re-pointing and `NoPage` take effect at all — nothing else in the codebase ever
sets `NexusInfo` back to null.

## Why the domain is stored

Nexus mod ids restart per game. Measured across the two live catalogues: **85 ids appear in both,
and not one of them shares a title.** Id 79 is *"AE Revolutions Reloaded"* on `drugdealersimulator`
and *"Gh0sted - Rebalance"* on `drugdealersimulator2`.

The registry file is already per-install, which is *not* the same guarantee: `AppPaths.GameKey` keys
on the install **path**, `ModRegistryService(string)` has no `GameInstallation` at all, and a Nexus
pass started under one game can finish after a switch. A record that cannot name its own game cannot
refuse when it is read under the wrong one — so it names it, and `Resolve` refuses.

## Two gates, deliberately

| gate | drives | needs |
|---|---|---|
| `HasNexusPage` | the row's `nexus` button | a domain and an id |
| `HasNexusInfo` | the hover card and its picture | a real catalogue entry |

They fail independently. The catalogue is cached for 3 days, so "published yesterday" and
"unmatchable" overlap exactly — a link to a brand-new mod gives a working button and no card, which
is honest. **Never fabricate a `NexusModPost` to collapse these into one gate**: the card binds
`Name`, `Downloads` and `Endorsements` with no emptiness guard, so a stub renders a blank title above
*"0 downloads · 0 endorsements"* — a false assertion about the user's own mod.

## Storage and compatibility

- **Properties, not fields.** `ModRegistryService`'s `JsonSerializerOptions` does not set
  `IncludeFields`, and `System.Text.Json` ignores public fields by default. Fields on
  `NexusModLink` would round-trip as `{}` and every link would vanish on the next launch, silently.
- **No `[property: JsonIgnore]` on `ModInfo.NexusLink`.** Its absence *is* the persistence. The
  runtime-only fields immediately below it (`NexusInfo`, `NexusThumbnail`) carry one, so copying by
  proximity is the easy mistake.
- **No converter and no schema bump.** `ModUpdateSourceJsonConverter` exists because a member once
  *changed shape*; a brand-new member cannot throw. The registry has no schema deliberately.
- **An older build silently drops the link.** `ModRegistryService` leaves `UnmappedMemberHandling`
  at the default `Skip`, so a downgrade ignores the member on read and erases it on its next
  `Save()`. This is the same exposure `Notes` / `Tags` / `IsFavourite` already carry. Note the
  asymmetry with profiles, which *do* have a schema and refuse a newer file outright
  (`ModProfileService`, `Schema` / `SupportedSchema`) — that difference is intended, not a bug.
- **`NexusLinkKind` must not reach `ProfileMod` or `ModBackup`.** Those two serialise with bare
  options and no `JsonStringEnumConverter`, so the enum would become a pinned ordinal there — the
  same hazard `ModType` carries. Only the bare `int NexusModId` goes into a profile, already
  qualified by `ModProfile.GameId`, and nothing may ever read it back as a link.

## What is deliberately not built

**No suggestion from the download filename.** Parsing the mod id out of a Nexus download filename is
accurate — 21 correct, 0 wrong, 0 missed across 23 Nexus archives — but the filename never names the
**game**, and the measured corpus spanned six different Nexus games. Pairing a filename id with
whichever game is open is precisely the collision above.

`Zone.Identifier` is the only artefact that carries the domain, via a CDN path of the form
`cdn/{gameId}/{modId}/…` — and Nexus's newer downloads use an opaque `cdn/xx/yy/zz/{uuid}` path
carrying neither. The two most recent downloads on the development machine already use the new form,
**including AERR itself** — the mod this whole feature exists for. So the signal is decaying and buys
nothing today. Coverage is also negligible: of 33 installed mods across both games, exactly **one**
has a `SourcePath` pointing at a downloaded archive at all.

If Nexus ever restores the numeric CDN path, this may only ever **pre-fill the dialog's id box** for
the user to confirm against a resolved title. Never apply a link without a person approving it.

**No rename column on the mod grid** as an alternative repair path. `mod.Name` feeds
`NexusModMatcher.KeyForInstalled`, so a user-typed string could normalise onto a real Nexus title and
conjure a wrong card — and it is also the profile match key in every saved profile.

**Never pre-fill `NexusLink` from a name match.** It is on `OnModAnnotationChanged`'s allow-list, so
every pre-fill would rewrite the whole registry file — and it would persist a guess as the user's own
declaration, which is the one thing this area exists to refuse.
