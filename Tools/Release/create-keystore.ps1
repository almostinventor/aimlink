# Creates the release signing key for AIMLINK (run once, then keep the file safe).
#
#   powershell -ExecutionPolicy Bypass -File Tools\Release\create-keystore.ps1
#
# The keystore is written OUTSIDE the repository (default: %USERPROFILE%\.aimlink\aimlink-release.keystore).
# Back it up and never commit it. If it is lost, future builds can't update an installed copy: users
# would have to uninstall first.
param(
    [string]$Keystore = (Join-Path $env:USERPROFILE '.aimlink\aimlink-release.keystore'),
    [string]$Alias = 'aimlink',
    [string]$UnityPath = ''
)
$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')

if (Test-Path $Keystore) { throw "A keystore already exists at $Keystore. Refusing to overwrite it." }

# keytool ships with the OpenJDK bundled in Unity's Android module
if (-not $UnityPath) {
    $version = (Select-String -Path (Join-Path $repo 'ProjectSettings\ProjectVersion.txt') -Pattern '^m_EditorVersion: (.+)$').Matches[0].Groups[1].Value.Trim()
    $UnityPath = "C:\Program Files\Unity\Hub\Editor\$version\Editor\Unity.exe"
}
$keytool = Join-Path (Split-Path $UnityPath) 'Data\PlaybackEngines\AndroidPlayer\OpenJDK\bin\keytool.exe'
if (-not (Test-Path $keytool)) { throw "keytool not found at $keytool. Pass -UnityPath to your Unity.exe (with Android Build Support installed)." }

$pass1 = Read-Host 'Keystore password (min 6 characters)' -AsSecureString
$pass2 = Read-Host 'Repeat password' -AsSecureString
$plain1 = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($pass1))
$plain2 = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($pass2))
if ($plain1 -ne $plain2) { throw 'Passwords do not match.' }
if ($plain1.Length -lt 6) { throw 'Password must be at least 6 characters.' }
# Unity writes the password into a Gradle file between single quotes, and keytool and Gradle can disagree
# on non-ASCII characters, so only allow characters that survive both unchanged.
if ($plain1 -notmatch '^[A-Za-z0-9!#%&*+,\-./:;<=>?@^_~]+$') {
    throw 'Use only English letters, digits and these symbols: ! # % & * + , - . / : ; < = > ? @ ^ _ ~  (no spaces, quotes, backslashes, $ or letters like ä/ö).'
}

New-Item -ItemType Directory -Force (Split-Path $Keystore) | Out-Null
# PKCS12 keystores use one password for both the store and the key. It is handed to keytool through an
# environment variable (":env"), not the command line, so it never shows up in the process list.
$env:AIMLINK_NEW_KEYSTORE_PASS = $plain1
try {
    & $keytool -genkeypair -v -keystore $Keystore -storetype PKCS12 -alias $Alias -keyalg RSA -keysize 2048 -validity 10000 `
        -dname 'CN=AIMLINK' -storepass:env AIMLINK_NEW_KEYSTORE_PASS -keypass:env AIMLINK_NEW_KEYSTORE_PASS
    if ($LASTEXITCODE -ne 0) { throw "keytool failed with exit code $LASTEXITCODE" }
}
finally {
    Remove-Item Env:AIMLINK_NEW_KEYSTORE_PASS -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "Keystore created: $Keystore (alias '$Alias')."
Write-Host "Back this file up somewhere safe. Build releases with Tools\Release\build-release.ps1."
