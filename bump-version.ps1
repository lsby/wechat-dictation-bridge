<#
.SYNOPSIS
    Bump project version and optionally commit and push to trigger GitHub Release.
#>

[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$Target = "patch",

    [switch]$Push
)

$ErrorActionPreference = "Stop"

$projectPath = Join-Path $PSScriptRoot "WechatDictationProbe.csproj"
$readmePath = Join-Path $PSScriptRoot "README.md"

if (-not (Test-Path $projectPath)) {
    Write-Error "Project file not found: $projectPath"
}

$projContent = [System.IO.File]::ReadAllText($projectPath, [System.Text.Encoding]::UTF8)
if ($projContent -notmatch '<Version>([^<]+)</Version>') {
    Write-Error "Cannot find <Version> in $projectPath"
}

$currentVersionStr = $Matches[1]
if ($currentVersionStr -notmatch '^(\d+)\.(\d+)\.(\d+)$') {
    Write-Error "Current version is not in standard X.Y.Z format: $currentVersionStr"
}

$major = [int]$Matches[1]
$minor = [int]$Matches[2]
$patch = [int]$Matches[3]

$newVersion = ""
switch ($Target.ToLowerInvariant()) {
    "patch" {
        $newVersion = "$major.$minor.$($patch + 1)"
    }
    "minor" {
        $newVersion = "$major.$($minor + 1).0"
    }
    "major" {
        $newVersion = "$($major + 1).0.0"
    }
    default {
        if ($Target -match '^\d+\.\d+\.\d+$') {
            $newVersion = $Target
        } else {
            Write-Error "Invalid target version: '$Target'"
        }
    }
}

Write-Host "Version: v$currentVersionStr -> v$newVersion" -ForegroundColor Cyan

# 1. Update csproj
$updatedProj = [System.Text.RegularExpressions.Regex]::Replace($projContent, '(?<=<Version>)[^<]+(?=</Version>)', $newVersion)
[System.IO.File]::WriteAllText($projectPath, $updatedProj, [System.Text.Encoding]::UTF8)
Write-Host "Updated $projectPath" -ForegroundColor Green

# 2. Update README.md
if (Test-Path $readmePath) {
    $readmeContent = [System.IO.File]::ReadAllText($readmePath, [System.Text.Encoding]::UTF8)
    $replacement = [char]0x60 + $newVersion + [char]0x60
    $updatedReadme = [System.Text.RegularExpressions.Regex]::Replace($readmeContent, '\x60\d+\.\d+\.\d+\x60', $replacement)
    [System.IO.File]::WriteAllText($readmePath, $updatedReadme, [System.Text.Encoding]::UTF8)
    Write-Host "Updated $readmePath" -ForegroundColor Green
}

# 3. Commit & Push
if ($Push) {
    Write-Host "Auto committing and pushing..." -ForegroundColor Yellow
    git add -A
    git commit -m "chore: bump version to v$newVersion"
    git push
    Write-Host "Pushed to GitHub! Release workflow triggered for v$newVersion." -ForegroundColor Green
} else {
    Write-Host "`nTo release this version, commit and push:" -ForegroundColor Yellow
    Write-Host "  git add -A"
    Write-Host "  git commit -m `"chore: bump version to v$newVersion`""
    Write-Host "  git push"
}