# Builds a signed release APK of AIMLINK to Builds\AimLink.apk.
#
#   powershell -ExecutionPolicy Bypass -File Tools\Release\build-release.ps1 -VersionName 1.0.0 -VersionCode 1
#
# Every release needs a higher -VersionCode than the one before it.
# Passwords are asked for at the prompt (or read from AIMLINK_KEYSTORE_PASS / AIMLINK_KEY_PASS if set),
# handed to Unity through environment variables for this one build, and never written to disk.
# Close the Unity editor on this project first: batch mode can't open a project that is already open.
param(
    [Parameter(Mandatory = $true)][string]$VersionName,
    [Parameter(Mandatory = $true)][int]$VersionCode,
    [string]$Keystore = (Join-Path $env:USERPROFILE '.aimlink\aimlink-release.keystore'),
    [string]$Alias = 'aimlink',
    [string]$UnityPath = ''
)
$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')

if (-not (Test-Path $Keystore)) { throw "Keystore not found at $Keystore. Create one with Tools\Release\create-keystore.ps1." }
if (-not $UnityPath) {
    $version = (Select-String -Path (Join-Path $repo 'ProjectSettings\ProjectVersion.txt') -Pattern '^m_EditorVersion: (.+)$').Matches[0].Groups[1].Value.Trim()
    $UnityPath = "C:\Program Files\Unity\Hub\Editor\$version\Editor\Unity.exe"
}
if (-not (Test-Path $UnityPath)) { throw "Unity not found at $UnityPath. Pass -UnityPath." }

function Read-Secret([string]$prompt) {
    $secure = Read-Host $prompt -AsSecureString
    return [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure))
}
$storePass = if ($env:AIMLINK_KEYSTORE_PASS) { $env:AIMLINK_KEYSTORE_PASS } else { Read-Secret 'Keystore password' }
$keyPass = if ($env:AIMLINK_KEY_PASS) { $env:AIMLINK_KEY_PASS } else { $storePass }

# Check the password against the keystore before spending minutes on a build.
$keytool = Join-Path (Split-Path $UnityPath) 'Data\PlaybackEngines\AndroidPlayer\OpenJDK\bin\keytool.exe'
if (Test-Path $keytool) {
    $env:AIMLINK_CHECK_PASS = $storePass
    try {
        & $keytool -list -keystore $Keystore -storepass:env AIMLINK_CHECK_PASS -alias $Alias *> $null
        $ok = $LASTEXITCODE -eq 0
    }
    finally {
        Remove-Item Env:AIMLINK_CHECK_PASS -ErrorAction SilentlyContinue
    }
    if (-not $ok) { throw "The keystore rejected that password (or has no key called '$Alias'). Nothing was built." }
}
if ($storePass -notmatch '^[A-Za-z0-9!#%&*+,\-./:;<=>?@^_~]+$') {
    throw 'The password contains characters Unity''s Gradle build can''t pass through (spaces, quotes, backslashes, $ or non-English letters). Recreate the keystore with create-keystore.ps1 and a simpler password.'
}

$log = Join-Path $repo 'Builds\build-release.log'
New-Item -ItemType Directory -Force (Split-Path $log) | Out-Null

$env:AIMLINK_KEYSTORE = $Keystore
$env:AIMLINK_KEYSTORE_PASS = $storePass
$env:AIMLINK_KEY_ALIAS = $Alias
$env:AIMLINK_KEY_PASS = $keyPass
try {
    $p = Start-Process -FilePath $UnityPath -Wait -PassThru -ArgumentList @(
        '-batchmode', '-quit', '-projectPath', "`"$repo`"", '-buildTarget', 'Android',
        '-executeMethod', 'AimLink.EditorTools.AimLinkSetup.BuildApk',
        '-versionName', $VersionName, '-versionCode', $VersionCode,
        '-logFile', "`"$log`"")
}
finally {
    Remove-Item Env:AIMLINK_KEYSTORE, Env:AIMLINK_KEYSTORE_PASS, Env:AIMLINK_KEY_ALIAS, Env:AIMLINK_KEY_PASS -ErrorAction SilentlyContinue

    # Unity writes the signing passwords in plain text into its generated Gradle project under
    # Library\Bee (git-ignored, but still on disk). Overwrite every copy so none is left behind.
    $bee = Join-Path $repo 'Library\Bee'
    $secrets = @($storePass, $keyPass) | Where-Object { $_ } | Select-Object -Unique
    if (Test-Path $bee) {
        Get-ChildItem $bee -Recurse -File -Include *.gradle, *.xml, *.json, *.properties -ErrorAction SilentlyContinue | ForEach-Object {
            $text = [IO.File]::ReadAllText($_.FullName)
            $clean = $text
            foreach ($secret in $secrets) { $clean = $clean.Replace($secret, 'REDACTED') }
            if ($clean -ne $text) { [IO.File]::WriteAllText($_.FullName, $clean) }
        }
    }
}

Select-String -Path $log -Pattern '\[AimLink\]' | ForEach-Object { $_.Line }
if ($p.ExitCode -ne 0) { throw "Build failed (exit code $($p.ExitCode)). See $log" }
Write-Host "Signed APK: $(Join-Path $repo 'Builds\AimLink.apk')"
