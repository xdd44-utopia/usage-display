# Usage Monitor for macOS

A macOS menu bar app that shows how much of your **Codex** and **Claude Code** usage limits is left.
Each icon is a short stacked label, **COD** or **CLA**, followed by the percentage left, or `--%` until
usage is known.
Click it to see every limit window and when it resets.
An icon appears only for tools that are installed. Requires macOS 13+.

## Live usage

On first launch the app asks once to **Allow Live Usage** for both tools, and remembers the answer. Allowed, it
gets current limits from OpenAI and Anthropic using each tool's own login, and keeps the local sources below as
a backup, showing whichever is newest. Declined, it uses only the local sources, which update only while you chat.
Whenever a tool isn't getting live usage, its menu offers **Use Live Usage…** (asking again if you declined) or, if
the last attempt failed, **Check Again**. Once allowed, live usage can't be turned off.

## Codex

**COD** shows whichever limit is closest to running out. Live usage, including banked resets, comes from
`codex app-server`, using Codex's own login. It checks at launch, when you open the menu (at most once a minute),
and every five minutes while the usage shown is stale. The backup is the latest snapshot Codex saves in
`~/.codex/sessions/`.

## Claude Code

**CLA** shows the weekly limit small on top and the 5-hour limit large below.

Live usage reads your Claude login from the Keychain and asks Anthropic, at launch, about every five minutes, and
when you open the menu. It never stores or refreshes your credentials; if the login has expired, it runs
`claude auth status` so Claude Code can renew it. Denying the Keychain prompt turns live usage off for Claude
until you choose **Use Live Usage…** or relaunch.

The backup is a usage status line the app adds to `~/.claude/settings.json` at launch. If you already have one,
it asks before replacing it, and keeps no backup. Claude then sends usage straight to the app, with no credentials
or network requests. That usage is kept in memory only, so after a relaunch it waits for your next Claude reply.
Without live usage or the status line, the Claude icon is off.

The status line points to the app, so install it by dragging it out of `UsageMonitor.dmg` in Finder, to
Applications or any other folder. If you open it straight from the disk image, or macOS runs it from a temporary
copy because it was never moved in Finder, it offers to move itself to `/Applications` (copying it when it
runs from the disk image). To remove the
integration, delete `statusLine` from Claude's settings.

## Updates

An installed app (not one running from the disk image) checks GitHub for a newer release when it starts and once a
day, retrying after 15 minutes if the check fails. When one is available, both menus show **Update to v…** just above
Quit; choosing it downloads the new disk image, replaces the app where it is, and reopens it, using
[Sparkle](https://sparkle-project.org). Updates skip the first-launch “Open Anyway” step: Sparkle downloads them
itself, so macOS doesn't flag them, and checks each one against the key in `Info.plist` instead.

## Build

Needs only the Xcode Command Line Tools:

```sh
./scripts/build-app.sh   # from macos/ → build/UsageMonitor.app and build/UsageMonitor.dmg (universal)
```

The disk image window's background is `Resources/dmg-background.png` (and `@2x`). The window size, which must match
the 1x PNG's pixel size, and the icon positions are set in `scripts/dmg-layout.sh`, which writes them to
`Resources/dmg-layout`. Rerun it after resizing the background or moving icons; it needs internet access to install
two Python packages.

Pushing a tag like `v0.2.0` builds and publishes a release; see [the release workflow](../.github/workflows/release.yml). The update signing key lives in the
login Keychain of whoever ran Sparkle's `generate_keys` and in the `SPARKLE_PRIVATE_KEY` repository secret. Without
it, installed copies can't be updated again, so keep a backup.
