param(
    [ValidateSet('PortableWinX64', 'SelfContainedWinX64')]
    [string]$Profile = 'PortableWinX64'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'RemotePlay.csproj'
$releaseOutputRoot = Join-Path $repoRoot 'bin\Release\net8.0-windows'
$videoPlayerOutputRoot = Join-Path $repoRoot 'RemotePlay.VideoPlayer\bin\Release\net8.0-windows'
$publishRoot = Join-Path $releaseOutputRoot 'publish'
$publishDir = $publishRoot + [System.IO.Path]::DirectorySeparatorChar
$runtimeIdentifier = 'win-x64'
$selfContained = $Profile -eq 'SelfContainedWinX64'
$selfContainedArgument = $selfContained.ToString().ToLowerInvariant()

Write-Host "Publishing RemotePlay ($Profile, $runtimeIdentifier)..."

foreach ($outputDirectory in @($releaseOutputRoot, $videoPlayerOutputRoot)) {
    if (Test-Path -LiteralPath $outputDirectory) {
        Remove-Item -LiteralPath $outputDirectory -Recurse -Force
    }
}

$publishArguments = @(
    $project,
    '--configuration', 'Release',
    '--runtime', $runtimeIdentifier,
    '--self-contained', $selfContainedArgument,
    "/p:PublishDir=$publishDir"
)

& dotnet publish @publishArguments
$publishExitCode = $LASTEXITCODE
if ($publishExitCode -ne 0) {
    throw "dotnet publish failed with exit code $publishExitCode."
}

$projectXml = [xml](Get-Content -LiteralPath $project -Raw)
$expectedVersion = [string]($projectXml.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
$versionFile = Join-Path $publishRoot 'version.txt'
if (-not (Test-Path -LiteralPath $versionFile -PathType Leaf)) {
    throw "Publish output is missing version.txt: $versionFile"
}

$publishedVersion = (Get-Content -LiteralPath $versionFile -Raw).Trim()
if ($publishedVersion -ne $expectedVersion) {
    throw "Published version '$publishedVersion' does not match project version '$expectedVersion'."
}

$requiredFiles = @(
    'RemotePlay.exe',
    'RemotePlay.dll',
    'RemotePlay.runtimeconfig.json',
    'RemotePlay.VideoPlayer\RemotePlay.VideoPlayer.exe',
    'libvlc\win-x64\libvlc.dll',
    'libvlc\win-x64\libvlccore.dll',
    'RemotePlay.VideoPlayer\libvlc\win-x64\libvlc.dll',
    'RemotePlay.VideoPlayer\libvlc\win-x64\libvlccore.dll'
)
foreach ($relativePath in $requiredFiles) {
    $path = Join-Path $publishRoot $relativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Publish output is missing required file: $path"
    }
}

foreach ($libVlcRoot in @(
    (Join-Path $publishRoot 'libvlc'),
    (Join-Path $publishRoot 'RemotePlay.VideoPlayer\libvlc')
)) {
    foreach ($unsupportedArchitecture in @('win-x86', 'win-arm64')) {
        $path = Join-Path $libVlcRoot $unsupportedArchitecture
        if (Test-Path -LiteralPath $path) {
            throw "Unexpected LibVLC architecture in x64 publish: $path"
        }
    }
}

Write-Host "Publish output root: $publishRoot"
Write-Host "Published version: $publishedVersion"
