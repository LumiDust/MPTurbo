#Requires -Version 7.0
<#
.SYNOPSIS
Prepare a separate Steam application installation, with no automatic server start.
.DESCRIPTION
Client-headless validates the known RimWorld 1.7 layout; official-server requires
an explicit AppId, executable, required paths and literal argument array.
SteamCMD owns authentication/cache and native phone, email or code interaction.
Use -Preflight/-DryRun for a redacted offline plan; -CheckOnly verifies local files.
.EXAMPLE
./install-server.ps1 -User ACCOUNT -SteamCmd 'C:\SteamCMD\steamcmd.exe' -Preflight
.EXAMPLE
./install-server.ps1 -Profile official-server -AppId 123456 -Executable 'bin/server.exe' -RequiredPath 'server.data' -NoLaunchArgs -CheckOnly -Dir 'D:\Servers\Future Server'
#>
[CmdletBinding()]
param(
    [ValidateSet('client-headless', 'official-server')]
    [string]$Profile = $(if ($env:PROFILE) { $env:PROFILE } else { 'client-headless' }),
    [string]$AppId = $env:APP_ID,
    [string]$Branch = $env:BRANCH,
    [string]$Dir = $(if ($env:INSTALL_DIR) { $env:INSTALL_DIR } else { Join-Path $PSScriptRoot 'server' }),
    [string]$SteamCmd = $(if ($env:STEAMCMD) { $env:STEAMCMD } else { 'steamcmd.exe' }),
    [string]$User = $env:STEAM_USER,
    [string]$Proxy = $env:PROXY,
    [string]$Executable,
    [string[]]$RequiredPath = @(),
    [string[]]$LaunchArgs = @(),
    [switch]$NoLaunchArgs,
    [string]$VersionFile,
    [string]$ExpectedVersion,
    [string]$ModSource = $env:MOD_SOURCE,
    [string]$ModName,
    [string]$ModsConfig = $env:MODS_CONFIG,
    [switch]$NoValidate,
    [switch]$LoginOnly,
    [switch]$DryRun,
    [switch]$Preflight,
    [switch]$CheckOnly,
    [switch]$SkipDownload
)
$ErrorActionPreference = 'Stop'
# Native nonzero exits are checked explicitly, independent of the caller's setting.
$PSNativeCommandUseErrorActionPreference = $false
function Write-Step([string]$Message) { Write-Host "[install] $Message" }
function Assert-RelativePath([string]$Value) {
    if (-not $Value -or [IO.Path]::IsPathRooted($Value) -or $Value -match '[:\r\n]' -or
        ($Value -split '[/\\]' | Where-Object { $_ -in '.', '..' })) {
        throw "expected a relative path under installation: $Value"
    }
}
function Full-Path([string]$Value) { $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Value) }

