<#
.SYNOPSIS
Runs the actual game and Scala mod headlessly in an isolated local sandbox.
#>
[CmdletBinding()]
param(
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\The Farmer Was Replaced',
    [string]$BundlePath,
    [ValidateSet('smoke','full')][string]$Suite = 'full',
    [string[]]$Cases,
    [switch]$AllowUnverifiedGame,
    [switch]$InjectFailure,
    [int]$TimeoutSeconds = 900
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
$base = [IO.Path]::GetFullPath((Join-Path $root '.local/integration'))
$run = Join-Path $base ('runs/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run -Force | Out-Null
$utf8 = [Text.UTF8Encoding]::new($false)
function JsonFile([string]$Path, $Value) { [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 30), $utf8) }
function Write-Junit($Results, [string]$Failure) {
    $cases=@(); if ($Results) { $cases=@($Results.scenarios) }
    $settings=[Xml.XmlWriterSettings]::new(); $settings.Indent=$true; $settings.Encoding=$utf8
    $xml=[Xml.XmlWriter]::Create((Join-Path $run 'results.xml'),$settings)
    try {
        $xml.WriteStartDocument(); $xml.WriteStartElement('testsuite')
        $xml.WriteAttributeString('name','FarmerScala.RealGame')
        $xml.WriteAttributeString('tests',[string]($cases.Count+1))
        $xml.WriteAttributeString('failures',[string](@($cases | Where-Object { !$_.passed }).Count + [int][bool]$Failure))
        $xml.WriteAttributeString('skipped',[string](@($cases | Where-Object { $_.skipped }).Count))
        foreach ($case in $cases) {
            $xml.WriteStartElement('testcase'); $xml.WriteAttributeString('name',$case.name)
            if ($case.skipped) { $xml.WriteElementString('skipped',[string]$case.skipReason) }
            elseif (!$case.passed) { $xml.WriteElementString('failure',[string]$case.error) }
            $xml.WriteEndElement()
        }
        $xml.WriteStartElement('testcase'); $xml.WriteAttributeString('name','process-and-isolation')
        if ($Failure) { $xml.WriteElementString('failure',$Failure) }
        $xml.WriteEndElement(); $xml.WriteEndElement(); $xml.WriteEndDocument()
    } finally { $xml.Dispose() }
}
function Copy-Tree([string]$From, [string]$To, [string[]]$Extra = @()) {
    & robocopy $From $To /E /R:0 /W:0 /NFL /NDL /NJH /NJS @Extra | Out-Null
    if ($LASTEXITCODE -ge 8) { throw ('Copy failed: ' + $From) }
}
function Snapshot-PlayerData {
    $values = @{}
    if (Test-Path -LiteralPath $realData) {
        Get-ChildItem -LiteralPath $realData -File -Recurse | ForEach-Object { $values[$_.FullName] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    }
    return $values
}
$realData = Join-Path $env:USERPROFILE 'AppData/LocalLow/TheFarmerWasReplaced/TheFarmerWasReplaced'
$before = Snapshot-PlayerData
$environmentNames = @('FARMER_SCALA_TEST_RUN','FARMER_SCALA_TEST_GAME','FARMER_SCALA_TEST_SUITE','FARMER_SCALA_TEST_FIXTURES','FARMER_SCALA_TEST_CASES','FARMER_SCALA_TEST_PHASE','SteamAppId','SteamGameId')
$oldEnvironment = @{}
foreach ($name in $environmentNames) { $oldEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
$runLock = $null
$process = $null; $failure = $null; $results = $null; $game = ''; $started = [DateTime]::UtcNow
$context = [ordered]@{ suite=$Suite; report=$run; started=$started.ToString('o'); diagnosticProbe=[bool]$AllowUnverifiedGame; gameLaunched=$false }
Write-Host ('Report directory: ' + $run)
try {
    $runLock=[IO.File]::Open((Join-Path $base 'runner.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    if ($TimeoutSeconds -lt 30 -or $TimeoutSeconds -gt 3600) { throw 'TimeoutSeconds must be between 30 and 3600.' }
    if (Get-Process TheFarmerWasReplaced -ErrorAction SilentlyContinue) { throw 'Close the ordinary game before running integration tests.' }
    $GamePath = (Resolve-Path -LiteralPath $GamePath).Path
    $core = Join-Path $GamePath 'TheFarmerWasReplaced_Data/Managed/Core.dll'
    $pins = Get-Content (Join-Path $root 'toolchain.lock.json') -Raw | ConvertFrom-Json
    $coreHash = (Get-FileHash -LiteralPath $core -Algorithm SHA256).Hash
    $context['gameCoreSha256'] = $coreHash
    $context['sourceCommit'] = (& git -C $root rev-parse HEAD 2>$null | Select-Object -First 1)
    $context['modVersion'] = (Select-String -Path (Join-Path $root 'src/Plugin/Plugin.cs') -Pattern 'BepInPlugin' | Select-Object -First 1).Line
    $context['expectedCoreSha256'] = $pins.game.coreSha256
    if ($coreHash -ne $pins.game.coreSha256 -and !$AllowUnverifiedGame) { throw 'Unsupported game build. The shipping guard was not changed. Use an explicit -AllowUnverifiedGame -BundlePath probe to investigate compatibility.' }
    if ($AllowUnverifiedGame -and !$BundlePath) { throw 'Diagnostic probes require an explicit prepared -BundlePath; shipping pins remain unchanged.' }
    Import-Module (Join-Path $root 'src/Installer/Installer.psm1') -Force -DisableNameChecking
    if ($BundlePath) {
        $BundlePath = (Resolve-Path -LiteralPath $BundlePath).Path
        $payload = Join-Path $BundlePath 'BepInEx/plugins/FarmerScala'
        $loader = $BundlePath
    } else {
        Write-Host 'Building a fresh source payload for the integration run...'
        $bundle = New-SourceBundle $root $GamePath
        $payload = $bundle.Payload; $loader = $bundle.Loader
    }
    Assert-Payload $payload
    # Include asset metadata so an update without a changed Core.dll cannot reuse stale scenes.
    $inventory = Get-ChildItem -LiteralPath $GamePath -File -Recurse | Where-Object { !$_.FullName.StartsWith((Join-Path $GamePath 'BepInEx') + '\', [StringComparison]::OrdinalIgnoreCase) }
    $signature = ($inventory | Sort-Object FullName | ForEach-Object { $_.FullName.Substring($GamePath.Length) + ':' + $_.Length + ':' + $_.LastWriteTimeUtc.Ticks }) -join "`n"
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $fingerprint = ([BitConverter]::ToString($sha.ComputeHash($utf8.GetBytes($signature)))).Replace('-','') } finally { $sha.Dispose() }
    $game = Join-Path $base ('game-' + $fingerprint.Substring(0,12))
    if (![IO.Path]::GetFullPath($game).StartsWith($base + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid sandbox target.' }
    $context['gameCopy'] = $game
    New-Item -ItemType Directory -Path $game -Force | Out-Null
    Copy-Tree $GamePath $game @('/XD','BepInEx')
    if ((Get-FileHash (Join-Path $game 'TheFarmerWasReplaced_Data/Managed/Core.dll') -Algorithm SHA256).Hash -ne $coreHash) { throw 'Game copy does not match the selected build.' }
    Copy-Tree (Join-Path $loader 'BepInEx') (Join-Path $game 'BepInEx') @('/XD','plugins','cache','config')
    Copy-Tree $payload (Join-Path $game 'BepInEx/plugins/FarmerScala')
    foreach ($file in @('winhttp.dll','doorstop_config.ini','.doorstop_version')) {
        if (Test-Path (Join-Path $loader $file)) { Copy-Item -LiteralPath (Join-Path $loader $file) -Destination $game -Force }
    }
    $dotnet = $null
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    $candidates = @(); if ($command) { $candidates += $command.Source }
    foreach ($directory in @($env:ProgramW6432,$env:ProgramFiles,${env:ProgramFiles(x86)},(Join-Path $root '.local/toolchain/dotnet-sdk'))) {
        if ($directory) { $candidates += Join-Path $directory 'dotnet.exe'; $candidates += Join-Path $directory 'dotnet/dotnet.exe' }
    }
    foreach ($candidate in $candidates | Select-Object -Unique) {
        if ((Test-Path -LiteralPath $candidate) -and (@(& $candidate --list-sdks 2>$null) -match '^10\.')) { $dotnet = $candidate; break }
    }
    if (!$dotnet) { throw 'A .NET 10 SDK is needed to build the test driver. The source installer can prepare one.' }
    foreach ($name in @('Isolation','Driver','Prepare')) {
        & $dotnet build (Join-Path $PSScriptRoot ($name + '/' + $name + '.csproj')) -c Release --nologo -v quiet -p:NuGetAudit=false ('-p:GameRoot=' + $GamePath) ('-p:BepInExDir=' + (Join-Path $game 'BepInEx/core')) ('-p:ModAssembly=' + (Join-Path $game 'BepInEx/plugins/FarmerScala/FarmerScala.dll'))
        if ($LASTEXITCODE -ne 0) { throw ('Could not build integration ' + $name) }
    }
    $patchers = Join-Path $game 'BepInEx/patchers'; $plugins = Join-Path $game 'BepInEx/plugins/Integration'
    New-Item -ItemType Directory -Path $patchers,$plugins -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Isolation/bin/Release/netstandard2.0/FarmerScala.Integration.Isolation.dll') -Destination $patchers -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Driver/bin/Release/netstandard2.1/FarmerScala.Integration.Driver.dll') -Destination $plugins -Force
    $data = Join-Path $run 'data'; $save = Join-Path $data 'Saves/Integration'; $sidecar = Join-Path $save '.farmer-scala'; $src = Join-Path $sidecar 'src'
    New-Item -ItemType Directory -Path $src -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $run 'test-run.marker'), $coreHash, $utf8)
    $id = [Guid]::NewGuid().ToString('N')
    JsonFile (Join-Path $sidecar 'language.json') @{ FormatVersion=1; SaveId=$id; Language='native-scala' }
    JsonFile (Join-Path $sidecar 'project.json') @{ FormatVersion=1; SaveId=$id; Language='native-scala'; Enabled=$true; SaveName='Integration'; Modules=@('main') }
    [IO.File]::WriteAllText((Join-Path $save 'main.py'), "pass`n", $utf8)
    $fixture = if ($Suite -eq 'full') { 'Fixtures/cold-start/main.scala' } else { 'Fixtures/smoke/main.scala' }
    $source = [IO.File]::ReadAllText((Join-Path $PSScriptRoot $fixture))
    if ($Suite -eq 'full') {
        # Unique invalid source prevents a cached successful build hiding bootstrap regressions.
        [IO.File]::WriteAllText((Join-Path $src 'Broken.scala'), ('// ' + $id + "`nval invalid: Int = " + '"not an Int"' + "`n"), $utf8)
        JsonFile (Join-Path $sidecar 'project.json') @{ FormatVersion=1; SaveId=$id; Language='native-scala'; Enabled=$true; SaveName='Integration'; Modules=@('main','Broken') }
    }
    if ($InjectFailure) { $source = $source.Replace('get_world_size() == 8', 'get_world_size() == 999') }
    [IO.File]::WriteAllText((Join-Path $src 'main.scala'), $source, $utf8)
    JsonFile (Join-Path $save 'save.json') @{ version=3; items=@{ serializeList=@(@{name='hay';nr=10000},@{name='wood';nr=10000},@{name='carrot';nr=10000}) }; unlocks=@('grass_1','soil','harvest','grassland','hay','straw_hat','true','false','loops','speed_3','expand_5','north','south','east','west','entities','plant','operators','items','carrots_1','grounds','none','senses','variables','functions','import','watering_1'); dockedFiles=@(); minimizedFiles=@();openDocPages=@();openFilePositions=@(@{key='main';value=@{x=100;y=60}}); openFileSizes=@(@{key='main';value=@{x=1000;y=700}});openFileScrollPositions=@(@{key='main';value=0}) }
    [IO.File]::WriteAllText((Join-Path $data 'options.txt'), "activeSave = Integration`nlanguage = EN`nautosave = disabled`nautosave progress = disabled`nvolume = 0`nambience volume = 0`nallow locked features = disabled`n", $utf8)
    [IO.File]::WriteAllText((Join-Path $game 'steam_appid.txt'), '2060160', $utf8)
    $env:FARMER_SCALA_TEST_RUN=$run; $env:FARMER_SCALA_TEST_GAME=$game; $env:FARMER_SCALA_TEST_SUITE=$Suite; $env:FARMER_SCALA_TEST_FIXTURES=Join-Path $PSScriptRoot 'Fixtures'; $env:FARMER_SCALA_TEST_CASES=$Cases -join ','
    $env:FARMER_SCALA_TEST_PHASE='main'
    $env:SteamAppId='2060160'; $env:SteamGameId='2060160'
    $prepareArgs = @((Join-Path $PSScriptRoot 'Prepare/bin/Release/net10.0/Prepare.dll'),$game,$run)
    if ($AllowUnverifiedGame) { $prepareArgs += '--allow-unverified' }
    & $dotnet @prepareArgs
    if ($LASTEXITCODE -ne 0) { throw 'Offline isolation failed. Game launch cancelled.' }
    if (Get-Process TheFarmerWasReplaced -ErrorAction SilentlyContinue) { throw 'Another game process started. Test launch cancelled.' }
    $launchArgs=@('-batchmode','-nographics','-logFile',('"'+(Join-Path $run 'Player.log')+'"'))
    $process=Start-Process -FilePath (Join-Path $game 'TheFarmerWasReplaced.exe') -ArgumentList $launchArgs -WorkingDirectory $game -WindowStyle Hidden -PassThru
    $context['gameLaunched']=$true
    [IO.File]::WriteAllText((Join-Path $run 'process-id.txt'),[string]$process.Id,$utf8)
    $limit=[DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while (!$process.WaitForExit(1000)) { if ([DateTime]::UtcNow -gt $limit) { throw 'Headless game timed out.' } }
    $context['processExitCode']=$process.ExitCode
    Copy-Item -LiteralPath (Join-Path $game 'BepInEx/LogOutput.log') -Destination (Join-Path $run 'BepInEx.log') -Force -ErrorAction SilentlyContinue
    if (!(Test-Path (Join-Path $run 'results.json'))) { throw 'Game exited without a completed test report. Inspect Player.log and BepInEx/LogOutput.log in the game copy.' }
    $results=Get-Content (Join-Path $run 'results.json') -Raw | ConvertFrom-Json
    $results.scenarios | Select-Object name,passed,checks | Format-Table | Out-Host
    if (!$results.complete -or !$results.passed -or $process.ExitCode -ne 0) { throw 'One or more real-game integration scenarios failed.' }
    if ($Suite -eq 'full' -and (!$Cases -or $Cases -contains 'save-reload-and-default-language')) {
        # Reuse only this disposable profile, then boot a genuinely new Unity process.
        $env:FARMER_SCALA_TEST_PHASE='restart'; $env:FARMER_SCALA_TEST_CASES=''
        $launchArgs=@('-batchmode','-nographics','-logFile',('"'+(Join-Path $run 'Player-restart.log')+'"'))
        $process=Start-Process -FilePath (Join-Path $game 'TheFarmerWasReplaced.exe') -ArgumentList $launchArgs -WorkingDirectory $game -WindowStyle Hidden -PassThru
        $context['restartProcessId']=$process.Id
        $limit=[DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
        while (!$process.WaitForExit(1000)) { if ([DateTime]::UtcNow -gt $limit) { throw 'Cold restart timed out.' } }
        $context['restartExitCode']=$process.ExitCode
        Copy-Item -LiteralPath (Join-Path $game 'BepInEx/LogOutput.log') -Destination (Join-Path $run 'BepInEx-restart.log') -Force -ErrorAction SilentlyContinue
        if (!(Test-Path (Join-Path $run 'results-restart.json'))) { throw 'Cold restart did not produce a report.' }
        $restart=Get-Content (Join-Path $run 'results-restart.json') -Raw | ConvertFrom-Json
        $results.scenarios=@($results.scenarios)+@($restart.scenarios)
        $results.checks=@($results.checks)+@($restart.checks)
        $results.errors=@($results.errors)+@($restart.errors)
        $results.passed=$results.passed -and $restart.passed
        $results.complete=$results.complete -and $restart.complete
        if (!$restart.complete -or !$restart.passed -or $process.ExitCode -ne 0) { throw 'Cold restart failed.' }
    }
} catch { $failure=$_.Exception.ToString() }
finally {
    if ($process -and !$process.HasExited) {
        if ($process.MainModule.FileName -eq (Join-Path $game 'TheFarmerWasReplaced.exe')) { $process.Kill(); $process.WaitForExit(10000) | Out-Null }
        else { $failure += "`nRefused to terminate a process outside the sandbox." }
    }
    if ($runLock) { $runLock.Dispose() }
    $after=Snapshot-PlayerData
    $changed=@($before.Keys | Where-Object { !$after.ContainsKey($_) -or $after[$_] -ne $before[$_] }) + @($after.Keys | Where-Object { !$before.ContainsKey($_) })
    $context['realPlayerDataUnchanged']=($changed.Count -eq 0)
    $context['playerFilesChecked']=$before.Count
    if ($changed.Count) { $failure += "`nReal player data changed: " + ($changed -join ', ') }
    foreach ($name in $environmentNames) { [Environment]::SetEnvironmentVariable($name,$oldEnvironment[$name],'Process') }
    $context['durationSeconds']=[Math]::Round(([DateTime]::UtcNow-$started).TotalSeconds,2)
    Write-Junit $results $failure
    JsonFile (Join-Path $run 'report.json') @{ passed=(!$failure); context=$context; error=$failure; results=$results }
    $encode={param($s) [Net.WebUtility]::HtmlEncode([string]$s)}
    $rows=''; if ($results) { foreach ($s in $results.scenarios) { $rows += '<tr><td>'+(& $encode $s.name)+'</td><td>'+$(if ($s.skipped){'SKIP'}elseif ($s.passed){'PASS'}else{'FAIL'})+'</td><td>'+(& $encode $s.checks)+'</td><td><pre>'+(& $encode $s.error)+'</pre></td></tr>' } }
    $html='<!doctype html><meta charset="utf-8"><title>Farmer Scala integration results</title><style>body{font:15px system-ui;background:#252525;color:#eee;max-width:1100px;margin:40px auto;padding:0 24px}table{border-collapse:collapse;width:100%}td,th{padding:12px;text-align:left;border-bottom:1px solid #555}pre{white-space:pre-wrap;max-width:620px}a{color:#d5df86}</style><h1>Real-game integration: '+$(if ($failure){'FAILED'}else{'PASSED'})+'</h1><p>'+(& $encode ($context|ConvertTo-Json -Compress))+'</p><table><tr><th>Scenario</th><th>Result</th><th>Checks</th><th>Failure</th></tr>'+$rows+'</table><pre>'+(& $encode $failure)+'</pre><p><a href="report.json">JSON report</a> · <a href="results.xml">JUnit XML</a> · <a href="BepInEx.log">Mod log</a> Â· <a href="progress.log">Progress</a> Â· <a href="Player.log">Unity log</a></p>'
    [IO.File]::WriteAllText((Join-Path $run 'Report.html'),$html,$utf8)
}
if ($failure) { throw ('Integration failed. Report: '+(Join-Path $run 'Report.html')+"`n"+$failure) }
Write-Host ('PASS real headless integration; report: '+(Join-Path $run 'Report.html')) -ForegroundColor Green
