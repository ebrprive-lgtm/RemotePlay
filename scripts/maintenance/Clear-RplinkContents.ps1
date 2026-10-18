<#
.SYNOPSIS
Clears the contents of every .rplink file under a directory.

.DESCRIPTION
Recursively scans the specified directory, including hidden files and folders.
The files remain in place. Use -WhatIf to preview or -Confirm to approve changes.

.PARAMETER RootPath
The existing directory to scan.

.EXAMPLE
.\scripts\maintenance\Clear-RplinkContents.ps1 -RootPath 'D:\Media' -WhatIf

.EXAMPLE
.\scripts\maintenance\Clear-RplinkContents.ps1 -RootPath 'D:\Media'
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateNotNullOrEmpty()]
    [string]$RootPath
)

$ErrorActionPreference = 'Stop'

$root = Get-Item -LiteralPath $RootPath -Force
if ($root.PSProvider.Name -ne 'FileSystem' -or -not $root.PSIsContainer) {
    throw "RootPath must identify an existing filesystem directory: '$RootPath'."
}

$files = @(
    Get-ChildItem -LiteralPath $root.FullName -Filter '*.rplink' -File -Force -Recurse -ErrorAction Stop
)

$clearedCount = 0
if ($files.Count -gt 0 -and $PSCmdlet.ShouldProcess(
    $root.FullName,
    "Clear the contents of $($files.Count) .rplink file(s)"
)) {
    foreach ($file in $files) {
        [System.IO.File]::WriteAllText($file.FullName, [string]::Empty)
        $clearedCount++
    }
}

[pscustomobject]@{
    RootPath     = $root.FullName
    FilesFound   = $files.Count
    FilesCleared = $clearedCount
    PreviewOnly  = [bool]$WhatIfPreference
}
