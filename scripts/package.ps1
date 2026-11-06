param([string]$Dotnet = 'dotnet', [string]$RimWorldDir)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$version = ([xml](Get-Content (Join-Path $repo 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$declaredVersion = ([xml](Get-Content (Join-Path $repo 'About/About.xml') -Raw)).ModMetaData.modVersion
if ($version -ne $declaredVersion) { throw 'Directory.Build.props and About.xml versions differ.' }
function Run-Dotnet([string[]]$Arguments) {
    & $Dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed: $($Arguments -join ' ')" }
}
$out = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts'))
function Reset-OutputDirectory([string]$Path) {
    $resolved = [IO.Path]::GetFullPath($Path)
    if ($resolved -notin @((Join-Path $out 'mod/TurboTick'), (Join-Path $out 'server'))) {
        throw "Unexpected output directory: $resolved"
    }
    Write-Output "Rebuild output directory: $resolved"
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
    New-Item -Path $resolved -ItemType Directory -Force | Out-Null
}
Push-Location $repo
try {
    $buildArguments = @('build', 'Source/TurboTick.csproj', '-c', 'Release')
    if ($RimWorldDir) { $buildArguments += "-p:RimWorldDir=$RimWorldDir" }
    Run-Dotnet -Arguments $buildArguments
    Run-Dotnet -Arguments @('run', '--project', 'Tests/TurboTick.Tests.csproj', '-c', 'Release')
    Run-Dotnet -Arguments @('run', '--project', 'Server/Verification/TurboTick.Networking.Verification.csproj', '-c', 'Release')
    $mod = Join-Path $out 'mod/TurboTick'
    $server = Join-Path $out 'server'
    Reset-OutputDirectory $mod
    Reset-OutputDirectory $server
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
    foreach ($assembly in @('TurboTick.dll', 'TurboTick.Networking.dll', 'TurboTick.Simulation.dll')) {
        Copy-Item -LiteralPath (Join-Path $repo "1.7/Assemblies/$assembly") -Destination $assemblyDirectory
    }
    foreach ($file in @('LoadFolders.xml', 'README.md', 'LICENSE')) {
        Copy-Item -LiteralPath (Join-Path $repo $file) -Destination $mod
    }
    Run-Dotnet -Arguments @('publish', 'Server/TurboTick.Server.csproj', '-c', 'Release', '--self-contained', 'false',
        '-p:UseAppHost=false', '-p:DebugType=None', '-p:DebugSymbols=false', '-o', $server)
    foreach ($file in @('README.md', 'LICENSE')) { Copy-Item -LiteralPath (Join-Path $repo $file) -Destination $server }
    $modZip = Join-Path $out "TurboTick-$version-mod-1.7.zip"
    $serverZip = Join-Path $out "TurboTick-$version-server.zip"
    Compress-Archive -Path $mod -DestinationPath $modZip -Force
    Compress-Archive -Path (Join-Path $server '*') -DestinationPath $serverZip -Force
    Get-FileHash -LiteralPath $modZip, $serverZip -Algorithm SHA256 | Select-Object Hash, Path
}
finally { Pop-Location }
