<#
.SYNOPSIS
  Publishes a self-contained Windows build of Carvera Controller C# to publish\win-x64.
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
