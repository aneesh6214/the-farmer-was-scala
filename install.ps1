<#
.SYNOPSIS
Installs Farmer Scala through a guided Windows x64 walkthrough.
.DESCRIPTION
Run .\install.ps1 and follow the prompts. Steam libraries are searched first;
you can enter a different game folder. Source installs download and verify their
own build tools, build the mod, and prepare all offline Scala dependencies.
The game must be closed for installation. Existing language plugins and any
replaced loader files are backed up. Saves and unrelated plugins are untouched.

A bundled download uses the same installer without downloading build tools.
Its BepInEx/plugins/FarmerScala folder contains everything required at runtime.
The bundled download can be extracted
directly beside TheFarmerWasReplaced.exe, then launched normally through Steam.
Use this walkthrough when updating an existing modded installation.
.PARAMETER GamePath
The game folder containing TheFarmerWasReplaced.exe. Omit for Steam discovery.
.PARAMETER Yes
Use the detected/provided game path and skip confirmation prompts. Fail rather
than prompt if the game is running, detection is ambiguous, or a different
BepInEx version is already installed.
.EXAMPLE
.\install.ps1
.EXAMPLE
Get-Help .\install.ps1 -Detailed
#>
[CmdletBinding()]
param(
    [string]$GamePath,
    [switch]$Yes
)
$ErrorActionPreference = 'Stop'
$module = Join-Path $PSScriptRoot 'src/Installer/Installer.psm1'
if (!(Test-Path -LiteralPath $module)) { $module = Join-Path $PSScriptRoot 'installer/Installer.psm1' }
if (!(Test-Path -LiteralPath $module)) { throw 'Installer files are missing. Extract the complete download before running install.ps1.' }
Import-Module $module -Force -DisableNameChecking
try {
    Invoke-FarmerScalaInstaller -Root $PSScriptRoot -GamePath $GamePath -Yes:$Yes
} catch {
    Write-Host ''
    Write-Host ('Installation did not complete: ' + $_.Exception.Message) -ForegroundColor Red
    Write-Host 'Correct the reported problem and run install.ps1 again. The game is never launched or closed automatically.'
    throw
}
