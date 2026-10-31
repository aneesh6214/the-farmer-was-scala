# Contributing

Bug reports, feature suggestions, documentation fixes, and pull requests are encouraged!

## Issues

When opening a GitHub issue, choose **Bug report** or **Feature request**. The forms live in [.github/ISSUE_TEMPLATE](.github/ISSUE_TEMPLATE). Search existing issues first so related reports stay together.

For a bug, please include the mod release or commit, game version if known, installation method, reproduction steps, and the smallest Scala snippet that demonstrates it. Screenshots help with editor and layout issues.

Useful logs:

- Mod and runtime failures: `BepInEx/LogOutput.log` in the game folder.
- Source installation: `.local/install-logs/` in your checkout.
- Setup EXE installation: `%LOCALAPPDATA%\FarmerScala\setup-logs\`.

A useful feature request describes what you want to do and how the proposed change would help.

## Development

The project targets Windows x64 and needs the supported game installation for assembly references. Run `install.ps1` for the normal source build and installation walkthrough. Build tools and downloaded dependencies are kept in the ignored `.local/` directory.

For targeted C# changes, use a .NET 10 SDK:

```powershell
dotnet build .\src\Plugin\FarmerScala.csproj -c Release
```

The project defaults to the usual Steam game path. Pass `-p:GameRoot="D:\SteamLibrary\steamapps\common\The Farmer Was Replaced"` to builds if yours differs. The plugin build also needs BepInEx references, which the normal installation prepares. Tests in `tests/` are executable projects; run the relevant project with `dotnet run --project <path-to-csproj> -c Release`.

Keep changes focused, follow `.editorconfig`, and use the game's existing window and button conventions for UI changes. Preserve Default-save behavior and keep Scala achievements and ranked leaderboard submission disabled. Add a focused regression check when fixing behavior that needs one; mention any in-game checks still needed in the pull request.

## Changelog and documentation

The initial release is listed under **1.0.0** in [CHANGELOG.md](CHANGELOG.md). If submitting a pull request into `main`, please include the new version number and release date. Keep entries focused on high-level, user-facing changes.

The plugin embeds `CHANGELOG.md` when it builds and displays it through the game's native news window. Edit the root changelog and rebuild to update both GitHub and in-game release notes. Use headings, short paragraphs, lists, inline code, HTTPS links, and `---` separators. The news view converts separators to the game's style; avoid tables and images in release notes.
