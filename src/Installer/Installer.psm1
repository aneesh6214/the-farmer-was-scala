# Shared installation engine used by install.ps1 and the setup wizard.
# Source installs prepare an offline-ready bundle in .local/; bundled installs
# reuse their existing files. Both paths use the same backup and install logic.
# Main entry point: Invoke-FarmerScalaInstaller. New-SourceBundle also lets the
# local release tools prepare files without installing anything into the game.

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'

# Keep generated copy, extraction, and backup paths inside their intended directory.
function Assert-ChildPath([string]$Path, [string]$Parent) {
    $full = [IO.Path]::GetFullPath($Path)
    $base = [IO.Path]::GetFullPath($Parent).TrimEnd('\') + '\'
    if (!$full.StartsWith($base, [StringComparison]::OrdinalIgnoreCase)) { throw "Path is outside the expected folder: $full" }
    return $full
}

function Write-InstallerStep([string]$Message) {
    Write-Host ''
    Write-Host $Message -ForegroundColor Cyan
}

# Find the game across Steam registry entries, default locations, and additional libraries.
function Get-SteamGamePaths {
    $roots = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($key in @('HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam', 'HKLM:\SOFTWARE\Valve\Steam')) {
        $item = Get-ItemProperty -LiteralPath $key -ErrorAction SilentlyContinue
        if ($null -eq $item) { continue }
        foreach ($name in @('SteamPath', 'InstallPath')) {
            $property = $item.PSObject.Properties[$name]
            if ($property -and $property.Value) { [void]$roots.Add([string]$property.Value) }
        }
    }
    foreach ($base in @(${env:ProgramFiles(x86)}, $env:ProgramW6432, $env:ProgramFiles)) {
        if ($base) { [void]$roots.Add((Join-Path $base 'Steam')) }
    }
    $libraries = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($steam in $roots) {
        [void]$libraries.Add($steam)
        $vdf = Join-Path $steam 'steamapps/libraryfolders.vdf'
        if (!(Test-Path -LiteralPath $vdf)) { continue }
        foreach ($match in [regex]::Matches([IO.File]::ReadAllText($vdf), '"path"\s+"((?:\\.|[^"\\])*)"')) {
            [void]$libraries.Add($match.Groups[1].Value.Replace('\\', '\'))
        }
    }
    foreach ($library in $libraries) {
        $name = 'The Farmer Was Replaced'
        $manifest = Join-Path $library 'steamapps/appmanifest_2060160.acf'
        if (Test-Path -LiteralPath $manifest) {
            $match = [regex]::Match([IO.File]::ReadAllText($manifest), '"installdir"\s+"([^"\r\n]+)"')
            if ($match.Success -and [IO.Path]::GetFileName($match.Groups[1].Value) -eq $match.Groups[1].Value) { $name = $match.Groups[1].Value }
        }
        $path = Join-Path $library ('steamapps/common/' + $name)
        if (Test-Path -LiteralPath (Join-Path $path 'TheFarmerWasReplaced.exe')) { [IO.Path]::GetFullPath($path) }
    }
}

# Require a real game folder and the exact supported Core.dll before proceeding.
function Assert-GamePath([string]$Path, $Pins) {
    $path = [IO.Path]::GetFullPath($Path.Trim().Trim('"'))
    if ([IO.Path]::GetFileName($path) -eq 'TheFarmerWasReplaced.exe') { $path = Split-Path $path -Parent }
    foreach ($file in @('TheFarmerWasReplaced.exe', 'TheFarmerWasReplaced_Data/Managed/Core.dll')) {
        if (!(Test-Path -LiteralPath (Join-Path $path $file) -PathType Leaf)) { throw "Not the game folder: missing $file in $path" }
    }
    $hash = (Get-FileHash -LiteralPath (Join-Path $path 'TheFarmerWasReplaced_Data/Managed/Core.dll') -Algorithm SHA256).Hash
    if ($hash -ne $Pins.game.coreSha256) { throw "This game build is not supported by this mod version. Core.dll SHA256: $hash" }
    return $path
}

# Use an explicit path or guide the user through choosing a detected Steam installation.
function Resolve-GamePath([string]$Requested, $Pins, [switch]$Yes) {
    if ($Requested) { return Assert-GamePath $Requested $Pins }
    $distinct = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($path in Get-SteamGamePaths) { [void]$distinct.Add($path) }
    $found = @($distinct)
    if ($Yes) {
        if ($found.Count -ne 1) { throw 'Specify -GamePath because Steam discovery did not find exactly one installation.' }
        return Assert-GamePath $found[0] $Pins
    }
    if ($found.Count -eq 1) {
        Write-Host ('Found: ' + $found[0])
        $answer = Read-Host 'Press Enter to use this folder, or paste a different game folder'
        if (!$answer.Trim()) { $answer = $found[0] }
    } else {
        foreach ($path in $found) { Write-Host ('Found: ' + $path) }
        $answer = Read-Host 'Paste the game folder (Steam > Manage > Browse local files)'
    }
    while ($true) {
        if (!$answer.Trim()) { throw 'No game folder was selected.' }
        try { return Assert-GamePath $answer $Pins } catch { Write-Host $_.Exception.Message -ForegroundColor Yellow }
        $answer = Read-Host 'Paste a valid game folder, or press Enter to cancel'
    }
}

# Reuse checksum-verified downloads; keep partial or mismatched files out of the usable cache.
function Get-VerifiedDownload([string]$Url, [string]$Name, [string]$Algorithm, [string]$Hash, [string]$Cache) {
    New-Item -ItemType Directory -Path $Cache -Force | Out-Null
    $target = Assert-ChildPath (Join-Path $Cache $Name) $Cache
    if (Test-Path -LiteralPath $target) {
        if ((Get-FileHash -LiteralPath $target -Algorithm $Algorithm).Hash -eq $Hash) { return $target }
        [IO.File]::Move($target, $target + '.invalid-' + [Guid]::NewGuid().ToString('N'))
    }
    Write-Host ('Downloading ' + $Name)
    $partial = $target + '.download-' + [Guid]::NewGuid().ToString('N')
    Invoke-WebRequest -Uri $Url -OutFile $partial -UseBasicParsing
    if ((Get-FileHash -LiteralPath $partial -Algorithm $Algorithm).Hash -ne $Hash) { throw "Checksum mismatch for $Name. The download was not used." }
    [IO.File]::Move($partial, $target)
    return $target
}

# Extract a verified ZIP while rejecting entries that would escape its destination.
function Expand-VerifiedArchive([string]$Zip, [string]$Destination) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    $archive = [IO.Compression.ZipFile]::OpenRead($Zip)
    try {
        foreach ($entry in $archive.Entries) {
            $target = Assert-ChildPath (Join-Path $Destination $entry.FullName) $Destination
            if (!$entry.Name) { New-Item -ItemType Directory -Path $target -Force | Out-Null; continue }
            New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
        }
    } finally { $archive.Dispose() }
}

# Capture detailed tool output in the build log and surface failures in the walkthrough.
function Invoke-BuildCommand([string]$Exe, [string[]]$Arguments, [string]$Log) {
    $previous = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $lines = @(& $Exe @Arguments 2>&1)
        $code = $LASTEXITCODE
    } finally { $ErrorActionPreference = $previous }
    $lines | Out-File -LiteralPath $Log -Append -Encoding utf8
    if ($code -ne 0) {
        $lines | Select-Object -Last 15 | ForEach-Object { Write-Host $_ }
        throw "A build command failed (exit $code). Details: $Log"
    }
}

# Prepare pinned build tools and a clean BepInEx copy under .local/, leaving the game unchanged.
function Initialize-BuildTools([string]$Root, $Pins, [string]$Log) {
    $cache = Join-Path $Root '.local/downloads'
    $tools = Join-Path $Root '.local/toolchain'
    New-Item -ItemType Directory -Path $tools -Force | Out-Null
    $sbt = Get-VerifiedDownload $Pins.sbt.url ('sbt-launch-' + $Pins.sbt.version + '.jar') SHA256 $Pins.sbt.sha256 $cache
    Copy-Item -LiteralPath $sbt -Destination $tools -Force
    if (!(Test-Path -LiteralPath (Join-Path $tools 'java/bin/java.exe'))) {
        $zip = Get-VerifiedDownload $Pins.java.url 'temurin-win-x64.zip' SHA256 $Pins.java.sha256 $cache
        $extract = Join-Path $Root '.local/java-extract'
        Expand-VerifiedArchive $zip $extract
        $java = Get-ChildItem -LiteralPath $extract -Directory | Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'bin/java.exe') } | Select-Object -First 1
        if (!$java) { throw 'The Java archive did not contain bin/java.exe.' }
        Copy-Item -LiteralPath $java.FullName -Destination (Join-Path $tools 'java') -Recurse
    }
    if (!(Test-Path -LiteralPath (Join-Path $tools 'dotnet-x64/dotnet.exe'))) {
        $zip = Get-VerifiedDownload $Pins.dotnet.url 'dotnet-runtime-win-x64.zip' SHA512 $Pins.dotnet.sha512 $cache
        Expand-VerifiedArchive $zip (Join-Path $tools 'dotnet-x64')
    }
    # Prefer an installed .NET SDK; download a private SDK only when needed.
    $dotnet = $null
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    $candidates = @()
    if ($command) { $candidates += $command.Source }
    foreach ($base in @($env:ProgramW6432, $env:ProgramFiles, ${env:ProgramFiles(x86)})) {
        if ($base) { $candidates += Join-Path $base 'dotnet/dotnet.exe' }
    }
    foreach ($candidate in ($candidates | Select-Object -Unique)) {
        if (!(Test-Path -LiteralPath $candidate)) { continue }
        if (@(& $candidate --list-sdks 2>$null) -match '^10\.') { $dotnet = $candidate; break }
    }
    if (!$dotnet) {
        Write-Host 'No .NET 10 SDK found; installing a private build SDK in .local.'
        $zip = Get-VerifiedDownload $Pins.dotnetSdk.url 'dotnet-sdk-win-x64.zip' SHA512 $Pins.dotnetSdk.sha512 $cache
        Expand-VerifiedArchive $zip (Join-Path $tools 'dotnet-sdk')
        $dotnet = Join-Path $tools 'dotnet-sdk/dotnet.exe'
    }
    foreach ($directory in @('coursier','sbt-boot','sbt-global','ivy')) { New-Item -ItemType Directory -Path (Join-Path $tools $directory) -Force | Out-Null }
    @('[repositories]', 'maven-central: https://repo.maven.apache.org/maven2/') | Set-Content -LiteralPath (Join-Path $tools 'repositories') -Encoding ascii
    # Keep loader build references, notices, and corresponding sources separate from the live game.
    $zip = Get-VerifiedDownload $Pins.bepinex.url ('BepInEx_win_x64_' + $Pins.bepinex.version + '.zip') SHA256 $Pins.bepinex.sha256 $cache
    $bep = Join-Path $Root ('.local/bepinex/' + $Pins.bepinex.version)
    Expand-VerifiedArchive $zip $bep
    $license = Get-VerifiedDownload $Pins.bepinex.licenseUrl ('BepInEx-' + $Pins.bepinex.version + '-LICENSE.txt') SHA256 $Pins.bepinex.licenseSha256 $cache
    Copy-Item -LiteralPath $license -Destination (Join-Path $bep 'BepInEx/LICENSE.txt') -Force
    $notices = Join-Path $bep 'BepInEx/licenses'
    New-Item -ItemType Directory -Path $notices -Force | Out-Null
    foreach ($notice in $Pins.loaderNotices) {
        $file = Get-VerifiedDownload $notice.url $notice.name SHA256 $notice.sha256 $cache
        Copy-Item -LiteralPath $file -Destination $notices -Force
    }
    $sources = Join-Path $bep 'BepInEx/sources'
    New-Item -ItemType Directory -Path $sources -Force | Out-Null
    $doorstop = Get-VerifiedDownload $Pins.doorstopSource.url 'UnityDoorstop-source.zip' SHA256 $Pins.doorstopSource.sha256 $cache
    Copy-Item -LiteralPath $doorstop -Destination $sources -Force
    return @{ Dotnet = $dotnet; Toolchain = $tools; BepInEx = $bep }
}

