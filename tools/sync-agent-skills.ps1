param(
    [switch]$Check
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$sourceRoot = Join-Path $repoRoot '.agents/skills'
$targetRoot = Join-Path $repoRoot '.claude/skills'

$sources = @(Get-ChildItem -LiteralPath $sourceRoot -Directory -Filter 'portfolio-*' | Sort-Object Name)
if ($sources.Count -eq 0) {
    throw 'No canonical portfolio skills were found.'
}

if ($Check) {
    foreach ($source in $sources) {
        $sourceFile = Join-Path $source.FullName 'SKILL.md'
        $targetFile = Join-Path (Join-Path $targetRoot $source.Name) 'SKILL.md'

        if (-not (Test-Path -LiteralPath $targetFile)) {
            throw "Missing Claude skill mirror: $targetFile"
        }

        $sourceHash = (Get-FileHash -LiteralPath $sourceFile -Algorithm SHA256).Hash
        $targetHash = (Get-FileHash -LiteralPath $targetFile -Algorithm SHA256).Hash
        if ($sourceHash -ne $targetHash) {
            throw "Claude skill mirror is stale: $($source.Name)"
        }
    }

    $sourceNames = @($sources.Name)
    $orphans = @(Get-ChildItem -LiteralPath $targetRoot -Directory -Filter 'portfolio-*' -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -notin $sourceNames })
    if ($orphans.Count -gt 0) {
        throw "Orphaned Claude skill mirrors: $($orphans.Name -join ', ')"
    }

    Write-Output "Verified $($sources.Count) synchronized portfolio skills."
    exit 0
}

foreach ($source in $sources) {
    $destination = Join-Path $targetRoot $source.Name
    New-Item -ItemType Directory -Force -Path $destination | Out-Null
    Copy-Item -LiteralPath (Join-Path $source.FullName 'SKILL.md') -Destination (Join-Path $destination 'SKILL.md') -Force
}

Write-Output "Synchronized $($sources.Count) portfolio skills."
