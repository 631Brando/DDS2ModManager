# Making your mod updatable

DDS Mod Manager can check your mod for updates and install them for your users, without them
having to visit a page and re-download anything by hand.

This is entirely opt-in. You tell the manager where your releases live; it does the rest. If you
don't set anything up, nothing changes and your mod works exactly as it does today.

**Applies to both games.** Everything below works the same for Drug Dealer Simulator and Drug
Dealer Simulator 2.

> **`.dds2mod.json` still works everywhere, and always will.** The app is now called DDS Mod Manager
> rather than DDS2 Mod Manager, but the manifest was NOT renamed out from under anyone: renaming it
> would stop every mod that already ships one from declaring an update source, and the only symptom
> would be that updates quietly stop appearing.
>
> If shipping a file with "dds2" in the name bothers you on a DDS1 mod, `.ddsmod.json` is accepted
> as an exact equivalent. Both names are read everywhere, on both games, with `.dds2mod.json`
> checked first. Use whichever you prefer — there is nothing to migrate either way.

## The short version

Publish your mod's files as **GitHub releases**, then point the manager at the repository:

- **LogicMods** — add a `ModUpdateUrl` string variable to your `ModActor` Blueprint
- **Lua, patch, loose-asset and DLL-plugin mods** — ship a `.dds2mod.json` file with your mod

That's it. Users get an "Update to v1.3.0" button, see your release notes, and choose whether to
install.

## Why GitHub only

The manager only accepts addresses that resolve to a `github.com` repository. That isn't a
preference — it's the safety boundary.

The update URL comes from inside your mod, and installing an update means putting executable
content on someone's machine (a Lua mod runs code in the game's process). If any address were
allowed, a malicious or compromised mod could point the updater at any server it liked. Requiring
a repository means the source of every update is something a user can go and read before agreeing
to it.

Plain `http://` is refused too, since an update fetched over it could be swapped in transit.

## What the address may look like

Write this, and write it exactly the same way in every release:

```
https://github.com/yourname/YourMod
```

These are all accepted, and all mean the same thing:

| You write | Read as |
|---|---|
| `https://github.com/yourname/YourMod` | yourname / YourMod |
| `https://github.com/yourname/YourMod/` | same |
| `https://github.com/yourname/YourMod.git` | same |
| `https://github.com/yourname/YourMod/releases/latest` | same |
| `https://github.com/yourname/YourMod/tree/main` | same |
| `https://github.com/yourname/YourMod?tab=readme#install` | same |
| `https://www.github.com/yourname/YourMod` | same |
| `HTTPS://GitHub.COM/yourname/YourMod` | same — owner and repo keep the case you typed |
| `github.com/yourname/YourMod` | same — `https://` is assumed |
| `yourname/YourMod` | same |

Only the first two path segments are used, so a link to a release, a branch, a file or even a
specific download all resolve to the repository itself. Surrounding spaces, tabs and newlines are
trimmed. Owner and repository may contain ASCII letters, digits, `-`, `_` and `.`, up to 100
characters each.

**The short `yourname/YourMod` form is stricter than it looks.** It only applies when there is
exactly one slash and **no dot anywhere** in the text. So `yourname/YourMod` works, but
`yourname/YourMod/`, `yourname/YourMod.git`, `yourname/My.Mod` and `yourname/v1.0` are all
refused. If your repository name contains a dot, write the full `https://` URL — the dot is
perfectly fine there.

### Refused

| Refused | Why |
|---|---|
| `http://github.com/...` | Must be `https`. Refused outright, never silently upgraded. |
| `git@github.com:yourname/YourMod.git` | The SSH clone string GitHub hands you. Not a URL. |
| `ssh://`, `git://`, `git+https://` | Not `https`. |
| `https://github.com/yourname` | Your profile is not a repository — two segments are needed. |
| `https://gist.github.com/...` | Gists cannot be auto-updated. |
| `https://raw.githubusercontent.com/...`, `https://api.github.com/...` | Only `github.com` and `www.github.com` are accepted. |
| `https://gitlab.com/...`, and every other host | GitHub only. A mod hosted elsewhere cannot auto-update. |
| Anything with a non-ASCII letter, a space, or an invisible character pasted from a web page | Owner and repository are ASCII only. If an address that *looks* right is refused, retype it by hand rather than pasting it again. |
| `https://github.com/yourname/YourMod.` | A trailing full stop. GitHub won't let a repository name end in one, so this is refused rather than being read as a repository called `YourMod.` — but it means a URL written at the end of a sentence stops working. A *leading* dot is fine (`.github` is a real repository name). |
| `https://github.com/orgs/yourname/repositories`, `/sponsors/...`, `/users/...`, `/topics/...`, `/settings/...` | GitHub's own pages, not repositories. They have the same two-segment shape as a repository URL, so they used to parse as owner `orgs`, repository `yourname`. Link the repository itself, not your organisation, sponsors or topics page. |

### Once it works, never reformat it