# Build the C# plugin/services and Scala editor helper, then fetch library sources for editor navigation.
function Build-Mod([string]$Root, [string]$GamePath, $Tools, [string]$Log) {
    foreach ($project in @('Plugin','Compiler','Worker')) {
        $file = if ($project -eq 'Plugin') { 'FarmerScala.csproj' } else { 'FarmerScala.' + $project + '.csproj' }
        Write-Host ('Building ' + $project + '...')
        Invoke-BuildCommand $Tools.Dotnet @('build', (Join-Path $Root ('src/' + $project + '/' + $file)), '-c', 'Release', '--nologo', '-p:NuGetAudit=false', ('-p:GameRoot=' + $GamePath), ('-p:BepInExDir=' + (Join-Path $Tools.BepInEx 'BepInEx/core'))) $Log
    }
    Write-Host 'Building Scala editor services (the first build downloads dependencies)...'
    $previous = $env:COURSIER_CACHE
    try {
        $env:COURSIER_CACHE = Join-Path $Tools.Toolchain 'coursier'
        Push-Location -LiteralPath (Join-Path $Root 'src/Analysis')
        try {
            Invoke-BuildCommand (Join-Path $Tools.Toolchain 'java/bin/java.exe') @('-Xmx2g', '-Dsbt.server.autostart=false', '-Dsbt.boot.server=false', '-Dsbt.supershell=false', '-Dsbt.color=false', '-Dsbt.log.noformat=true', ('-Dsbt.boot.directory=' + $Tools.Toolchain + '/sbt-boot'), ('-Dsbt.global.base=' + $Tools.Toolchain + '/sbt-global'), ('-Dsbt.ivy.home=' + $Tools.Toolchain + '/ivy'), ('-Dsbt.repository.config=' + $Tools.Toolchain + '/repositories'), '-Dsbt.override.build.repos=true', '-jar', (Join-Path $Tools.Toolchain 'sbt-launch-1.11.7.jar'), 'stageAnalysis') $Log
        } finally { Pop-Location }
    } finally { $env:COURSIER_CACHE = $previous }
    $sources = Join-Path $Root '.local/analysis/sources'
    New-Item -ItemType Directory -Path $sources -Force | Out-Null
    $sourcePins = Join-Path $Root 'src/Analysis/library-sources.lock.json'
    foreach ($source in (Get-Content -LiteralPath $sourcePins -Raw | ConvertFrom-Json)) {
        $download = Get-VerifiedDownload $source.url $source.name SHA256 $source.sha256 (Join-Path $Root '.local/downloads')
        Copy-Item -LiteralPath $download -Destination $sources -Force
    }
    Copy-Item -LiteralPath $sourcePins -Destination (Join-Path $sources 'manifest.json') -Force
}

