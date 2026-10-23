# Regenerates the AIMLINK placeholder SFX into Assets/AimLink/Audio.
# Usage: powershell -ExecutionPolicy Bypass -File Tools\SfxGen\generate.ps1
$ErrorActionPreference = 'Stop'
$src = Get-Content -Raw (Join-Path $PSScriptRoot 'SfxGen.cs')
Add-Type -TypeDefinition $src -Language CSharp
$out = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\Assets\AimLink\Audio'))
[SfxGen]::Run($out)
Write-Host "Wrote WAVs to $out"
