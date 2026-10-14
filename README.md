# Usage Display

A macOS menu bar app that shows how much of your **Codex** and **Claude Code** usage limits is left.
Each logo fills from the bottom up with what remains; click it to see every limit window and when it resets.
An icon appears only for tools that are installed. Requires macOS 13+.

## Codex

The logo shows whichever limit is closest to running out. By default it reads the latest snapshot Codex
saves in `~/.codex/sessions/`, so it only updates while you use Codex on this Mac.

Turn on **Use Background Codex for Live Usage** in the menu to get current limits and banked resets from
OpenAI through `codex app-server`, using Codex's own login. It checks when you open the menu (at most once a
minute) and every five minutes while the local snapshot is stale. The setting lasts until quit.

## Claude Code

In the Anthropic mark, the **A** shows the 5-hour limit and the **\\** shows the weekly limit.

At launch the app adds a usage status line to `~/.claude/settings.json`. If you already have one, it asks
before replacing it, and keeps no backup. Claude then sends usage straight to the app, with no credentials
or network requests. Usage is kept in memory only, so after a relaunch the icon waits for your next Claude reply.

If you keep your own status line, or your Claude interface doesn't run status lines (choose **Use Credential
Access for Live Usage…**), the app can instead read your Claude login from the Keychain and ask Anthropic.
It checks at launch, about every five minutes, and when you open the menu. It never stores or refreshes your
credentials; if the login has expired, it runs `claude auth status` so Claude Code can renew it. Declining
from the menu keeps the status line; declining at launch turns the Claude icon off until the next launch.

The status line points to the app, so it offers to move itself to `/Applications` when launched from anywhere
else. To remove the integration, delete `statusLine` from Claude's settings.

## Build

Needs only the Xcode Command Line Tools:

```sh
./scripts/build-app.sh   # → build/UsageDisplay.app and build/UsageDisplay.zip (universal)
```

## Trademarks

Not affiliated with or endorsed by OpenAI or Anthropic. Codex and OpenAI are trademarks of OpenAI; Claude and
Anthropic are trademarks of Anthropic. Their logos are used only to identify the services whose usage is shown
and are not covered by this project's [license](LICENSE).
