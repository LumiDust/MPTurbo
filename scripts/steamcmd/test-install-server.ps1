#Requires -Version 7.0
<# Offline contract tests. Uses only fake SteamCMD/game scripts, never Steam services. #>
[CmdletBinding()]
param([string]$Bash = 'bash')
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$pwsh = (Get-Process -Id $PID).Path
$root = Join-Path $PSScriptRoot ('.installer-test-' + [guid]::NewGuid().ToString('N'))
$checks = 0
$environmentNames = @('PROFILE','APP_ID','BRANCH','INSTALL_DIR','STEAMCMD','STEAM_USER','STEAM_PASS','STEAM_GUARD','PROXY','MOD_SOURCE','MODS_CONFIG','LAUNCH_ARGS','FAKE_TRACE','FAKE_LAUNCH_TRACE','FAKE_RESULT','FAKE_EXIT','FAKE_LAYOUT','FAKE_OMIT','FAKE_VERSION')
$previousEnvironment = @{}
foreach ($name in $environmentNames) {
    $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    [Environment]::SetEnvironmentVariable($name, $null, 'Process')
}
function Assert([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "FAIL: $Message" }
    $script:checks++
    Write-Host "PASS: $Message"
}
function Unix-Path([string]$Path) {
    $normalized = $Path.Replace('\','/')
    if ($normalized -match '^([a-zA-Z]):/(.*)$') { return '/' + $Matches[1].ToLower() + '/' + $Matches[2] }
    return $normalized
}
function Run-PS([hashtable]$Parameters) {
    $casePath = Join-Path $root 'case.json'
    @{ script = (Join-Path $PSScriptRoot 'install-server.ps1'); parameters = $Parameters } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $casePath
    $text = & $pwsh -NoProfile -File (Join-Path $root 'run-case.ps1') $casePath 2>&1 | Out-String
    return @{ code = $LASTEXITCODE; text = $text }
}
function Run-Sh([string[]]$Arguments) {
    $text = & $Bash (Unix-Path (Join-Path $PSScriptRoot 'install-server.sh')) @Arguments 2>&1 | Out-String
    return @{ code = $LASTEXITCODE; text = $text }
}
try {
    New-Item -ItemType Directory -Path $root | Out-Null
    $fakeDir = Join-Path $root 'fake SteamCMD'
    New-Item -ItemType Directory -Path $fakeDir | Out-Null
    @'
param([string]$CaseFile)
$case = Get-Content -LiteralPath $CaseFile -Raw | ConvertFrom-Json -AsHashtable
$parameters = $case.parameters
& $case.script @parameters
exit $LASTEXITCODE
'@ | Set-Content -LiteralPath (Join-Path $root 'run-case.ps1')
    @'
$ErrorActionPreference = 'Stop'
ConvertTo-Json -InputObject @($args) -Compress | Set-Content -LiteralPath $env:FAKE_TRACE
if ($env:FAKE_EXIT -and $env:FAKE_EXIT -ne '0') { Write-Output 'fake native failure'; exit ([int]$env:FAKE_EXIT) }
if ($env:FAKE_RESULT -eq 'authfail') { Write-Output 'ERROR! Failed to log in (Invalid Password)'; exit 0 }
if ($env:FAKE_RESULT -eq 'nomarker') { Write-Output 'fake unconfirmed result'; exit 0 }
Write-Output 'Waiting for user info...OK'
$i = [array]::IndexOf($args, '+force_install_dir')
if ($i -ge 0) {
    $dir = $args[$i + 1]
    $app = $args[([array]::IndexOf($args, '+app_update') + 1)]
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    if ($env:FAKE_LAYOUT -eq 'official') {
        New-Item -ItemType Directory -Force -Path (Join-Path $dir 'bin') | Out-Null
        'payload' | Set-Content -LiteralPath (Join-Path $dir 'server.data')
        'exit 23' | Set-Content -LiteralPath (Join-Path $dir 'bin/fake server.ps1')
    }
    else {
        New-Item -ItemType Directory -Force -Path (Join-Path $dir 'Data/Core') | Out-Null
        foreach ($name in @('Assembly-CSharp.dll','RimWorld.dll','Waylith.dll','UnityStubs.dll','Assets.zip')) {
            if ($name -ne $env:FAKE_OMIT) { 'fake' | Set-Content -LiteralPath (Join-Path $dir $name) }
        }
        $(if ($env:FAKE_VERSION) { $env:FAKE_VERSION } else { '1.7.4950 rev123' }) | Set-Content -LiteralPath (Join-Path $dir 'Version.txt')
        if ($env:FAKE_OMIT -ne 'executable') { @('ConvertTo-Json -InputObject @($args) -Compress | Set-Content -LiteralPath $env:FAKE_LAUNCH_TRACE', 'exit 23') | Set-Content -LiteralPath (Join-Path $dir 'fake game.ps1') }
    }
    Write-Output "Success! App '$app' fully installed."
}
exit 0
'@ | Set-Content -LiteralPath (Join-Path $fakeDir 'steamcmd.ps1')
    # A double-quoted outer here-string is avoided: fixture source must stay literal.
    $fakeShell = @'
#!/usr/bin/env bash
set -eu
printf '%s\n' "$@" > "$FAKE_TRACE"
if [[ ${FAKE_EXIT:-0} != 0 ]]; then echo 'fake native failure'; exit "$FAKE_EXIT"; fi
if [[ ${FAKE_RESULT:-} == authfail ]]; then echo 'ERROR! Failed to log in (Invalid Password)'; exit 0; fi
if [[ ${FAKE_RESULT:-} == nomarker ]]; then echo 'fake unconfirmed result'; exit 0; fi
echo 'Waiting for user info...OK'
install=''; app=''
while [[ $# -gt 0 ]]; do
  case "$1" in +force_install_dir) install="$2"; shift 2 ;; +app_update) app="$2"; shift 2 ;; *) shift ;; esac
done
if [[ -n "$install" ]]; then
  mkdir -p "$install"
  if [[ ${FAKE_LAYOUT:-} == official ]]; then
    mkdir -p "$install/bin"
    printf 'payload\n' > "$install/server.data"
    printf '#!/usr/bin/env bash\nexit 23\n' > "$install/bin/fake server"
    chmod +x "$install/bin/fake server"
  else
    mkdir -p "$install/Data/Core"
    for f in Assembly-CSharp.dll RimWorld.dll Waylith.dll UnityStubs.dll Assets.zip; do
      [[ "$f" == "${FAKE_OMIT:-}" ]] || printf 'fake\n' > "$install/$f"
    done
    printf '%s\n' "${FAKE_VERSION:-1.7.4950 rev123}" > "$install/Version.txt"
    if [[ ${FAKE_OMIT:-} != executable ]]; then
      printf '#!/usr/bin/env bash\nprintf "%%s\\n" "$@" > "$FAKE_LAUNCH_TRACE"\nexit 23\n' > "$install/RimWorld"
      chmod +x "$install/RimWorld"
    fi
  fi
  echo "Success! App '$app' fully installed."
fi
'@
    [IO.File]::WriteAllText((Join-Path $fakeDir 'steamcmd.sh'), ($fakeShell.Replace("`r`n", "`n") + "`n"))
    $env:FAKE_TRACE = (Join-Path $root 'steam trace.txt').Replace('\','/')
    $env:FAKE_LAUNCH_TRACE = (Join-Path $root 'launch trace.txt').Replace('\','/')
    $mod = Join-Path $root 'Packaged TurboTick'
    New-Item -ItemType Directory -Path (Join-Path $mod 'About') | Out-Null
    '<ModMetaData><packageId>test.turbotick</packageId></ModMetaData>' | Set-Content -LiteralPath (Join-Path $mod 'About/About.xml')
    $config = Join-Path $root 'initial config.xml'
    '<initial />' | Set-Content -LiteralPath $config
    $tokens = $null; $errors = $null
    [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'install-server.ps1'), [ref]$tokens, [ref]$errors) | Out-Null
    Assert ($errors.Count -eq 0) 'PowerShell installer syntax'
    & $Bash -n (Unix-Path (Join-Path $PSScriptRoot 'install-server.sh'))
    Assert ($LASTEXITCODE -eq 0) 'Bash installer syntax'
    & $Bash -c 'chmod +x "$1"' _ (Unix-Path (Join-Path $fakeDir 'steamcmd.sh'))
    if ($LASTEXITCODE) { throw 'fake SteamCMD chmod failed' }

    foreach ($platform in @('ps', 'sh')) {
        $dir = Join-Path $root "$platform install space [literal] 'quote'"
        $steam = Join-Path $fakeDir "steamcmd.$(if ($platform -eq 'ps') { 'ps1' } else { 'sh' })"
        $base = @{ User = 'fixture_account'; SteamCmd = $steam; Dir = $dir; Executable = 'fake game.ps1' }
        $baseSh = @('--user','fixture_account','--steamcmd',(Unix-Path $steam),'--dir',(Unix-Path $dir))
        function Invoke-Fixture([hashtable]$More = @{}, [string[]]$MoreSh = @()) {
            if ($platform -eq 'ps') {
                $parameters = $base.Clone()
                foreach ($key in $More.Keys) { $parameters[$key] = $More[$key] }
                return Run-PS $parameters
            }
            return Run-Sh ($baseSh + $MoreSh)
        }
        Remove-Item -LiteralPath $env:FAKE_TRACE -ErrorAction SilentlyContinue
        $env:STEAM_PASS = 'password-DO-NOT-PRINT-493'
        $env:STEAM_GUARD = 'guard-DO-NOT-PRINT-771'
        $r = Invoke-Fixture @{ DryRun = $true } @('--dry-run')
        Assert ($r.code -eq 0 -and $r.text -notmatch 'password-DO-NOT-PRINT|guard-DO-NOT-PRINT') "$platform dry-run redacts secrets"
        Assert (-not (Test-Path -LiteralPath $env:FAKE_TRACE) -and -not (Test-Path -LiteralPath $dir)) "$platform dry-run invokes nothing and writes nothing"
        $r = Invoke-Fixture @{ DryRun = $true; LoginOnly = $true } @('--dry-run','--login-only')
        Assert ($r.code -eq 0 -and $r.text -notmatch 'password-DO-NOT-PRINT|guard-DO-NOT-PRINT') "$platform login-only dry-run redacts secrets"
        $env:FAKE_EXIT = '7'
        $r = Invoke-Fixture @{ LoginOnly = $true } @('--login-only')
        Assert ($r.code -eq 7 -and $r.text -notmatch 'authentication confirmed') "$platform login-only preserves failure exit 7"
        $r = Invoke-Fixture
        Assert ($r.code -eq 7 -and -not (Test-Path -LiteralPath $dir)) "$platform install preserves failure exit 7 without preparation"
        $env:FAKE_EXIT = '0'
        $env:FAKE_RESULT = 'authfail'
        $r = Invoke-Fixture @{ LoginOnly = $true } @('--login-only')
        Assert ($r.code -ne 0 -and $r.text -notmatch 'authentication confirmed') "$platform detects exit-zero authentication failure"
        $env:FAKE_RESULT = 'nomarker'
        $r = Invoke-Fixture @{ LoginOnly = $true } @('--login-only')
        Assert ($r.code -ne 0) "$platform rejects unconfirmed login"
        $r = Invoke-Fixture
        Assert ($r.code -ne 0) "$platform rejects missing app success marker"
        $env:FAKE_RESULT = ''
        $r = Invoke-Fixture @{ LoginOnly = $true } @('--login-only')
        Assert ($r.code -eq 0 -and $r.text -match 'authentication confirmed') "$platform confirms login only with positive output"
        $trace = Get-Content -LiteralPath $env:FAKE_TRACE -Raw
        Assert ($trace.IndexOf('+set_steam_guard_code') -lt $trace.IndexOf('+login')) "$platform Steam Guard command precedes login"
        $env:STEAM_PASS = ''; $env:STEAM_GUARD = ''
        $env:FAKE_OMIT = 'Assets.zip'
        $r = Invoke-Fixture
        Assert ($r.code -ne 0 -and -not (Test-Path -LiteralPath (Join-Path $dir "start-server.$(if ($platform -eq 'ps') {'ps1'} else {'sh'})"))) "$platform missing file blocks launcher creation"
        $gameName = if ($platform -eq 'ps') { 'fake game.ps1' } else { 'RimWorld' }
        Remove-Item -LiteralPath (Join-Path $dir $gameName)
        $env:FAKE_OMIT = 'executable'
        $r = Invoke-Fixture
        Assert ($r.code -ne 0 -and $r.text -match 'executable') "$platform missing executable fails before preparation"
        $env:FAKE_OMIT = ''
        $env:FAKE_VERSION = '1.6.9999'
        $r = Invoke-Fixture
        Assert ($r.code -ne 0) "$platform rejects wrong client version"
        $env:FAKE_VERSION = ''
        New-Item -ItemType Directory -Force -Path (Join-Path $dir 'Saved/Config') | Out-Null
        'preserved config' | Set-Content -LiteralPath (Join-Path $dir 'Saved/Config/ModsConfig.xml')
        'preserved world' | Set-Content -LiteralPath (Join-Path $dir 'Saved/world.rws')
        $injection = 'literal; $(touch SHOULD_NOT_EXIST) '' " $HOME'
        $r = Invoke-Fixture @{ ModSource = $mod; ModsConfig = $config; LaunchArgs = @('-headless', $injection) } @('--mod-source',(Unix-Path $mod),'--mods-config',(Unix-Path $config),'--launch-arg','-headless','--launch-arg',$injection)
        if ($r.code -ne 0) { throw $r.text }
        Assert ($r.code -eq 0) "$platform install succeeds with spaces and quotes"
        Assert ((Test-Path -LiteralPath (Join-Path $dir 'Mods/Packaged TurboTick/About/About.xml')) -and -not (Test-Path -LiteralPath (Join-Path $dir 'Mods/About'))) "$platform mod remains wrapped below Mods/name"
        Assert (((Get-Content -LiteralPath (Join-Path $dir 'Saved/Config/ModsConfig.xml') -Raw).Trim() -eq 'preserved config') -and ((Get-Content -LiteralPath (Join-Path $dir 'Saved/world.rws') -Raw).Trim() -eq 'preserved world')) "$platform preserves existing Saved and config"
        $before = (Get-Item -LiteralPath $env:FAKE_TRACE).LastWriteTimeUtc
        $r = Invoke-Fixture @{ CheckOnly = $true; User = ''; SteamCmd = 'not-installed' } @('--check-only','--steamcmd','not-installed')
        Assert ($r.code -eq 0 -and (Get-Item -LiteralPath $env:FAKE_TRACE).LastWriteTimeUtc -eq $before) "$platform check-only requires no SteamCMD and invokes nothing"
        $r = Invoke-Fixture @{ SkipDownload = $true; User = ''; SteamCmd = 'not-installed'; LaunchArgs = @('-headless', $injection) } @('--skip-download','--steamcmd','not-installed','--launch-arg','-headless','--launch-arg',$injection)
        Assert ($r.code -eq 0 -and (Get-Item -LiteralPath $env:FAKE_TRACE).LastWriteTimeUtc -eq $before) "$platform skip-download prepares locally without SteamCMD"
        if ($platform -eq 'ps') {
            & $pwsh -NoProfile -File (Join-Path $dir 'start-server.ps1') 'extra space argument'
            $launchExit = $LASTEXITCODE
            $launchArgs = @(Get-Content -LiteralPath $env:FAKE_LAUNCH_TRACE -Raw | ConvertFrom-Json)
        }
        else {
            & $Bash (Unix-Path (Join-Path $dir 'start-server.sh')) 'extra space argument'
            $launchExit = $LASTEXITCODE
            $launchArgs = @(Get-Content -LiteralPath $env:FAKE_LAUNCH_TRACE)
        }
        Assert ($launchExit -eq 23 -and $launchArgs.Count -eq 4 -and $launchArgs[1] -ceq $injection -and $launchArgs[2] -like '-savedatafolder=*' -and $launchArgs[-1] -eq 'extra space argument') "$platform generated launcher preserves literal argv and exit 23"
        Assert (-not (Test-Path -LiteralPath (Join-Path $dir 'SHOULD_NOT_EXIST'))) "$platform generated launcher does not execute argument text"
        $r = Invoke-Fixture @{ AppId = '123456' } @('--app-id','123456')
        Assert ($r.code -ne 0 -and $r.text -match 'official-server') "$platform changing client AppId requires explicit profile"
        $official = Join-Path $root "$platform future server"
        $env:FAKE_LAYOUT = 'official'
        $r = Invoke-Fixture @{ Profile = 'official-server'; AppId = '123456'; Dir = $official; Executable = 'bin/fake server.ps1'; RequiredPath = @('server.data'); NoLaunchArgs = $true } @('--profile','official-server','--app-id','123456','--dir',(Unix-Path $official),'--executable','bin/fake server','--required-path','server.data','--no-launch-args')
        Assert ($r.code -eq 0 -and -not (Test-Path -LiteralPath (Join-Path $official 'Saved')) -and -not (Test-Path -LiteralPath (Join-Path $official 'Version.txt'))) "$platform official profile uses explicit layout without client assumptions"
        $r = Invoke-Fixture @{ Profile = 'official-server'; AppId = '123456'; RequiredPath = @() } @('--profile','official-server','--app-id','123456')
        Assert ($r.code -ne 0) "$platform official profile fails on incomplete declaration"
        $r = Invoke-Fixture @{ Executable = '../escape' } @('--executable','../escape')
        Assert ($r.code -ne 0) "$platform executable traversal rejected"
        $r = Invoke-Fixture @{ NoLaunchArgs = $true; LaunchArgs = @('conflict') } @('--no-launch-args','--launch-arg','conflict')
        Assert ($r.code -ne 0) "$platform contradictory argument declarations fail"
        $env:LAUNCH_ARGS = 'retired command string'
        $r = Invoke-Fixture @{ SkipDownload = $true } @('--skip-download')
        Assert ($r.code -ne 0 -and $r.text -match 'retired') "$platform command-string environment fails explicitly"
        $env:LAUNCH_ARGS = ''
        $env:FAKE_LAYOUT = ''
        $r = Invoke-Fixture @{ Branch = 'public'; NoValidate = $true } @('--branch','public','--no-validate')
        if ($platform -eq 'ps') { $traceArgs = @(Get-Content -LiteralPath $env:FAKE_TRACE -Raw | ConvertFrom-Json) }
        else { $traceArgs = @(Get-Content -LiteralPath $env:FAKE_TRACE) }
        Assert ($r.code -eq 0 -and $traceArgs -notcontains '-beta' -and $traceArgs -notcontains 'validate') "$platform public branch omits beta and NoValidate omits validate"
        $fresh = Join-Path $root "$platform fresh install"
        $r = Invoke-Fixture @{ Dir = $fresh } @('--dir',(Unix-Path $fresh))
        $freshConfig = Join-Path $fresh 'Saved/Config/ModsConfig.xml'
        Assert ($r.code -eq 0 -and (Test-Path -LiteralPath $freshConfig) -and (Get-Content -LiteralPath $freshConfig -Raw) -match '<version>1.7.4950</version>') "$platform fresh config uses numeric version without revision label"
    }
    Write-Host "RESULT: $checks checks passed; all SteamCMD/game processes were local fixtures."
}
finally {
    foreach ($name in $environmentNames) { [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], 'Process') }
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}
