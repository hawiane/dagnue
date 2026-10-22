param(
    [int]$MaxVersions = 3,
    [string]$TargetDirectory = "$PSScriptRoot\..\tmp"
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $TargetDirectory)) {
    Write-Host "Target directory '$TargetDirectory' does not exist."
    return
}

# Group versioned update archives by base prefix
$archives = Get-ChildItem -Path $TargetDirectory -File -Filter "*.tar.gz" |
    Sort-Object LastWriteTime -Descending

if ($archives.Count -le $MaxVersions) {
    Write-Host "Found $($archives.Count) archives. Within limit of $MaxVersions versions. No action required."
    return
}

$toKeep = $archives | Select-Object -First $MaxVersions
$toDelete = $archives | Select-Object -Skip $MaxVersions

Write-Host "Retaining $MaxVersions latest versions:"
$toKeep | ForEach-Object { Write-Host "  [KEEP] $($_.Name) ($($_.LastWriteTime))" }

Write-Host "`nDeleting older versions:"
foreach ($file in $toDelete) {
    Write-Host "  [DELETE] $($file.Name) ($($file.LastWriteTime))"
    Remove-Item -LiteralPath $file.FullName -Force
}

Write-Host "`nVersion pruning complete."
