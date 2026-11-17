# Real-game integration tests

Runs the actual Unity player, production mod, Scala compiler, Scala.js worker and native simulation without a game window. Windows x64, Steam running and ownership of the game are required. Close the ordinary game first.

```powershell
# Build current source and run the complete suite.
.\tests\Integration\run.ps1

# Short compiler -> worker -> native movement check.
.\tests\Integration\run.ps1 -Suite smoke

# Optional: another Steam installation or an expanded release ZIP.
.\tests\Integration\run.ps1 -GamePath 'D:\SteamLibrary\steamapps\common\The Farmer Was Replaced'
.\tests\Integration\run.ps1 -BundlePath 'D:\expanded-release'

# Selected cases; cold editor startup and native movement are always included.
.\tests\Integration\run.ps1 -Cases mining-native-api,step-resume-stop
```

The source path prepares pinned build tools and an offline payload through the installer module. It does not install into the ordinary game. Building the test tools requires a .NET 10 SDK. An explicit bundle must contain its BepInEx loader and `BepInEx/plugins/FarmerScala` payload. Initial preparation may download dependencies; execution uses the bundled offline compiler dependencies. Allow several GB for local copies and reports.

Reports are written to `.local/integration/runs/<id>/`: `Report.html`, `report.json`, JUnit `results.xml`, per-process results, progress and Unity logs. Assertions, runtime Unity errors, missing reports, unexpected exits and watchdog timeouts fail the command. A full run includes a second Unity process for cold-start persistence. `-TimeoutSeconds` bounds each process (900 by default). `-Suite smoke -InjectFailure` deliberately breaks a Scala assertion to verify failure reporting.

## Coverage

| Area | Real-game checks |
| --- | --- |
| Startup/API | A fresh process opens files with type/name errors and verifies enum/assert hover, semantic colors, completion and diagnostics before any successful player build. Native Play then compiles and moves the real drone; active SDK covers native function names; batch/null graphics mode. |
| Scala | Classes, case classes, collections, imports, `@main`, imported entry-point isolation and legacy scripts. |
| Farming | Movement/wrapping, planting/costs, sensing, water, inventory and hats. |
| Drones | Captured closures, typed results, parent isolation, native child lifecycle and waiting. |
| Simulation | Native simulation, globals, timing results and restoration of the main farm. |
| Mining | Real Steam entitlement/profile selection, jumping, digging, depth, sensors, prospecting, blocks and hover. Gameplay is skipped when DLC is absent. |
| Research | Real purchases/costs/progression, free programming upgrades, tree/Info rebuilds and both leaderboard locks. |
| Steam/ranked guards | Actual Harmony hooks reject achievements/stats and base/mining ranked starts before an independent Steam-write tripwire. |
| Controls/errors | Source stepping, resume/stop, involved/unrelated edits, compile/runtime errors and recovery. |
| Editor | Project import completion, inherited docs, semantic spans, read-only Scala sources, formatting/import cleanup, ranked enum/member completion and prefix matching in the native popup. |
| Windows | Native UI objects/layout, output toolbar presence and equal Scala/DLC/news button gaps. |
| Output startup | Pending text throughout compilation/worker initialization; execution-start transition, cached runs, cancellation, compile errors and programs with no game calls. |
| Saves | Native asynchronous save, Scala/Python separation, reload, Default interpreter, switching saves and cold startup. Forced overlap between native backups and editor writes, coherent copied revisions, genuine failure recovery and repeated saves during editing. |

Keep the existing component tests for exhaustive lexer, palette, edit-transform, protocol and formatting edge cases. This suite checks integration with the actual player. It does not prove every SDK argument combination, every farm strategy, pixel appearance, physical input or DLC absence on a machine that owns the DLC.

## Isolation

The runner copies the retail game into `.local/integration/game-<fingerprint>` and installs the payload and test driver there. Disposable saves/settings live under that run's `data/` directory. The watchdog can terminate only its own copied executable. No test tools or fixtures are installed into the ordinary game.

Before launch, a Cecil preparation tool redirects the copied game's data path and blocks Steam account writes, even if BepInEx fails to load. A preloader repeats these protections before game initialization. Real Steam initialization and ownership reads remain active; restart-through-Steam is suppressed to prevent launching the ordinary installation. An independent journal records account writes that pass the production guards. All original player-data files are hashed before/after, including additions/deletions.

The null graphics device requires suppressing four GPU-only drawing leaves in memory: `FarmRenderer.RenderInstanced` and `VFXManager.DrawMovingMeshEffects`, `DrawCrumbleEffects`, `DrawCrumbleDecorationEffects`. Native simulation, gameplay, research, UI objects and their update logic remain active. On-disk `Core.dll` stays original. Adaptations are recorded in the run directory. Startup shader messages are expected without a GPU; runtime Unity errors observed by the driver fail the suite.

The installer copies explicitly selected production outputs; these test-only projects are excluded from mod packages. Copied retail assets, payloads, test saves and reports stay Git-ignored under `.local/`. Do not distribute the copied game.

## Game updates

Run normally first. An unknown `Core.dll` is rejected before launch without changing the supported-game pin.

For investigation, an explicit prepared `-BundlePath ... -AllowUnverifiedGame` allows changing the accepted hash in the **copied plug-in only**. The override is recorded in `isolation-manifest.json`; shipping source and lock stay unchanged. Native assemblies must still be compatible enough to build/run the driver. A green diagnostic probe is evidence to review, not automatic release compatibility approval. Review API/tree differences and perform visual checks before updating the release pin.
