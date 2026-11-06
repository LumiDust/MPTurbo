#Requires -Version 7.0
param([string]$Dotnet = 'dotnet', [string]$RimWorldDir, [string]$Bash)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$version = ([xml](Get-Content (Join-Path $repo 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$declaredVersion = ([xml](Get-Content (Join-Path $repo 'About/About.xml') -Raw)).ModMetaData.modVersion
if ($version -ne $declaredVersion) { throw 'Directory.Build.props and About.xml versions differ.' }
function Run-Dotnet([string[]]$Arguments) {
    & $Dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed: $($Arguments -join ' ')" }
}
function Resolve-TestBash {
    if ($Bash) { return $Bash }
    $command = Get-Command bash -ErrorAction SilentlyContinue
    if ($command -and $command.Source -notmatch '(?i)[\\/]Windows[\\/]System32[\\/]bash\.exe$') { return $command.Source }
    $git = Get-Command git -ErrorAction SilentlyContinue
    if ($git) {
        $candidate = Join-Path (Split-Path $git.Source -Parent) '../bin/bash.exe'
        if (Test-Path -LiteralPath $candidate) { return [IO.Path]::GetFullPath($candidate) }
    }
    throw 'Bash is required for installer verification; install it or pass -Bash PATH.'
}
$out = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts'))
function Reset-OutputDirectory([string]$Path) {
    $resolved = [IO.Path]::GetFullPath($Path)
    if ($resolved -notin @((Join-Path $out 'mod/TurboTick'), (Join-Path $out 'server'), (Join-Path $out 'headless'))) {
        throw "Unexpected output directory: $resolved"
    }
    if (Test-Path -LiteralPath $resolved) {
        if ((Get-Item -LiteralPath $resolved -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Output directory must not be a filesystem link: $resolved"
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
    New-Item -Path $resolved -ItemType Directory -Force | Out-Null
}
function Assert-Package([string]$Path, [string[]]$AllowedAssemblies) {
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName.Replace('\', '/')
            if ($name -match '(^|/)(Source|Networking|Simulation|Tests|Verification|artifacts|secrets|\.git|docs)(/|$)' -or
                $name -match '(?i)(PublishedFileId\.txt|\.pdb|\.rws|\.log|\.snapshot|\.cs)$') {
                throw "Local/source-only material entered the runtime package: $name"
            }
            if ($name.EndsWith('.dll', [StringComparison]::OrdinalIgnoreCase) -and
                [IO.Path]::GetFileName($name) -notin $AllowedAssemblies) {
                throw "Unexpected third-party/game assembly entered the package: $name"
            }
        }
    }
    finally { $archive.Dispose() }
}
Push-Location $repo
try {
    $buildArguments = @('build', 'Source/TurboTick.csproj', '-c', 'Release')
    if ($RimWorldDir) { $buildArguments += "-p:RimWorldDir=$RimWorldDir" }
    Run-Dotnet -Arguments $buildArguments
    $mod = Join-Path $out 'mod/TurboTick'
    $server = Join-Path $out 'server'
    $headless = Join-Path $out 'headless'
    Reset-OutputDirectory $server
    Run-Dotnet -Arguments @('publish', 'Server/TurboTick.Server.csproj', '-c', 'Release', '--self-contained', 'false',
        '-p:UseAppHost=false', '-p:DebugType=None', '-p:DebugSymbols=false', '-o', $server)
    Run-Dotnet -Arguments @('run', '--project', 'Tests/TurboTick.Tests.csproj', '-c', 'Release')
    Run-Dotnet -Arguments @('run', '--project', 'Server/Verification/TurboTick.Networking.Verification.csproj', '-c', 'Release',
        '--', '--server-program', (Join-Path $server 'TurboTick.Server.dll'))
    & (Join-Path $PSScriptRoot 'steamcmd/test-install-server.ps1') -Bash (Resolve-TestBash)

    Reset-OutputDirectory $mod
    foreach ($directory in @('About', 'Languages')) {
        Get-ChildItem -LiteralPath (Join-Path $repo $directory) -File -Recurse |
            Where-Object { $_.Extension -in @('.xml', '.png', '.dds') } | ForEach-Object {
                $relative = [IO.Path]::GetRelativePath($repo, $_.FullName)
                $target = Join-Path $mod $relative
                New-Item -Path (Split-Path $target -Parent) -ItemType Directory -Force | Out-Null
                Copy-Item -LiteralPath $_.FullName -Destination $target
            }
    }
    $assemblyDirectory = Join-Path $mod '1.7/Assemblies'
    New-Item $assemblyDirectory -ItemType Directory -Force | Out-Null
    $modAssemblies = @('TurboTick.dll', 'TurboTick.Networking.dll', 'TurboTick.Simulation.dll')
    foreach ($assembly in $modAssemblies) {
        Copy-Item -LiteralPath (Join-Path $repo "1.7/Assemblies/$assembly") -Destination $assemblyDirectory
    }
    foreach ($file in @('LoadFolders.xml', 'README.md', 'LICENSE')) {
        Copy-Item -LiteralPath (Join-Path $repo $file) -Destination $mod
    }
    foreach ($file in @('README.md', 'LICENSE')) { Copy-Item -LiteralPath (Join-Path $repo $file) -Destination $server }
    $modZip = Join-Path $out "TurboTick-$version-mod-1.7.zip"
    $serverZip = Join-Path $out "TurboTick-$version-server.zip"
    Compress-Archive -Path $mod -DestinationPath $modZip -Force
    Compress-Archive -Path (Join-Path $server '*') -DestinationPath $serverZip -Force

    Reset-OutputDirectory $headless
    Copy-Item -LiteralPath $mod -Destination (Join-Path $headless 'TurboTick') -Recurse
    foreach ($directory in @('scripts/steamcmd', 'deploy')) {
        New-Item (Join-Path $headless $directory) -ItemType Directory -Force | Out-Null
    }
    foreach ($file in @('scripts/steamcmd/install-server.sh', 'scripts/steamcmd/install-server.ps1',
        'deploy/README.md', 'deploy/turbotick-headless.service', 'README.md', 'LICENSE')) {
        Copy-Item -LiteralPath (Join-Path $repo $file) -Destination (Join-Path $headless $file)
    }
    $headlessZip = Join-Path $out "TurboTick-$version-headless-installer.zip"
    Compress-Archive -Path (Join-Path $headless '*') -DestinationPath $headlessZip -Force
    Assert-Package $modZip $modAssemblies
    Assert-Package $serverZip @('TurboTick.Server.dll', 'TurboTick.Networking.dll')
    Assert-Package $headlessZip $modAssemblies
    $packages = @($modZip, $serverZip, $headlessZip)
    $hashes = Get-FileHash -LiteralPath $packages -Algorithm SHA256
    $lines = $hashes | ForEach-Object { "$($_.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($_.Path))" }
    [IO.File]::WriteAllText((Join-Path $out 'SHA256SUMS'), ($lines -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
    $hashes | Select-Object Hash, Path
}
finally { Pop-Location }
