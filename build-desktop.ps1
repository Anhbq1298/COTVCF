$ErrorActionPreference = 'Stop'

$repoRoot = [System.IO.Path]::GetFullPath($PSScriptRoot)
$binRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'bin'))
if ($binRoot -ne (Join-Path $repoRoot 'bin')) { throw "Unexpected build output path: $binRoot" }
$binItem = Get-Item -LiteralPath $binRoot -ErrorAction SilentlyContinue
if ($binItem -and ($binItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
    throw "The bin directory is a reparse point: $binRoot"
}

$runningFromBin = @(Get-Process -Name Mcotis -ErrorAction SilentlyContinue | Where-Object {
    $_.Path -and [System.IO.Path]::GetFullPath($_.Path).StartsWith(
        $binRoot + [System.IO.Path]::DirectorySeparatorChar,
        [System.StringComparison]::OrdinalIgnoreCase)
})
if ($runningFromBin.Count -gt 0) {
    throw 'Close Mcotis launched from bin before rebuilding; Windows locks its DLLs.'
}

Push-Location $repoRoot
try {
    & dotnet build COTVCF.csproj -c FreshBuild
    if ($LASTEXITCODE -ne 0) { throw "FreshBuild failed with exit code $LASTEXITCODE" }
}
finally { Pop-Location }

$exePath = Join-Path $binRoot 'Mcotis.exe'
foreach ($name in 'Mcotis.exe', 'Mcotis.dll', 'Mcotis.runtimeconfig.json') {
    if (-not (Test-Path -LiteralPath (Join-Path $binRoot $name))) { throw "Build output is missing $name" }
}

$desktop = [Environment]::GetFolderPath('DesktopDirectory')
if (-not $desktop -or -not (Test-Path -LiteralPath $desktop)) { throw 'Desktop directory was not found.' }
$shortcutPath = Join-Path $desktop 'Mcotis.lnk'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $exePath
$shortcut.WorkingDirectory = $binRoot
$shortcut.IconLocation = "$exePath,0"
$shortcut.Description = 'Mcotis - Meinhardt Cities of Tomorrow Integrated Structures'
$shortcut.Save()

Write-Output "Desktop icon: $shortcutPath"
Write-Output "Application: $exePath"
