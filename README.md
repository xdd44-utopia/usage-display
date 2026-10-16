# Usage Display

Shows how much of your **Codex** and **Claude Code** usage limits is left, at a glance:

- **[macOS](macos/)**: menu bar items labeled **COD** and **CLA** with the percentage left. Swift, macOS 13+.
- **[Windows](windows/)**: an always-on-top floating window with usage rings and a notification-area icon. C#/.NET 10, Windows 10/11.

Both read the same sources: Codex's session logs or `codex app-server`, and Claude Code's status line or its
saved login. Click a macOS menu bar item or the Windows floating window to see every limit window and when it resets.

```
macos/     Swift package, tests, and scripts/build-app.sh
windows/   .NET solution: src/UsageDisplay, tests/UsageDisplay.Tests, installer/, and scripts/build.ps1
```

## Releasing

Push a version tag; GitHub Actions builds both apps at that version and publishes one release with the macOS disk
image, its Sparkle update feed (`appcast.xml`) and both Windows installers:

```sh
git tag v0.2.0 && git push origin v0.2.0
```

Installed copies on both platforms look at the latest release, so publish every release through this workflow, and
never mark one without all of these files as latest. The macOS disk image is signed for Sparkle with the
`SPARKLE_PRIVATE_KEY` repository secret.