The address is pinned on each player's machine the first time it's seen, and compared afterwards
as the **exact string you wrote** — not as the repository it resolves to. So adding a trailing
slash, adding or removing `.git`, dropping the `https://`, or tidying up a stray space in a later
release all read as *the update address has moved*: your players get a warning, trust in your
account is revoked, and no update is offered until they confirm it by hand.

The repository never changed. The string did. Pick one form and leave it alone.

## LogicMods: the `ModUpdateUrl` variable

In your `ModActor` Blueprint, add a **String** variable named `ModUpdateUrl` and set its default
value to your repository:

```
ModUpdateUrl  (String)  =  https://github.com/yourname/YourMod
```

Also set `ModVersion` — without it no update is ever offered:

| Variable | Type | Purpose |
|---|---|---|
| `ModUpdateUrl` | String | **Required.** Your GitHub repository — see [What the address may look like](#what-the-address-may-look-like). |
| `ModVersion` | String | **Required in practice.** Your current version, e.g. `1.2.0`. Without it the manager has nothing to compare a release against, and offers nothing. |
| `ModAuthor` | String | Recorded, but not currently shown anywhere — the prompt names the GitHub account that publishes the release. |

Compile and save the Blueprint, then package as normal. The value travels inside your `.pak`, so
it can't get separated from the mod.

Leave the variable blank and nothing happens — Unreal doesn't write out values that match the
default, so a blank `ModUpdateUrl` reads exactly like not having one.

## Lua, patch, loose-asset and DLL mods: `.dds2mod.json`

Ship a file called `.dds2mod.json` alongside your mod's files. For a Lua mod that's your mod's
own folder; for a patch mod it sits next to your `.pak`, named after it
(`YourMod.pak` → `YourMod.dds2mod.json`). For a loose-asset or DLL-plugin mod, put it at the top
level of your archive.

```json
{
  "schema": 1,
  "name": "Your Mod",
  "author": "yourname",
  "version": "1.2.0",
  "updateUrl": "https://github.com/yourname/YourMod",
  "asset": "YourMod.zip"
}
```

| Field | Required | Purpose |
|---|---|---|
| `updateUrl` | yes | Your GitHub repository — see [What the address may look like](#what-the-address-may-look-like). |
| `version` | in practice, yes | Your current version. Without it no update is ever offered — see below. |
| `schema` | no | Manifest format version. Leave it at `1`. |
| `asset` | no | Which release file to download — see below. |
| `name` | no | What the mod is called. Overrides the name the manager would otherwise work out from your `.pak`, your folder or your `.dll` — worth setting if those don't match your mod's real name. |
| `author`, `description` | no | Recorded, but not currently shown anywhere. |

Field names are matched ignoring case, so `updateUrl` and `UpdateUrl` both work. The older spelling
`modUpdateUrl` is still read, so manifests published before this was renamed keep working. `//`
comments and trailing commas are tolerated.

Unknown *fields* are ignored, so a manifest written for a later version of the manager still works.
`schema` is the exception: a manifest declaring a schema higher than the build understands is
refused whole, and the mod stops offering updates.

## Publishing an update

1. Tag a release on your repository.
2. Attach your mod as a single `.zip`, `.7z` or `.rar`.
3. Write release notes — users see them in the update prompt before deciding.

A bare `.pak` is recognised as a new version, but the manager can't unpack one: your players are
told an update exists and shown a link to fetch it themselves. Ship an archive if you want the
one-click path.

**If your release has more than one downloadable file**, name the right one with the `asset` field
in your manifest. Without it the manager refuses to guess, and skips the update rather than risk
installing the wrong file. It'll say so in the log.

The `asset` value is the **exact** file name as published, matched ignoring case. If it matches
nothing in the release — because the name now carries a version number, say — the update is
skipped entirely. There is no fallback to guessing, which is the point of naming it. Either keep
the file name stable across releases, or leave `asset` out and publish exactly one archive.

Version comparison uses `version` (or your Blueprint's `ModVersion`) against the release tag, and
a leading `v` is ignored on both sides. **If you don't set one, no update is ever offered** — the
manager won't guess whether a release is newer than something it can't see. It says so once in the
log and moves on. Treat the version as required, even though a manifest without it still parses.

## What your users see

Nothing installs silently. Ever. When an update is found they get a prompt showing:

- the version, and your release notes
- **the repository the download comes from**, and how your mod declared it
- whether your account is unrecognised, trusted by them, or verified by the maintainers

They can trust your account to stop the manager flagging you as unrecognised in future — but even
then, they're still asked before each install. That's deliberate: accounts get compromised, and a
silent update would turn that into code running on someone's machine with no warning.

## Getting verified

`verified-mods.json` in this repository lists GitHub accounts the maintainers have checked. Being
on it means players see "Verified update address" instead of "Unrecognised update address". Open a
pull request or an issue to be considered.

Be clear about what it claims, because it's narrower than it sounds: **it vouches for the account
your updates are fetched from, not for any particular mod.** Nothing is signed, and a mod declares
its own address, so the badge follows the address rather than the file. It also doesn't promise
anything permanent — accounts get compromised — and it never skips the install prompt.
