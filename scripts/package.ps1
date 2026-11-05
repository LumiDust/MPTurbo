param([string]$Dotnet = 'dotnet')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$version = ([xml](Get-Content (Join-Path $repo 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$declaredVersion = ([xml](Get-Content (Join-Path $repo 'About/About.xml') -Raw)).ModMetaData.modVersion
if ($version -ne $declaredVersion) { throw 'Directory.Build.props and About.xml versions differ.' }
function Run-Dotnet([string[]]$Arguments) {
    & $Dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed: $($Arguments -join ' ')" }
}
Push-Location $repo
try {
    Run-Dotnet -Arguments @('build', 'Source/TurboTick.csproj', '-c', 'Release')
    Run-Dotnet -Arguments @('run', '--project', 'Tests/TurboTick.Tests.csproj', '-c', 'Release')
    Run-Dotnet -Arguments @('run', '--project', 'Server/Verification/TurboTick.Networking.Verification.csproj', '-c', 'Release')
    $out = Join-Path $repo 'artifacts'
    $mod = Join-Path $out 'mod/TurboTick'
    $server = Join-Path $out 'server'
    foreach ($path in @($mod, $server)) {
        if (Test-Path $path) { Remove-Item $path -Recurse -Force }
        New-Item -Path $path -ItemType Directory -Force | Out-Null
    }
    foreach ($directory in @('About', 'Languages', '1.7')) {
        Get-ChildItem -Path (Join-Path $repo $directory) -File -Recurse | Where-Object { $_.Extension -ne '.pdb' -and $_.Name -ne 'PublishedFileId.txt' } | ForEach-Object {
            $relative = [System.IO.Path]::GetRelativePath($repo, $_.FullName)
            $target = Join-Path $mod $relative
            New-Item -Path (Split-Path $target -Parent) -ItemType Directory -Force | Out-Null
            Copy-Item $_.FullName $target
        }
    }
    Copy-Item (Join-Path $repo 'LoadFolders.xml') $mod
    Copy-Item (Join-Path $repo 'README.md') $mod
    Copy-Item (Join-Path $repo 'CONTRIBUTING.md') $mod
    Copy-Item (Join-Path $repo 'CHANGELOG.md') $mod
    Copy-Item (Join-Path $repo 'LICENSE') $mod
    Copy-Item (Join-Path $repo 'docs') (Join-Path $mod 'docs') -Recurse
    Run-Dotnet -Arguments @('publish', 'Server/TurboTick.Server.csproj', '-c', 'Release', '--self-contained', 'false', '-p:UseAppHost=false', '-o', $server)
    Copy-Item (Join-Path $repo 'docs/SERVER.md') (Join-Path $server 'README.md')
    Copy-Item (Join-Path $repo 'LICENSE') $server
    Compress-Archive -Path $mod -DestinationPath (Join-Path $out "TurboTick-$version-mod-1.7.zip") -Force
    Compress-Archive -Path (Join-Path $server '*') -DestinationPath (Join-Path $out "TurboTick-$version-server.zip") -Force
    Get-ChildItem $out -Filter '*.zip' | Get-FileHash -Algorithm SHA256 | Select-Object Hash, Path
}
finally { Pop-Location }
