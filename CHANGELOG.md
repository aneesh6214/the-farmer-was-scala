# Changelog

Coming Soon:
- Specialized upgrade tree for Scala code with helpful usage tips.
- Various themes and a theme customizer.

## 1.1.0
11/16/2026

- **Game Compatibility:** Updated for the November base-game update.
- **Mining DLC:** Added Scala APIs and editor support for The Miner Was Replaced. Mining support activates only when the DLC is installed.
- **Execution:** Added optional `@main` entry points and allowed running programs to continue while unrelated files are edited.
- **Documentation:** Fixed missing Scaladoc on inherited members and declarations with modifiers such as `override` and `private`.
- **Completion:** Fixed suggestions for project files and modules in imports, and prioritized enum cases and relevant members over inherited utilities and operators.
- **Formatting:** Ctrl+Alt+L now removes duplicate imports, combines matching imports, and organizes imported names.
- **Editor:** Added matching-parenthesis highlighting. Editor features now initialize even when the project has never compiled successfully.
- **Interface:** Matched menu button spacing to the native DLC controls and kept output startup status visible until execution begins.
- **Save Reliability:** Fixed conflicts between automatic backups and Scala source/layout updates.
- **Testing:** Added automated integration tests running the actual game headlessly.

## 1.0.1
10/31/2026

- **Theme Updates:** Refined the Scala editor palette, syntax colors, and text brightness.
- **Editor Enhancements:** Added right-aligned line numbers with an automatically sized gutter, and improved highlighting consistency while editing.
- **Editing Tools:** Unified two-space indentation across Tab, Enter, and code formatting.
- **Output Panel:** Compilation status matches the native text style and shows when compilation finishes.
- **Completion:** Game API suggestions now respect the save's unlocked features.
- **Bug Fixes:** Fixed startup save restoration, error highlighting, and Scaladoc display issues.

## 1.0.0
10/30/2026
- Play The Farmer Was Replaced in Scala 3, with typed game APIs, classes, case classes, pattern matching, and Scala collections.
- **Editor Enhancements:** Semantic highlighting, hover documentation, Ctrl-click navigation, code completion, type diagnostics, and read-only Scala library sources.
- **Editing Tools:** Automatic indentation, bracket pairing, Scaladoc assistance, comment toggling, and code formatting.
- **Save Management:** Choose Default or Scala 3 when creating a save. Each save keeps its own language.
- **Output Panel:** A persistent window with compilation status, program output, and error notifications.
- **Gameplay:** Programming upgrades unlocked from the start of Scala saves, with normal farming progression. Steam achievements and leaderboards are disabled in Scala 3.
- **Installation:** Guided source installation, a ZIP download, or a setup wizard for Windows x64, with bundled dependencies and installation backups.
- **Mod News:** An in-game Scala news window displaying this changelog.