try {
    if ($env:LAUNCH_ARGS) { throw 'LAUNCH_ARGS environment is retired; use -LaunchArgs as a string array' }
    if ($LoginOnly -and ($CheckOnly -or $SkipDownload)) { throw '-LoginOnly conflicts with local-file modes' }
    if ($CheckOnly -and $SkipDownload) { throw 'choose -CheckOnly or -SkipDownload' }
    $argsExplicit = $PSBoundParameters.ContainsKey('LaunchArgs') -or $NoLaunchArgs
    if ($NoLaunchArgs -and $LaunchArgs.Count) { throw 'choose -LaunchArgs or -NoLaunchArgs' }
    if ($Profile -eq 'client-headless') {
        if (-not $AppId) { $AppId = '294100' }
        if ($AppId -ne '294100') { throw 'a different AppId needs -Profile official-server with explicit layout/argv' }
        if (-not $PSBoundParameters.ContainsKey('Branch') -and -not $env:BRANCH) { $Branch = 'unstable' }
        $RequiredPath = @('Version.txt', 'Assembly-CSharp.dll', 'RimWorld.dll', 'Waylith.dll', 'UnityStubs.dll', 'Assets.zip', 'Data/Core') + $RequiredPath
        if (-not $VersionFile) { $VersionFile = 'Version.txt' }
        if (-not $ExpectedVersion) { $ExpectedVersion = '1.7' }
        if (-not $argsExplicit) { $LaunchArgs = @('-headless') }
    }
    else {
        if (-not $LoginOnly -and (-not $AppId -or -not $Executable -or -not $RequiredPath.Count -or -not $argsExplicit)) {
            throw 'official-server needs -AppId, -Executable, -RequiredPath and -LaunchArgs (or -NoLaunchArgs)'
        }
        if ($ModSource -or $ModName -or $ModsConfig) { throw 'official-server does not assume a RimWorld Mods/Saved layout; configure application paths explicitly in its argv' }
    }
    if (-not $LoginOnly -and $AppId -notmatch '^[1-9][0-9]*$') { throw 'AppId must be a positive integer' }
    if ($Branch -and $Branch -notmatch '^[a-zA-Z0-9_][a-zA-Z0-9_./-]*$') { throw 'invalid branch name' }
    foreach ($item in $RequiredPath) { Assert-RelativePath $item }
    if ($Executable) { Assert-RelativePath $Executable }
    if ($VersionFile) { Assert-RelativePath $VersionFile }
    if ($ExpectedVersion -and -not $VersionFile) { throw '-ExpectedVersion requires -VersionFile' }
    if ($Dir -match '[\r\n]') { throw 'installation path contains a newline' }
    $Dir = Full-Path $Dir
    if ($ModSource) {
        $ModSource = (Resolve-Path -LiteralPath $ModSource).Path.TrimEnd([IO.Path]::DirectorySeparatorChar)
        if (-not (Test-Path -LiteralPath (Join-Path $ModSource 'About/About.xml') -PathType Leaf)) { throw 'mod source must contain About/About.xml' }
        if (-not $ModName) { $ModName = Split-Path -Leaf $ModSource }
        if ($ModName -notmatch '^[a-zA-Z0-9_][a-zA-Z0-9_. -]*$' -or $ModName -in '.', '..') { throw 'invalid mod directory name' }
        $sourcePrefix = $ModSource + [IO.Path]::DirectorySeparatorChar
        if (($Dir + [IO.Path]::DirectorySeparatorChar).StartsWith($sourcePrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'installation must be outside mod source to avoid recursive copying'
        }
    }
    if ($ModsConfig) {
        $ModsConfig = (Resolve-Path -LiteralPath $ModsConfig).Path
        if (-not (Test-Path -LiteralPath $ModsConfig -PathType Leaf)) { throw 'mods config file not found' }
    }

    $commandArgs = @()
    $displayArgs = @()
    if (-not $CheckOnly -and -not $SkipDownload) {
        if ($User -notmatch '^[a-zA-Z0-9_@.-]+$') { throw 'supply a Steam account using -User / STEAM_USER' }
        $steamCommand = Get-Command $SteamCmd -CommandType Application, ExternalScript -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $steamCommand) { throw 'SteamCMD executable not found; pass -SteamCmd PATH (download SteamCMD separately)' }
        $SteamCmd = $steamCommand.Source
        if (-not $LoginOnly) { $commandArgs += @('+force_install_dir', $Dir); $displayArgs += @('+force_install_dir', $Dir) }
        # A one-time code must be set before login. Leave unset for native Guard prompts.
        if ($env:STEAM_GUARD) { $commandArgs += @('+set_steam_guard_code', $env:STEAM_GUARD); $displayArgs += @('+set_steam_guard_code', '<redacted>') }
        $commandArgs += @('+login', $User)
        $displayArgs += @('+login', $User)
        if ($env:STEAM_PASS) { $commandArgs += $env:STEAM_PASS; $displayArgs += '<redacted>' }
        if (-not $LoginOnly) {
            $commandArgs += @('+app_update', $AppId)
            $displayArgs += @('+app_update', $AppId)
            if ($Branch -and $Branch -ne 'public') { $commandArgs += @('-beta', $Branch); $displayArgs += @('-beta', $Branch) }
            if (-not $NoValidate) { $commandArgs += 'validate'; $displayArgs += 'validate' }
        }
        $commandArgs += '+quit'
        $displayArgs += '+quit'
    }
    if ($DryRun -or $Preflight) {
        Write-Step "preflight only: profile=$Profile app=$AppId branch=$Branch directory=$Dir"
        if ($displayArgs.Count) {
            $quoted = @($SteamCmd) + $displayArgs | ForEach-Object { ConvertTo-Json -InputObject $_ -Compress }
            Write-Step ('command: ' + ($quoted -join ' '))
        }
        if (-not $LoginOnly) { Write-Step ('required: ' + ($RequiredPath -join ', ')); Write-Step "executable: $(if ($Executable) { $Executable } else { 'detect client executable after install' })" }
        Write-Step 'no authentication, download or filesystem changes performed'
        return
    }

    if (-not $CheckOnly -and -not $SkipDownload) {
        $proxyNames = @('HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY')
        $previousProxy = @{}
        foreach ($name in $proxyNames) { $previousProxy[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
        $transcript = [IO.Path]::GetTempFileName()
        try {
            if ($Proxy) {
                foreach ($name in $proxyNames) { [Environment]::SetEnvironmentVariable($name, $Proxy, 'Process') }
                Write-Step 'proxy environment configured for SteamCMD'
            }
            Write-Step 'SteamCMD uses its own cache and native password / phone / email / code interaction'
            Push-Location -LiteralPath (Split-Path -Parent $SteamCmd)
            try {
                # Avoid shadowing exit status produced by PowerShell wrappers.
                $global:LASTEXITCODE = 0
                # Inherit native stdin. Capture stdout for outcome checks, not prompt automation.
                & $SteamCmd @commandArgs 2>&1 | Tee-Object -FilePath $transcript | ForEach-Object { Write-Host "$_" }
                $steamExit = $LASTEXITCODE
            }
            finally { Pop-Location }
            if ($steamExit -ne 0) { Write-Step "SteamCMD exited with $steamExit"; exit $steamExit }
            $output = Get-Content -LiteralPath $transcript -Raw
            if ($output -match 'ERROR!|FAILED \(|Login Failure|Invalid Password|Account Logon Denied') { throw 'SteamCMD reported an authentication/update error' }
            if ($LoginOnly) {
                if ($output -notmatch 'Logged in OK|Logging in user .*\.\.\.OK|Waiting for user info\.\.\.OK') {
                    throw 'SteamCMD exited without a recognized login-success marker; cache status unconfirmed'
                }
                Write-Step 'authentication confirmed; SteamCMD controls cache location (reuse this executable, OS account and home)'
                return
            }
            if ($output -notmatch "Success! App '$AppId' (fully installed|already up to date)") { throw 'SteamCMD exited without the requested AppId success marker' }
        }
        finally {
            Remove-Item -LiteralPath $transcript -Force -ErrorAction SilentlyContinue
            foreach ($name in $proxyNames) { [Environment]::SetEnvironmentVariable($name, $previousProxy[$name], 'Process') }
        }
    }

    Write-Step "verifying $Profile layout in $Dir"
    foreach ($item in $RequiredPath) {
        if (-not (Test-Path -LiteralPath (Join-Path $Dir $item))) { throw "missing required path: $item" }
    }
    if ($Profile -eq 'client-headless') {
        foreach ($file in @('Version.txt','Assembly-CSharp.dll','RimWorld.dll','Waylith.dll','UnityStubs.dll','Assets.zip')) {
            if (-not (Test-Path -LiteralPath (Join-Path $Dir $file) -PathType Leaf)) { throw "required file is not a regular file: $file" }
        }
        if (-not (Test-Path -LiteralPath (Join-Path $Dir 'Data/Core') -PathType Container)) { throw 'Data/Core must be a directory' }
    }
    if (-not $Executable) {
        foreach ($candidate in @('RimWorld.exe', 'RimWorldWin64.exe', 'RimWorld')) {
            if (Test-Path -LiteralPath (Join-Path $Dir $candidate) -PathType Leaf) { $Executable = $candidate; break }
        }
    }
    if (-not $Executable -or -not (Test-Path -LiteralPath (Join-Path $Dir $Executable) -PathType Leaf)) { throw 'required executable missing' }
    $version = 'unspecified'
    if ($VersionFile) {
        $versionPath = Join-Path $Dir $VersionFile
        if (-not (Test-Path -LiteralPath $versionPath -PathType Leaf)) { throw "version file missing: $VersionFile" }
        $version = Get-Content -LiteralPath $versionPath -TotalCount 1
        if (-not $version) { throw 'version file is empty' }
        $version = $version.Trim()
        if (-not $version) { throw 'version file is empty' }
        if ($ExpectedVersion -and $version -notmatch ('^' + [regex]::Escape($ExpectedVersion) + '(\.|\s|$)')) { throw "version '$version' does not match $ExpectedVersion" }
    }
    Write-Step "verified: app=$AppId branch=$Branch version=$version executable=$Executable directory=$Dir"
    if ($CheckOnly) { Write-Step 'check-only complete; no files changed'; return }

    if ($ModSource) {
        $destination = Join-Path (Join-Path $Dir 'Mods') $ModName
        New-Item -ItemType Directory -Force -Path $destination | Out-Null
        # Copy the mod's contents into its own wrapper directory, including hidden entries.
        Get-ChildItem -LiteralPath $ModSource -Force | Copy-Item -Destination $destination -Recurse -Force
        if (-not (Test-Path -LiteralPath (Join-Path $destination 'About/About.xml') -PathType Leaf)) { throw 'copied mod is missing About/About.xml' }
        Write-Step "mod installed under Mods/$ModName"
    }
    if ($Profile -eq 'client-headless') {
        $configDir = Join-Path $Dir 'Saved/Config'
        New-Item -ItemType Directory -Force -Path $configDir | Out-Null
        $configPath = Join-Path $configDir 'ModsConfig.xml'
        if (Test-Path -LiteralPath $configPath) {
            Write-Step 'preserving existing Saved/Config/ModsConfig.xml (including when -ModsConfig is supplied)'
        }
        elseif ($ModsConfig) { Copy-Item -LiteralPath $ModsConfig -Destination $configPath }
        else {
            $xmlVersion = ($version -split '\s+')[0]
            if ($xmlVersion -notmatch '^\d+(\.\d+)*$') { throw 'version token is not suitable for default ModsConfig.xml; provide -ModsConfig' }
            @"
<?xml version="1.0" encoding="utf-8"?>
<ModsConfigData>
  <version>$xmlVersion</version>
  <activeMods><li>ludeon.rimworld</li></activeMods>
  <knownExpansions />
</ModsConfigData>
"@ | Set-Content -LiteralPath $configPath -Encoding utf8NoBOM
            Write-Step 'created initial Core-only ModsConfig.xml; enable Harmony/TurboTick and matching DLC/mods separately'
        }
    }
    # Data is serialized, never interpolated into executable PowerShell source.
    $settings = [ordered]@{
        profile = $Profile; appId = $AppId; branch = $Branch; version = $version
        installDirectory = $Dir; executable = $Executable
        arguments = @($LaunchArgs); clientSavedData = ($Profile -eq 'client-headless')
    }
    $settings | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $Dir 'launch-settings.json') -Encoding utf8NoBOM
    $startPath = Join-Path $Dir 'start-server.ps1'
    @'
#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$settings = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'launch-settings.json') -Raw | ConvertFrom-Json
$executable = Join-Path $PSScriptRoot $settings.executable
$launchArguments = @($settings.arguments | ForEach-Object { [string]$_ })
if ($settings.clientSavedData) { $launchArguments += ('-savedatafolder=' + (Join-Path $PSScriptRoot 'Saved')) }
Push-Location -LiteralPath $PSScriptRoot
try {
    & $executable @launchArguments @args
    exit $LASTEXITCODE
}
finally { Pop-Location }
'@ | Set-Content -LiteralPath $startPath -Encoding utf8NoBOM
    Write-Step "prepared launcher: $startPath"
    Write-Step 'installation preparation complete; no server process started'
}
catch {
    Write-Error ("[install] " + $_.Exception.Message) -ErrorAction Continue
    exit 1
}
