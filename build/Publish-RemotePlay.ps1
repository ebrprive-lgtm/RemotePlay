param(
    [ValidateSet('PortableWinX64', 'SelfContainedWinX64')]
    [string]$Profile = 'PortableWinX64'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'RemotePlay.csproj'

Write-Host "Publishing RemotePlay with profile '$Profile'..."
dotnet publish $project /p:PublishProfile=$Profile

$publishRoot = Join-Path $repoRoot "bin\Release\net8.0-windows\publish"
Write-Host "Publish output root: $publishRoot"
