# Usage Display for Windows

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

## Codex

By default the app reads the latest snapshot Codex saves in `%USERPROFILE%\.codex\sessions\`, so it only
updates while you use Codex on this PC.

Turn on **Use Background Codex for Live Usage** in the panel to get current limits and banked resets from
OpenAI through `codex app-server`, using Codex's own login. It checks when you open the panel (at most once a
minute) and every five minutes while the local snapshot is stale. The setting lasts until quit.

## Claude Code

At launch the app adds a usage status line to `%USERPROFILE%\.claude\settings.json`. If you already have one,
it asks before replacing it, and keeps no backup. Claude then sends usage straight to the app over a local
named pipe, with no credentials or network requests. Usage is kept in memory only, so after a relaunch the
gauge waits for your next Claude reply.

Claude Code runs status lines in Git Bash, or in PowerShell when Git Bash isn't installed. The command is a bare
path that works in both, so the app's path can't contain spaces or symbols. It falls back to the 8.3 short
path when there is one.

If you keep your own status line, or your Claude interface doesn't run status lines (choose **Use Credential
Access for Live Usage…**), the app can instead read your Claude login from `%USERPROFILE%\.claude\.credentials.json`
and ask Anthropic. It checks at launch, about every five minutes, and when you open the panel. It never stores or
refreshes your credentials; if the login has expired, it runs `claude auth status` so Claude Code can renew it.

## Install

Run `UsageDisplay-Setup-x64.exe`, or `UsageDisplay-Setup-arm64.exe` on an ARM PC. It installs for your account
only, into `%LOCALAPPDATA%\Programs\UsageDisplay\`, without asking for administrator approval, and adds a
Start menu entry. Quit Usage Display before installing an update. Uninstall it from Settings › Apps; that also
removes its settings, Launch at Login and its Claude status line (if that still points to the installed copy). A
status line it replaced isn't restored.

An installed copy checks GitHub for a newer release when it starts and once a day. When one is available,
**Update to v…** appears in the panel and the tray menu; it downloads the installer, quits, installs and reopens
Usage Display.

## Build

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and, for the installers,
[Inno Setup 6](https://jrsoftware.org/isinfo.php) (`winget install JRSoftware.InnoSetup`). Run these commands from `windows/`:

```powershell
.\scripts\build.ps1   # tests, then build\UsageDisplay-Setup-x64.exe and build\UsageDisplay-Setup-arm64.exe
```

For a quick look during development, like `swift run`:

```powershell
.\scripts\run.ps1   # builds and starts the debug app; it logs to the terminal, and Ctrl+C quits it
```

Quit an installed copy first, since only one can run. The debug app has no Launch at Login. While it runs,
Claude's status line points at it; the installed copy points it back when it next starts. The scripts find the SDK even when the `dotnet` on PATH is a runtime-only install, which a bare
`dotnet run` or `dotnet test` can't.

Releases are built by GitHub Actions; see [Releasing](../README.md#releasing).
