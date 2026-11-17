$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
Import-Module (Join-Path $workspace 'src/Installer/Installer.psm1') -Force -DisableNameChecking
$scratch = [IO.Path]::GetFullPath((Join-Path $workspace ('.local/installerchecks/' + [Guid]::NewGuid().ToString('N'))))
if (!$scratch.StartsWith($workspace + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid test scratch path.' }
New-Item -ItemType Directory -Path $scratch -Force | Out-Null
$game = Join-Path $scratch 'fakeGameRoot'
$plugins = Join-Path $game 'BepInEx/plugins'
New-Item -ItemType Directory -Path $plugins -Force | Out-Null
$script:checks = 0
$script:backups = [Collections.Generic.List[string]]::new()
function Check([bool]$Condition, [string]$Description) {
    if (!$Condition) { throw ('FAIL ' + $Description) }
    $script:checks++
    Write-Host ('PASS ' + $Description)
}
function Put([string]$Root, [string]$Relative, [string]$Contents) {
    $target = [IO.Path]::GetFullPath((Join-Path $Root $Relative))
    if (!$target.StartsWith([IO.Path]::GetFullPath($Root) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid test fixture path.' }
    New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
    [IO.File]::WriteAllText($target, $Contents, [Text.UTF8Encoding]::new($false))
}
function Payload([string]$Name, [string]$Version) {
    $folder = Join-Path $scratch $Name
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
    # These inert bytes exercise the installer's filesystem transaction. No file
    # in this fixture is a runnable compiler, game plugin, runtime or executable.
    foreach ($file in @(
        'FarmerScala.dll','Newtonsoft.Json.dll','runtime/dotnet.exe',
        'runtime/shared/Microsoft.NETCore.App/10.0.14/System.Private.CoreLib.dll',
        'runtime/host/fxr/10.0.14/hostfxr.dll','toolchain/java/bin/java.exe',
        'toolchain/sbt-launch-1.11.7.jar','toolchain/repositories',
        'tools/compiler/FarmerScala.Compiler.dll','tools/compiler/FarmerScala.Compiler.deps.json','tools/compiler/FarmerScala.Compiler.runtimeconfig.json',
        'tools/worker/FarmerScala.Worker.dll','tools/worker/FarmerScala.Worker.deps.json','tools/worker/FarmerScala.Worker.runtimeconfig.json',
        'tools/analysis/classpath.txt','tools/analysis/editor-classpath.json','tools/analysis/fake-analysis.jar',
        'sdk/game/api.scala','sdk/game/WireCodec.scala','sdk/game/GameEnums.scala',
        'assets/the-farmer-was-scala.mp4','assets/the-farmer-was-scala.jpg'
    )) { Put $folder $file ($Version + ':' + $file) }
    return $folder
}
function Tree([string]$Folder) {
    if (!(Test-Path -LiteralPath $Folder)) { return '<missing>' }
    $base = (Resolve-Path -LiteralPath $Folder).Path
    return ((Get-ChildItem -LiteralPath $base -File -Recurse | Sort-Object FullName | ForEach-Object {
        $_.FullName.Substring($base.Length + 1) + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }) -join "`n")
}
function Install([string]$Stage) {
    $backup = Install-ModPayload -Payload $Stage -Loader '' -GamePath $game -BackupRoot (Join-Path $scratch 'backups')
    if (!$backup.StartsWith($workspace + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Installer returned an external backup path.' }
    $script:backups.Add($backup)
    return $backup
}

Put $plugins 'FarmerPython/FarmerPython.dll' 'original Python fallback'
Put $plugins 'FarmerScala/FarmerScala.dll' 'original Scala version'
Put $plugins 'OtherMod/keep.txt' 'unrelated plugin'
$stage1 = Payload 'fakeStage1' 'first'
$stageDigest = Tree $stage1
$backup1 = Install $stage1
Check (!(Test-Path -LiteralPath (Join-Path $plugins 'FarmerPython'))) 'the previous Python plugin is absent from active plugins after success'
Check ([IO.File]::ReadAllText((Join-Path $backup1 'plugins/FarmerPython/FarmerPython.dll')) -eq 'original Python fallback') 'the Python fallback is preserved byte-for-byte in the reported backup'
Check ([IO.File]::ReadAllText((Join-Path $backup1 'plugins/FarmerScala/FarmerScala.dll')) -eq 'original Scala version') 'the previous Scala plugin is preserved in the same backup'
Check ((Tree (Join-Path $plugins 'FarmerScala')) -eq $stageDigest) 'the active Scala plugin contains every staged path and exact file contents'
Check (@(Get-ChildItem -LiteralPath $plugins -Filter FarmerScala.dll -File -Recurse).Count -eq 1) 'exactly one FarmerScala plugin DLL is active'
Check ((Tree $stage1) -eq $stageDigest) 'installation leaves the source stage intact'
Check ([IO.File]::ReadAllText((Join-Path $plugins 'OtherMod/keep.txt')) -eq 'unrelated plugin') 'unrelated installed plugins are preserved'

$stage2 = Payload 'fakeStage2' 'second'
$backup2 = Install $stage2
Check ($backup1 -ne $backup2) 'successive installations receive distinct backup directories'
Check ((Tree (Join-Path $backup2 'plugins/FarmerScala')) -eq $stageDigest) 'reinstallation backs up the exact previously active Scala payload'
Check ((Tree (Join-Path $plugins 'FarmerScala')) -eq (Tree $stage2)) 'reinstallation activates the complete replacement payload'

Put $plugins 'FarmerPython/FarmerPython.dll' 'fallback retained on failed install'
$beforeFailure = Tree $plugins
$incomplete = Payload 'fakeIncompleteStage' 'incomplete'
$missing = [IO.Path]::GetFullPath((Join-Path $incomplete 'tools/worker/FarmerScala.Worker.dll'))
if (!$missing.StartsWith($scratch + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid incomplete fixture path.' }
Remove-Item -LiteralPath $missing
$incompleteError = $null
try { Install $incomplete | Out-Null } catch { $incompleteError = $_.Exception.Message }
Check ($null -ne $incompleteError -and $incompleteError -match 'Incomplete mod payload') 'a missing required payload file is rejected'
Check ((Tree $plugins) -eq $beforeFailure) 'incomplete-stage rejection leaves both existing language plugins untouched'

$lockedStage = Payload 'fakeLockedStage' 'locked'
$lockedPath = Join-Path $lockedStage 'copy-must-fail.bin'
Put $lockedStage 'copy-must-fail.bin' 'exclusive source lock'
$copyError = $null
$exclusive = [IO.File]::Open($lockedPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
try {
    try { Install $lockedStage | Out-Null } catch { $copyError = $_.Exception.Message }
} finally { $exclusive.Dispose() }
Check ($null -ne $copyError) 'an exclusively locked staged file produces a real copy failure'
Check ((Tree $plugins) -eq $beforeFailure) 'copy failure occurs before either existing language plugin is moved'

$loader = Join-Path $scratch 'fakeCompleteBundle'
Put $loader 'BepInEx/core/BepInEx.dll' 'pinned loader'
Put $loader 'BepInEx/core/BepInEx.Preloader.dll' 'pinned preloader'
Put $loader 'BepInEx/core/0Harmony.dll' 'pinned harmony'
Put $loader 'BepInEx/LICENSE.txt' 'loader license'
Put $loader 'BepInEx/licenses/component.txt' 'component license'
Put $loader 'BepInEx/sources/component.zip' 'corresponding source'
Put $loader 'winhttp.dll' 'pinned proxy'
Put $loader 'doorstop_config.ini' 'pinned doorstop config'
# Bundled loader input intentionally contains a plugin too. That plugin must
# never be copied over the installed language plugin before it is backed up.
Put $loader 'BepInEx/plugins/FarmerScala/FarmerScala.dll' 'must not be treated as loader'
Put $loader 'BepInEx/config/keep.cfg' 'must not replace user config'
Put $game 'BepInEx/core/BepInEx.dll' 'old loader'
Put $game 'winhttp.dll' 'old proxy'
Put $game 'BepInEx/config/keep.cfg' 'user config'
$pluginBeforeLoader = Tree (Join-Path $plugins 'FarmerScala')
$backup3 = Install-ModPayload $stage1 $loader $game (Join-Path $scratch 'backups') -ReplaceLoader
Check ((Tree (Join-Path $backup3 'plugins/FarmerScala')) -eq $pluginBeforeLoader) 'complete-bundle loader handling backs up the old plugin before replacement'
Check ([IO.File]::ReadAllText((Join-Path $game 'BepInEx/core/BepInEx.dll')) -eq 'pinned loader') 'pinned loader files are installed'
Check ([IO.File]::ReadAllText((Join-Path $game 'winhttp.dll')) -eq 'pinned proxy' -and [IO.File]::ReadAllText((Join-Path $game 'doorstop_config.ini')) -eq 'pinned doorstop config' -and [IO.File]::ReadAllText((Join-Path $game 'BepInEx/core/BepInEx.Preloader.dll')) -eq 'pinned preloader' -and [IO.File]::ReadAllText((Join-Path $game 'BepInEx/core/0Harmony.dll')) -eq 'pinned harmony') 'proxy, configuration, preloader and Harmony are installed at their exact required paths'
Check ([IO.File]::ReadAllText((Join-Path $backup3 'loader/winhttp.dll')) -eq 'old proxy') 'replaced loader files are backed up'
Check ([IO.File]::ReadAllText((Join-Path $game 'BepInEx/config/keep.cfg')) -eq 'user config') 'existing BepInEx configuration is preserved'
Check ([IO.File]::ReadAllText((Join-Path $game 'BepInEx/sources/component.zip')) -eq 'corresponding source') 'loader notices and source material are installed'

$beforeRollback = Tree $game
$lockedLoader = [IO.File]::Open((Join-Path $loader 'winhttp.dll'), [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
$rollbackError = $null
try {
    try { Install-ModPayload $stage2 $loader $game (Join-Path $scratch 'backups') -ReplaceLoader | Out-Null }
    catch { $rollbackError = $_.Exception.Message }
} finally { $lockedLoader.Dispose() }
Check ($null -ne $rollbackError) 'loader copy failure is reported'
$gameFilesAfterRollback = Tree $game
# An unsuccessful install keeps its private prepared directory for diagnosis.
$normalFiles = ((Get-ChildItem -LiteralPath $game -File -Recurse | Where-Object FullName -NotMatch '\\.farmer-scala-prepared-' | Sort-Object FullName | ForEach-Object {
    $_.FullName.Substring($game.Length + 1) + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
}) -join "`n")
$normalBefore = ($beforeRollback.Split("`n") | Where-Object { $_ -notmatch '^\.farmer-scala-prepared-' }) -join "`n"
Check ($normalFiles -eq $normalBefore) 'loader failure rolls back every replaced original file and preserves plugins'

$validatedGame = Join-Path $scratch 'path with spaces'
Put $validatedGame 'TheFarmerWasReplaced.exe' 'inert exe'
Put $validatedGame 'TheFarmerWasReplaced_Data/Managed/Core.dll' 'supported fixture'
$pins = [PSCustomObject]@{ game = [PSCustomObject]@{ coreSha256 = (Get-FileHash -LiteralPath (Join-Path $validatedGame 'TheFarmerWasReplaced_Data/Managed/Core.dll')).Hash } }
Check ((Assert-GamePath $validatedGame $pins) -eq $validatedGame) 'a valid explicit game path with spaces is accepted'
$pins.game.coreSha256 = 'BAD'
$versionError = $null
try { Assert-GamePath $validatedGame $pins | Out-Null } catch { $versionError = $_.Exception.Message }
Check ($versionError -match 'not supported') 'unsupported game versions are rejected'

Add-Type -AssemblyName System.IO.Compression.FileSystem
$badZip = Join-Path $scratch 'traversal.zip'
$zip = [IO.Compression.ZipFile]::Open($badZip, [IO.Compression.ZipArchiveMode]::Create)
try {
    $entry = $zip.CreateEntry('../escape.txt')
    $writer = [IO.StreamWriter]::new($entry.Open())
    try { $writer.Write('must not escape') } finally { $writer.Dispose() }
} finally { $zip.Dispose() }
$zipError = $null
try { & (Get-Module Installer) { param($file, $output) Expand-VerifiedArchive $file $output } $badZip (Join-Path $scratch 'zip-target') }
catch { $zipError = $_.Exception.Message }
Check ($null -ne $zipError -and !(Test-Path -LiteralPath (Join-Path $scratch 'escape.txt'))) 'archive traversal paths are rejected before extraction'

# Reproduce the shortened temporary paths used by Windows setup programs.
$aliasRoot = Join-Path $scratch 'Long temporary profile directory/FarmerScala'
Copy-Item -LiteralPath $loader -Destination $aliasRoot -Recurse
$fso = New-Object -ComObject Scripting.FileSystemObject
$shortRoot = $fso.GetFolder($aliasRoot).ShortPath
if ($shortRoot -ne $aliasRoot) {
    foreach ($inputPath in @($shortRoot, ($shortRoot + '\'), ($aliasRoot + '\'))) {
        $aliasGame = Join-Path $scratch ('alias-game-' + [Guid]::NewGuid().ToString('N'))
        Put $aliasGame 'TheFarmerWasReplaced.exe' 'inert game executable'
        Install-ModPayload $stage1 $inputPath $aliasGame (Join-Path $scratch 'alias-backups') -ReplaceLoader | Out-Null
        Check ([IO.File]::ReadAllText((Join-Path $aliasGame 'winhttp.dll')) -eq 'pinned proxy' -and [IO.File]::ReadAllText((Join-Path $aliasGame 'doorstop_config.ini')) -eq 'pinned doorstop config' -and [IO.File]::ReadAllText((Join-Path $aliasGame 'BepInEx/core/BepInEx.Preloader.dll')) -eq 'pinned preloader') 'short or trailing-separator bundle paths install loader files beside the game executable'
        Check (@(Get-ChildItem -LiteralPath $aliasGame -Directory | Where-Object Name -NE 'BepInEx').Count -eq 0) 'path aliases do not create stray truncated bundle folders'
    }
} else { Write-Host 'SKIP short-path fixture: this volume has no 8.3 aliases.' }
$incompleteLoader = Join-Path $scratch 'incomplete-loader'
Put $incompleteLoader 'BepInEx/core/BepInEx.dll' 'incomplete loader'
$loaderError = $null
$beforeIncompleteLoader = Tree $plugins
try { Install-ModPayload $stage1 $incompleteLoader $game (Join-Path $scratch 'backups') -ReplaceLoader | Out-Null } catch { $loaderError = $_.Exception.Message }
Check ($loaderError -match 'Incomplete BepInEx payload') 'missing loader startup files are rejected instead of reporting success'
Check ((Tree $plugins) -eq $beforeIncompleteLoader) 'an incomplete loader does not replace an existing plugin'

$result = [ordered]@{
    checks = $script:checks
    gameRoot = $game
    fixtureRoot = $scratch
    backupPaths = @($script:backups)
    incompleteError = $incompleteError
    copyError = $copyError
    observedUtc = [DateTime]::UtcNow.ToString('o')
}
$report = Join-Path $scratch 'result.json'
[IO.File]::WriteAllText($report, ($result | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
Write-Host ($script:checks.ToString() + ' installer filesystem checks passed. No installed game or user save was accessed.')
Write-Host ('Evidence: ' + $report)
