# Usage Display for macOS

A macOS menu bar app that shows how much of your **Codex** and **Claude Code** usage limits is left.
Each icon is a short stacked label, **COD** or **CLA**, followed by the percentage left, or `--%` until
usage is known.
Click it to see every limit window and when it resets.
An icon appears only for tools that are installed. Requires macOS 13+.

## Codex

**COD** shows whichever limit is closest to running out. By default it reads the latest snapshot Codex
saves in `~/.codex/sessions/`, so it only updates while you use Codex on this Mac.

Turn on **Use Background Codex for Live Usage** in the menu to get current limits and banked resets from
OpenAI through `codex app-server`, using Codex's own login. It checks when you open the menu (at most once a
minute) and every five minutes while the local snapshot is stale. The setting lasts until quit.

## Claude Code

**CLA** shows the weekly limit small on top and the 5-hour limit large below.

At launch the app adds a usage status line to `~/.claude/settings.json`. If you already have one, it asks
before replacing it, and keeps no backup. Claude then sends usage straight to the app, with no credentials
or network requests. Usage is kept in memory only, so after a relaunch the icon waits for your next Claude reply.

If you keep your own status line, or your Claude interface doesn't run status lines (choose **Use Credential
Access for Live Usage…**), the app can instead read your Claude login from the Keychain and ask Anthropic.
It checks at launch, about every five minutes, and when you open the menu. It never stores or refreshes your
credentials; if the login has expired, it runs `claude auth status` so Claude Code can renew it. Declining
from the menu keeps the status line; declining at launch turns the Claude icon off until the next launch.

The status line points to the app, so install it by dragging it out of `UsageDisplay.dmg` in Finder, to
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
./scripts/build-app.sh   # from macos/ → build/UsageDisplay.app and build/UsageDisplay.dmg (universal)
```

Releases are built by GitHub Actions; see [Releasing](../README.md#releasing). The update signing key lives in the
login Keychain of whoever ran Sparkle's `generate_keys` and in the `SPARKLE_PRIVATE_KEY` repository secret. Without
it, installed copies can't be updated again, so keep a backup.
