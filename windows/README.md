# Usage Monitor for Windows

A small floating window that stays on top and shows how much of your **Codex** and **Claude Code** usage limits
is left as rings, like Apple Watch activity rings, filling clockwise from the top:

- **CLA**: Claude Code's 5-hour limit as the thick inner ring, and the weekly limit as the thin outer ring.
- **COD**: the Codex limit closest to running out.

An empty ring with a faded name means usage isn't known yet. Hover for the numbers. Drag the window anywhere;
it remembers where you put it. Click it to open a panel with every limit window and when it resets, side by
side when both tools are installed. A gauge appears only for tools that are installed. Requires Windows 10/11 and the
[.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0).

**Hide Floating Window** in the panel leaves just the notification-area icon. Click the icon to open the panel,
or right-click it and choose **Show Floating Window** to bring the window back. While the floating window is showing, the icon only offers
Hide and Quit. Windows puts new icons behind the **^** overflow arrow; drag it onto the taskbar, or turn it on
under Settings › Personalization › Taskbar › Other system tray icons.

## Live usage

On first launch the app asks once to **Allow Live Usage** for both tools, and remembers the answer. Allowed, it
gets current limits from OpenAI and Anthropic using each tool's own login, and keeps the local sources below as
a backup, showing whichever is newest. Declined, it uses only the local sources, which update only while you chat.
Whenever a tool isn't getting live usage, its panel section offers **Use Live Usage…** (asking again if you declined) or, if
the last attempt failed, **Check Again**. Once allowed, live usage can't be turned off.

## Codex

Live usage, including banked resets, comes from `codex app-server`, using Codex's own login. It checks at launch,
when you open the panel (at most once a minute), and every five minutes while the usage shown is stale. The backup
is the latest snapshot Codex saves in `%USERPROFILE%\.codex\sessions\`.

## Claude Code

Live usage reads your Claude login from `%USERPROFILE%\.claude\.credentials.json` and asks Anthropic, at launch,
about every five minutes, and when you open the panel. It never stores or refreshes your credentials; if the login
has expired, it runs `claude auth status` so Claude Code can renew it.

The backup is a usage status line the app adds to `%USERPROFILE%\.claude\settings.json` at launch. If you already
have one, it asks before replacing it, and keeps no backup. Claude then sends usage straight to the app over a local
named pipe, with no credentials or network requests. That usage is kept in memory only, so after a relaunch it
waits for your next Claude reply. Without live usage or the status line, the Claude gauge is hidden.

Claude Code runs status lines in Git Bash, or in PowerShell when Git Bash isn't installed. The command is a bare
path that works in both, so the app's path can't contain spaces or symbols. It falls back to the 8.3 short
path when there is one.

## Install

Run `UsageMonitor-Setup-x64.exe`, or `UsageMonitor-Setup-arm64.exe` on an ARM PC. It installs for your account
only, into `%LOCALAPPDATA%\Programs\UsageMonitor\`, without asking for administrator approval, and adds a
Start menu entry. Quit Usage Monitor before installing an update. Uninstall it from Settings › Apps; that also
removes its settings, Launch at Login and its Claude status line (if that still points to the installed copy). A
status line it replaced isn't restored.

An installed copy checks GitHub for a newer release when it starts and once a day. When one is available,
**Update to v…** appears in the panel and the tray menu; it downloads the installer, quits, installs and reopens
Usage Monitor.

## Build

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and, for the installers,
[Inno Setup 6](https://jrsoftware.org/isinfo.php) (`winget install JRSoftware.InnoSetup`). Run these commands from `windows/`:

```powershell
.\scripts\build.ps1   # tests, then build\UsageMonitor-Setup-x64.exe and build\UsageMonitor-Setup-arm64.exe
```

For a quick look during development, like `swift run`:

```powershell
.\scripts\run.ps1   # builds and starts the debug app; it logs to the terminal, and Ctrl+C quits it
```

Quit an installed copy first, since only one can run. The debug app has no Launch at Login. While it runs,
Claude's status line points at it; the installed copy points it back when it next starts. The scripts find the SDK even when the `dotnet` on PATH is a runtime-only install, which a bare
`dotnet run` or `dotnet test` can't.

Pushing a tag like `v0.2.0` builds and publishes a release; see [the release workflow](../.github/workflows/release.yml).
