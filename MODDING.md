# Making your mod updatable

DDS2 Mod Manager can check your mod for updates and install them for your users, without them
having to visit a page and re-download anything by hand.

This is entirely opt-in. You tell the manager where your releases live; it does the rest. If you
don't set anything up, nothing changes and your mod works exactly as it does today.

## The short version

Publish your mod's files as **GitHub releases**, then point the manager at the repository:

- **LogicMods** — add a `ModUpdateUrl` string variable to your `ModActor` Blueprint
- **Lua and patch mods** — ship a `.dds2mod.json` file with your mod

That's it. Users get an "Update to v1.3.0" button, see your release notes, and choose whether to
install.

## Why GitHub only

The manager only accepts `github.com` URLs. That isn't a preference — it's the safety boundary.

The update URL comes from inside your mod, and installing an update means putting executable
content on someone's machine (a Lua mod runs code in the game's process). If any address were
allowed, a malicious or compromised mod could point the updater at any server it liked. Requiring
a repository means the source of every update is something a user can go and read before agreeing
to it.

Plain `http://` is refused too, since an update fetched over it could be swapped in transit.

## LogicMods: the `ModUpdateUrl` variable

In your `ModActor` Blueprint, add a **String** variable named `ModUpdateUrl` and set its default
value to your repository:

```
ModUpdateUrl  (String)  =  https://github.com/yourname/YourMod
```

Optionally also:

| Variable | Type | Purpose |
|---|---|---|
| `ModUpdateUrl` | String | **Required.** Your GitHub repository. |
| `ModVersion` | String | Your current version, e.g. `1.2.0`. Lets the manager compare properly. |
| `ModAuthor` | String | Display name for the update prompt. |

Compile and save the Blueprint, then package as normal. The value travels inside your `.pak`, so
it can't get separated from the mod.

Leave the variable blank and nothing happens — Unreal doesn't write out values that match the
default, so a blank `ModUpdateUrl` reads exactly like not having one.

## Lua and patch mods: `.dds2mod.json`

Ship a file called `.dds2mod.json` alongside your mod's files. For a Lua mod that's your mod's
own folder; for a patch mod it sits next to your `.pak`, named after it
(`YourMod.pak` → `YourMod.dds2mod.json`).

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
| `updateUrl` | yes | Your GitHub repository. |
| `schema` | no | Manifest version. Currently `1`. |
| `version` | no | Your current version, so the manager can compare rather than guess. |
| `name`, `author` | no | Shown in the update prompt. |
| `asset` | no | Which release file to download — see below. |

Unknown fields are ignored, so a manifest written for a later version of the manager still works.

## Publishing an update

1. Tag a release on your repository.
2. Attach your mod as a single `.zip`, `.7z`, `.rar` or `.pak`.
3. Write release notes — users see them in the update prompt before deciding.

**If your release has more than one downloadable file**, name the right one with the `asset` field
in your manifest. Without it the manager refuses to guess, and skips the update rather than risk
installing the wrong file. It'll say so in the log.

Version comparison uses `version` (or your Blueprint's `ModVersion`) against the release tag. If
you don't set one, the manager falls back to "is this a release tag I haven't seen before" — which
works, but it can't tell users how far behind they are.

## What your users see

Nothing installs silently. Ever. When an update is found they get a prompt showing:

- the version, and your release notes
- **the repository the download comes from**, and how your mod declared it
- whether your account is unrecognised, trusted by them, or verified by the maintainers

They can trust your account to stop the manager flagging you as unrecognised in future — but even
then, they're still asked before each install. That's deliberate: accounts get compromised, and a
silent update would turn that into code running on someone's machine with no warning.

## Getting verified

`verified-mods.json` in this repository lists sources the maintainers have checked. Being on it
means users see "Verified source" instead of "Unrecognised source". Open a pull request or an
issue to be considered.

Verification says someone trusted looked at your account and your mod. It doesn't promise
anything permanent, and it doesn't skip the install prompt.