# Compile a tiny sample to populate Scala dependencies or verify that a prepared bundle works offline.
function Initialize-OfflineCompiler([string]$Sdk, [string]$Toolchain, [string]$Compiler, [string]$Runtime, [string]$Work, [string]$Log, [switch]$Offline) {
    $project = Join-Path $Work 'project'
    New-Item -ItemType Directory -Path $project -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $project 'main.scala'), "val checked = List(1, 2, 3).map(_ + 1)\nval direction: Directions = Directions.North\n".Replace('\n', "`n"), [Text.UTF8Encoding]::new($false))
    $request = @{ id = 1; op = 'compile'; project = $project; entry = 'main.scala'; output = (Join-Path $Work 'build') } | ConvertTo-Json -Compress
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $Runtime
    # All paths are generated by this installer; Windows filenames cannot contain quotes.
    $info.Arguments = '"' + $Compiler + '" --sdk "' + $Sdk + '" --toolchain "' + $Toolchain + '"'
    $info.UseShellExecute = $false; $info.CreateNoWindow = $true
    $info.RedirectStandardInput = $true; $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
    $info.EnvironmentVariables['FARMER_SCALA_OFFLINE'] = $(if ($Offline) { 'true' } else { 'false' })
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $info
    try {
        if (!$process.Start()) { throw 'Could not start the Scala dependency preparation service.' }
        $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
        $process.StandardInput.WriteLine($request); $process.StandardInput.Close()
        $timer = [Diagnostics.Stopwatch]::StartNew()
        while (!$process.WaitForExit(1000)) {
            Write-Progress -Activity 'Preparing Scala for offline use' -Status ($timer.Elapsed.ToString('mm\:ss') + ' elapsed; the first preparation can take several minutes')
            if ($timer.Elapsed.TotalMinutes -gt 10) {
                & taskkill.exe /PID $process.Id /T /F | Out-Null
                throw 'Scala dependency preparation timed out after 10 minutes.'
            }
        }
        Write-Progress -Activity 'Preparing Scala for offline use' -Completed
        $errorText = $stderr.GetAwaiter().GetResult(); $text = $stdout.GetAwaiter().GetResult()
        @($errorText, $text) | Out-File -LiteralPath $Log -Append -Encoding utf8
        $result = $text.Trim() | ConvertFrom-Json
        if ($process.ExitCode -ne 0 -or !$result.ok) { throw ('Scala preparation failed. ' + (($result.diagnostics | ForEach-Object message) -join ' ') + " See $Log") }
    } finally { $process.Dispose() }
}

