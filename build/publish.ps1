<#
.SYNOPSIS
  Publishes a self-contained Windows build of Carvera Controller C# to publish\win-x64,
  then updates the "Carvera Controller (latest build).lnk" shortcut in the repository root to point at it.
#>
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Output = (Join-Path $PSScriptRoot "..\publish\$Runtime")
)
$ErrorActionPreference = "Stop"
Get-Process CarveraController -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet publish (Join-Path $PSScriptRoot "..\src\Carvera.App\Carvera.App.csproj") `
    -c $Configuration -r $Runtime --self-contained true `
    -p:PublishSingleFile=false -o $Output
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
Write-Host "Published to $Output"

# Keep a shortcut in the repository root pointing at the latest build.
$exe = Join-Path (Resolve-Path $Output) "CarveraController.exe"
$shortcutPath = Join-Path (Resolve-Path (Join-Path $PSScriptRoot "..")) "Carvera Controller (latest build).lnk"
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $exe
$shortcut.WorkingDirectory = Split-Path $exe
$shortcut.Save()
Write-Host "Updated shortcut $shortcutPath -> $exe"
