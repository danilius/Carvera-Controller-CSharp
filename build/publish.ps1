<#
.SYNOPSIS
  Publishes a self-contained Windows build of Carvera Controller C# and its layout editor to publish\win-x64
  (both programs share the folder, so they share the runtime), then updates the shortcuts in the repository root
  ("Carvera Controller (latest build).lnk" and "Carvera Layout Editor (latest build).lnk") to point at them.
#>
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Output = (Join-Path $PSScriptRoot "..\publish\$Runtime")
)
$ErrorActionPreference = "Stop"
Get-Process CarveraController, CarveraLayoutEditor -ErrorAction SilentlyContinue | Stop-Process -Force
foreach ($project in "Carvera.App", "Carvera.Editor") {
    dotnet publish (Join-Path $PSScriptRoot "..\src\$project\$project.csproj") `
        -c $Configuration -r $Runtime --self-contained true `
        -p:PublishSingleFile=false -o $Output
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $project" }
}
Write-Host "Published to $Output"

# Keep shortcuts in the repository root pointing at the latest build.
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$shell = New-Object -ComObject WScript.Shell
foreach ($app in @(
        @{ Exe = "CarveraController.exe"; Link = "Carvera Controller (latest build).lnk" },
        @{ Exe = "CarveraLayoutEditor.exe"; Link = "Carvera Layout Editor (latest build).lnk" })) {
    $exe = Join-Path (Resolve-Path $Output) $app.Exe
    $shortcutPath = Join-Path $root $app.Link
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $exe
    $shortcut.WorkingDirectory = Split-Path $exe
    $shortcut.Save()
    Write-Host "Updated shortcut $shortcutPath -> $exe"
}