# Assemble a self-contained runtime folder with the mod, SDK, tools, dependency caches, and licenses.
function New-ModPayload([string]$Root, $Tools, [string]$Destination) {
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    foreach ($file in @('FarmerScala.dll','Newtonsoft.Json.dll')) {
        Copy-Item -LiteralPath (Join-Path $Root ('src/Plugin/bin/Release/netstandard2.1/' + $file)) -Destination $Destination
    }
    foreach ($service in @('Compiler','Worker')) {
        $target = Join-Path $Destination ('tools/' + $service.ToLowerInvariant())
        New-Item -ItemType Directory -Path $target -Force | Out-Null
        Get-ChildItem -LiteralPath (Join-Path $Root ('src/' + $service + '/bin/Release/net10.0')) -File | Where-Object Extension -In @('.dll','.json') | Copy-Item -Destination $target
    }
    $media = Join-Path $Destination 'assets'
    New-Item -ItemType Directory -Path $media -Force | Out-Null
    foreach ($file in @('the-farmer-was-scala.mp4','the-farmer-was-scala.jpg')) {
        Copy-Item -LiteralPath (Join-Path $Root ('assets/' + $file)) -Destination $media
    }
    Copy-Item -LiteralPath (Join-Path $Root 'sdk') -Destination $Destination -Recurse
    Copy-Item -LiteralPath (Join-Path $Root '.local/analysis') -Destination (Join-Path $Destination 'tools/analysis') -Recurse
    Copy-Item -LiteralPath (Join-Path $Tools.Toolchain 'dotnet-x64') -Destination (Join-Path $Destination 'runtime') -Recurse
    # Carry the populated dependency caches into the payload so normal play needs no downloads.
    $runtimeTools = Join-Path $Destination 'toolchain'
    New-Item -ItemType Directory -Path $runtimeTools -Force | Out-Null
    foreach ($name in @('java','coursier','sbt-boot','sbt-global','ivy')) {
        $base = (Get-Item -LiteralPath (Join-Path $Tools.Toolchain $name) -Force).FullName
        $sourcePrefix = $base.TrimEnd('\') + '\'
        $target = Join-Path $runtimeTools $name
        New-Item -ItemType Directory -Path $target -Force | Out-Null
        Get-ChildItem -LiteralPath $base -Recurse -File | Where-Object Name -NotMatch '\.lock$|\.part$' | ForEach-Object {
            $relative = (Assert-ChildPath $_.FullName $base).Substring($sourcePrefix.Length)
            $file = Assert-ChildPath (Join-Path $target $relative) $target
            New-Item -ItemType Directory -Path (Split-Path $file -Parent) -Force | Out-Null
            Copy-Item -LiteralPath $_.FullName -Destination $file
        }
    }
    Copy-Item -LiteralPath (Join-Path $Tools.Toolchain 'sbt-launch-1.11.7.jar'),(Join-Path $Tools.Toolchain 'repositories') -Destination $runtimeTools
    Copy-Item -LiteralPath (Join-Path $Root 'LICENSE'),(Join-Path $Root 'toolchain.lock.json') -Destination $Destination
    Copy-Item -LiteralPath (Join-Path $Root 'assets/SOURCES.txt') -Destination (Join-Path $Destination 'ICON_SOURCES.txt')
    $pins = Get-Content -LiteralPath (Join-Path $Root 'toolchain.lock.json') -Raw | ConvertFrom-Json
    $notices = Join-Path $Destination 'licenses'
    New-Item -ItemType Directory -Path $notices -Force | Out-Null
    foreach ($notice in $pins.runtimeNotices) {
        $file = Get-VerifiedDownload $notice.url $notice.name SHA256 $notice.sha256 (Join-Path $Root '.local/downloads')
        Copy-Item -LiteralPath $file -Destination $notices
    }
    @(
        'Farmer Scala runtime notices', '',
        '.NET: retain runtime/LICENSE.txt and runtime/ThirdPartyNotices.txt.',
        'Java: retain toolchain/java/NOTICE and its complete legal/ directory.',
        'Java sources: supplied in the ZIP at THIRD_PARTY_SOURCES/Temurin-sources.tar.gz.',
        'Exact Java binary/source URLs and hashes are in toolchain.lock.json.',
        'Jint, Acornima and Newtonsoft.Json: full license texts are in licenses/.',
        'Scala, Scala.js, sbt, Scalafmt, Scalameta and dependencies: original JARs',
        'and embedded META-INF license/notice metadata remain intact.',
        'Scala library source archives are in tools/analysis/sources/.',
        'BepInEx: complete bundle retains BepInEx/LICENSE.txt and licenses/.',
        'Doorstop corresponding sources are included under BepInEx/sources/.',
        'Game and Unity assemblies are not included; the game is installed separately.'
    ) | Set-Content -LiteralPath (Join-Path $Destination 'THIRD_PARTY_NOTICES.txt') -Encoding utf8
    return $Destination
}

# Reject incomplete bundles before replacing any part of an existing installation.
function Assert-Payload([string]$Payload) {
    foreach ($file in @('FarmerScala.dll','Newtonsoft.Json.dll','runtime/dotnet.exe','toolchain/java/bin/java.exe','toolchain/sbt-launch-1.11.7.jar','tools/compiler/FarmerScala.Compiler.dll','tools/worker/FarmerScala.Worker.dll','tools/analysis/classpath.txt','sdk/game/api.scala','sdk/game/GameEnums.scala','assets/the-farmer-was-scala.mp4','assets/the-farmer-was-scala.jpg')) {
        if (!(Test-Path -LiteralPath (Join-Path $Payload $file) -PathType Leaf)) { throw "Incomplete mod payload: $file" }
    }
}

# Stage the replacement, back up affected files, then install; restore originals if replacement fails.
function Install-ModPayload([string]$Payload, [string]$Loader, [string]$GamePath, [string]$BackupRoot, [switch]$ReplaceLoader) {
    Assert-Payload $Payload
    if (Get-Process -Name TheFarmerWasReplaced -ErrorAction SilentlyContinue) { throw 'Close The Farmer Was Replaced before installation.' }
    $destination = Join-Path $GamePath 'BepInEx/plugins/FarmerScala'
    # Finish copying the new mod before moving any installed files into backup.
    $prepared = Assert-ChildPath (Join-Path $GamePath ('.farmer-scala-prepared-' + [Guid]::NewGuid().ToString('N'))) $GamePath
    Copy-Item -LiteralPath $Payload -Destination $prepared -Recurse
    $backup = Assert-ChildPath (Join-Path $BackupRoot ([DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))) $BackupRoot
    New-Item -ItemType Directory -Path $backup -Force | Out-Null
    $restore = [Collections.Generic.List[object]]::new()
    $installed = [Collections.Generic.List[string]]::new()
    # Prepare each loader file before changing the current installation.
    $loaderFiles = @()
    $loaderRequired = @('winhttp.dll','doorstop_config.ini','BepInEx/core/BepInEx.dll','BepInEx/core/BepInEx.Preloader.dll','BepInEx/core/0Harmony.dll')
    if ($ReplaceLoader) {
        # Inno Setup can supply an 8.3 temp path. Enumeration expands it, so both
        # sides must use the same canonical directory before taking a relative path.
        $Loader = (Get-Item -LiteralPath $Loader -Force).FullName
        $loaderPrefix = $Loader.TrimEnd('\') + '\'
        foreach ($required in $loaderRequired) {
            if (!(Test-Path -LiteralPath (Join-Path $Loader $required) -PathType Leaf)) { throw "Incomplete BepInEx payload: $required" }
        }
        # A complete bundle also contains the mod and installer. Loader updates
        # must never traverse those trees or overwrite plugins before backup.
        foreach ($directory in @('BepInEx/core','BepInEx/licenses','BepInEx/sources')) {
            $path = Join-Path $Loader $directory
            if (Test-Path -LiteralPath $path) { $loaderFiles += Get-ChildItem -LiteralPath $path -File -Recurse }
        }
        foreach ($name in @('.doorstop_version','doorstop_config.ini','winhttp.dll','BepInEx/LICENSE.txt')) {
            $path = Join-Path $Loader $name
            if (Test-Path -LiteralPath $path -PathType Leaf) { $loaderFiles += Get-Item -LiteralPath $path -Force }
        }
    }
    try {
        if (Get-Process -Name TheFarmerWasReplaced -ErrorAction SilentlyContinue) { throw 'The game started during preparation. Close it and retry.' }
        foreach ($file in $loaderFiles) {
            $relative = (Assert-ChildPath $file.FullName $Loader).Substring($loaderPrefix.Length)
            $target = Assert-ChildPath (Join-Path $GamePath $relative) $GamePath
            if (Test-Path -LiteralPath $target) {
                $saved = Assert-ChildPath (Join-Path $backup ('loader/' + $relative)) $backup
                New-Item -ItemType Directory -Path (Split-Path $saved -Parent) -Force | Out-Null
                [IO.File]::Move($target, $saved)
                $restore.Add(@{ Source = $saved; Target = $target })
            }
            New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
            $installed.Add($target)
            Copy-Item -LiteralPath $file.FullName -Destination $target
        }
        if ($ReplaceLoader) {
            foreach ($required in $loaderRequired) {
                if (!(Test-Path -LiteralPath (Join-Path $GamePath $required) -PathType Leaf)) { throw "BepInEx installation is incomplete: $required" }
            }
        }
        # Retire previous language plugins together; leave unrelated plugins in place.
        foreach ($name in @('FarmerScala','FarmerPython')) {
            $old = Assert-ChildPath (Join-Path $GamePath ('BepInEx/plugins/' + $name)) $GamePath
            if (Test-Path -LiteralPath $old) {
                $saved = Assert-ChildPath (Join-Path $backup ('plugins/' + $name)) $backup
                New-Item -ItemType Directory -Path (Split-Path $saved -Parent) -Force | Out-Null
                [IO.Directory]::Move($old, $saved)
                $restore.Add(@{ Source = $saved; Target = $old })
            }
        }
        New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
        [IO.Directory]::Move($prepared, $destination)
    } catch {
        # Roll back files written by this invocation only. Never recurse-delete
        # the game, existing plugin trees or arbitrary user-selected paths.
        foreach ($file in $installed) {
            $checked = Assert-ChildPath $file $GamePath
            if ([IO.File]::Exists($checked)) { [IO.File]::Delete($checked) }
        }
        $reverse = $restore.ToArray()
        [array]::Reverse($reverse)
        foreach ($item in $reverse) {
            if ([IO.Directory]::Exists($item.Source)) { [IO.Directory]::Move($item.Source, $item.Target) }
            else { [IO.File]::Move($item.Source, $item.Target) }
        }
        throw
    }
    Write-Host ('Installed. Previous files are preserved at ' + $backup) -ForegroundColor Green
    return $backup
}

# Prepare and check a source build without installing it; shared by source installation and release tooling.
function New-SourceBundle([string]$Root, [string]$GamePath) {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $pins = Get-Content -LiteralPath (Join-Path $Root 'toolchain.lock.json') -Raw | ConvertFrom-Json
    $game = Assert-GamePath $GamePath $pins
    $logs = Join-Path $Root '.local/install-logs'
    New-Item -ItemType Directory -Path $logs -Force | Out-Null
    $log = Join-Path $logs ([DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '.log')
    Write-Host ('Detailed build log: ' + $log)
    Write-InstallerStep '2. Prepare pinned tools (tool downloads are checksum-verified)'
    $tools = Initialize-BuildTools $Root $pins $log
    Write-InstallerStep '3. Build the mod and editor services'
    Build-Mod $Root $game $tools $log
    Write-InstallerStep '4. Prepare Scala for offline play'
    Initialize-OfflineCompiler (Join-Path $Root 'sdk') $tools.Toolchain (Join-Path $Root 'src/Compiler/bin/Release/net10.0/FarmerScala.Compiler.dll') (Join-Path $tools.Toolchain 'dotnet-x64/dotnet.exe') (Join-Path $Root '.local/bootstrap') $log
    # Validate a fresh copy with dependency downloads disabled before it can be installed or packaged.
    $payload = New-ModPayload $Root $tools (Join-Path $Root ('.local/payloads/' + [Guid]::NewGuid().ToString('N') + '/FarmerScala'))
    Assert-Payload $payload
    Write-Host 'Checking the prepared payload with network downloads disabled...'
    Initialize-OfflineCompiler (Join-Path $payload 'sdk') (Join-Path $payload 'toolchain') (Join-Path $payload 'tools/compiler/FarmerScala.Compiler.dll') (Join-Path $payload 'runtime/dotnet.exe') (Join-Path $Root ('.local/offline-check/' + [Guid]::NewGuid().ToString('N'))) $log -Offline
    return [PSCustomObject]@{ Payload = $payload; Loader = $tools.BepInEx; Pins = $pins }
}

# Run the walkthrough: select the game, obtain a bundle, confirm loader changes, and install with backups.
function Invoke-FarmerScalaInstaller([string]$Root, [string]$GamePath, [switch]$Yes, [switch]$ReplaceLoader, [string]$BackupRoot) {
    if ($env:OS -ne 'Windows_NT' -or ![Environment]::Is64BitOperatingSystem) { throw 'This mod supports Windows x64 only.' }
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $pins = Get-Content -LiteralPath (Join-Path $Root 'toolchain.lock.json') -Raw | ConvertFrom-Json
    $source = Test-Path -LiteralPath (Join-Path $Root 'src/Plugin/FarmerScala.csproj')
    Write-Host 'Farmer Scala installer - Windows x64' -ForegroundColor Green
    Write-InstallerStep '1. Locate and check the game'
    $game = Resolve-GamePath $GamePath $pins -Yes:$Yes
    Write-Host ('Using: ' + $game)
    # A checkout builds its payload here; a ZIP or EXE already contains the prepared payload.
    if ($source) {
        $bundle = New-SourceBundle $Root $game
        $payload = $bundle.Payload
        $loader = $bundle.Loader
    } else {
        $payload = Join-Path $Root 'BepInEx/plugins/FarmerScala'
        Assert-Payload $payload
        $loader = $Root
    }
    # Keep a complete matching loader; ask before replacing a different installed version.
    $core = Join-Path $game 'BepInEx/core/BepInEx.dll'
    $replace = !(Test-Path -LiteralPath $core)
    if (!$replace) {
        $version = [Reflection.AssemblyName]::GetAssemblyName($core).Version.ToString()
        if ($version -ne $pins.bepinex.version) {
            if ($Yes -and !$ReplaceLoader) { throw "Installed BepInEx is $version, but this mod pins $($pins.bepinex.version). Run interactively to choose whether to replace the loader." }
            if (!$ReplaceLoader -and (Read-Host "BepInEx $version is installed. Back it up and replace loader files with $($pins.bepinex.version)? [y/N]") -notmatch '^(y|yes)$') { throw 'Loader replacement cancelled.' }
            $replace = $true
        }
    }
    foreach ($required in @('winhttp.dll','doorstop_config.ini','BepInEx/core/BepInEx.Preloader.dll','BepInEx/core/0Harmony.dll')) {
        if (!(Test-Path -LiteralPath (Join-Path $game $required) -PathType Leaf)) { $replace = $true }
    }
    if ($replace -and !(Test-Path -LiteralPath (Join-Path $loader 'BepInEx/core/BepInEx.dll'))) { throw 'The bundled BepInEx files are missing. Extract the entire download and try again.' }
    Write-InstallerStep '5. Ready to install'
    Write-Host ('Destination: ' + (Join-Path $game 'BepInEx/plugins/FarmerScala'))
    Write-Host ('BepInEx: ' + $pins.bepinex.version + $(if ($replace) { ' (install pinned loader)' } else { ' (keep existing matching loader)' }))
    Write-Host 'The previous Scala/Python language plugins and replaced loader files will be backed up. Saves and unrelated plugins are preserved.'
    if (!$Yes -and (Read-Host 'Install now? [Y/n]') -match '^(n|no)$') { Write-Host 'Cancelled before changing the game.'; return }
    # The user closes the game; the installer never stops or launches it automatically.
    while (Get-Process -Name TheFarmerWasReplaced -ErrorAction SilentlyContinue) {
        if ($Yes) { throw 'Close the game and run install.ps1 again.' }
        [void](Read-Host 'Close The Farmer Was Replaced, then press Enter to continue')
    }
    Write-InstallerStep '6. Install and preserve backups'
    if (!$BackupRoot) { $BackupRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'FarmerScala/install-backups' }
    [void](Install-ModPayload $payload $loader $game $backupRoot -ReplaceLoader:$replace)
    Write-Host 'Done. Launch the game through Steam and create a Scala 3 save.' -ForegroundColor Green
}

Export-ModuleMember -Function Invoke-FarmerScalaInstaller, New-SourceBundle, Get-SteamGamePaths, Resolve-GamePath, Assert-GamePath, Assert-Payload, Install-ModPayload, Assert-ChildPath, Get-VerifiedDownload
