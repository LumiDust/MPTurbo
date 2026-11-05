param(
    [string]$PackageDirectory,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (-not $PackageDirectory) { $PackageDirectory = Join-Path $repo 'artifacts/mod/TurboTick' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repo 'workshop-local/TurboTick' }
$source = [IO.Path]::GetFullPath($PackageDirectory)
$target = [IO.Path]::GetFullPath($OutputDirectory)
$sep = [IO.Path]::DirectorySeparatorChar
if ($source -eq $target -or $source.StartsWith($target.TrimEnd($sep) + $sep) -or $target.StartsWith($source.TrimEnd($sep) + $sep)) {
    throw 'Source and upload directories must be separate, non-overlapping directories.'
}
foreach ($file in @('About/About.xml', 'About/Preview.png', 'LoadFolders.xml', '1.7/Assemblies/TurboTick.dll')) {
    if (-not (Test-Path (Join-Path $source $file) -PathType Leaf)) { throw "Missing $file; first run scripts/package.ps1." }
}
$about = [xml](Get-Content (Join-Path $source 'About/About.xml') -Raw)
if ($about.ModMetaData.packageId -ne 'yg.turbotick' -or $about.ModMetaData.name -ne 'TurboTick') {
    throw 'The source package is not the canonical TurboTick mod.'
}
$idPath = Join-Path $target 'About/PublishedFileId.txt'
$idBackup = Join-Path $repo 'workshop-local/PublishedFileId.txt'
$id = $null
if (Test-Path $idPath) { $id = (Get-Content $idPath -Raw).Trim() }
elseif (Test-Path $idBackup) { $id = (Get-Content $idBackup -Raw).Trim() }
if ($id -and $id -notmatch '^[1-9][0-9]*$') { throw 'Existing PublishedFileId.txt is invalid; check it before preparing an update.' }
if ($id) {
    New-Item (Split-Path $idBackup -Parent) -ItemType Directory -Force | Out-Null
    [IO.File]::WriteAllText($idBackup, $id)
}
New-Item $target -ItemType Directory -Force | Out-Null
# Keep About in place so the game's existing Workshop ID survives even if copying is interrupted.
New-Item (Join-Path $target 'About') -ItemType Directory -Force | Out-Null
Get-ChildItem (Join-Path $source 'About') -File | Where-Object Name -ne 'PublishedFileId.txt' | ForEach-Object {
    Copy-Item $_.FullName (Join-Path $target 'About') -Force
}
foreach ($folder in @('1.7', 'Languages', 'docs')) {
    $destination = Join-Path $target $folder
    if (Test-Path $destination) { Remove-Item $destination -Recurse -Force }
    if (Test-Path (Join-Path $source $folder)) { Copy-Item (Join-Path $source $folder) $destination -Recurse }
}
foreach ($file in @('LoadFolders.xml', 'LICENSE', 'README.md', 'CONTRIBUTING.md', 'CHANGELOG.md')) {
    if (Test-Path (Join-Path $source $file)) { Copy-Item (Join-Path $source $file) (Join-Path $target $file) -Force }
}
if ($id) { [IO.File]::WriteAllText($idPath, $id) }
Write-Output "Prepared upload directory: $target"
Write-Output $(if ($id) { "Existing Workshop item preserved: $id" } else { 'First upload: no PublishedFileId.txt has been generated.' })
Write-Output 'No Steam upload was performed. Move this folder into the game Mods directory, or set OutputDirectory to that location.'
