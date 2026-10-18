$ErrorActionPreference = 'Stop'

$repoRoot = [System.IO.Path]::GetFullPath($PSScriptRoot)
$buildRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'build'))
if ($buildRoot -ne (Join-Path $repoRoot 'build')) { throw "Unexpected build output path: $buildRoot" }
$buildItem = Get-Item -LiteralPath $buildRoot -ErrorAction SilentlyContinue
if ($buildItem -and ($buildItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
    throw "The build directory is a reparse point: $buildRoot"
}

$runningFromBuild = @(Get-Process -Name Mcotis -ErrorAction SilentlyContinue | Where-Object {
    $_.Path -and [System.IO.Path]::GetFullPath($_.Path).StartsWith(
        $buildRoot + [System.IO.Path]::DirectorySeparatorChar,
        [System.StringComparison]::OrdinalIgnoreCase)
})
if ($runningFromBuild.Count -gt 0) {
    throw 'Close Mcotis launched from build before rebuilding; Windows locks its DLLs.'
}

Push-Location $repoRoot
try {
    & dotnet build src\Mcotis.Desktop\Mcotis.Desktop.csproj -c FreshBuild
    if ($LASTEXITCODE -ne 0) { throw "FreshBuild failed with exit code $LASTEXITCODE" }
}
finally { Pop-Location }

$exePath = Join-Path $buildRoot 'Mcotis.exe'
foreach ($name in 'Mcotis.exe', 'Mcotis.dll', 'Mcotis.runtimeconfig.json') {
    if (-not (Test-Path -LiteralPath (Join-Path $buildRoot $name))) { throw "Build output is missing $name" }
}

$desktop = [Environment]::GetFolderPath('DesktopDirectory')
if (-not $desktop -or -not (Test-Path -LiteralPath $desktop)) { throw 'Desktop directory was not found.' }
$shortcutPath = Join-Path $desktop 'Mcotis.lnk'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $exePath
$shortcut.WorkingDirectory = $buildRoot
$shortcut.IconLocation = "$exePath,0"
$shortcut.Description = 'Mcotis - Meinhardt Cities of Tomorrow Integrated Structures'
$shortcut.Save()

Write-Output "Desktop icon: $shortcutPath"
Write-Output "Application: $exePath"
