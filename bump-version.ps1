<#
.SYNOPSIS
    自动升级版本号并可选择自动提交与推送，触发 GitHub Actions Release。
.DESCRIPTION
    用法示例：
      .\bump-version.ps1             # 默认递增 patch (0.0.2 -> 0.0.3)
      .\bump-version.ps1 minor       # 递增 minor (0.0.2 -> 0.1.0)
      .\bump-version.ps1 major       # 递增 major (0.0.2 -> 1.0.0)
      .\bump-version.ps1 0.0.5       # 指定具体版本号
      .\bump-version.ps1 -Push       # 升级并自动执行 git commit & git push
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
    Write-Error "找不到工程文件：$projectPath"
}

# 读取当前版本
[xml]$projXml = Get-Content -LiteralPath $projectPath
$currentVersionStr = [string]$projXml.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($currentVersionStr)) {
    Write-Error "无法在 $projectPath 中找到 <Version> 节点。"
}

if ($currentVersionStr -notmatch '^(\d+)\.(\d+)\.(\d+)$') {
    Write-Error "当前版本格式非标准 X.Y.Z: $currentVersionStr"
}

$major = [int]$Matches[1]
$minor = [int]$Matches[2]
$patch = [int]$Matches[3]

# 计算新版本
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
            Write-Error "无效的目标版本参数：'$Target'。有效参数为 patch, minor, major 或具体版本如 1.0.0"
        }
    }
}

Write-Host "版本更新: v$currentVersionStr -> v$newVersion" -ForegroundColor Cyan

# 1. 更新 csproj
$projXml.Project.PropertyGroup.Version = $newVersion
$projXml.Save($projectPath)
# 规范化换行，避免格式差异
(Get-Content -LiteralPath $projectPath -Raw) -replace "`r?`n", "`r`n" | Set-Content -LiteralPath $projectPath -NoNewline
Write-Host "已更新 $projectPath" -ForegroundColor Green

# 2. 更新 README.md
if (Test-Path $readmePath) {
    $readmeContent = Get-Content -LiteralPath $readmePath -Raw -Encoding utf8
    $updatedReadme = $readmeContent -replace "当前版本：`$currentVersionStr`", "当前版本：`$newVersion`"
    Set-Content -LiteralPath $readmePath -Value $updatedReadme -Encoding utf8 -NoNewline
    Write-Host "已更新 $readmePath" -ForegroundColor Green
}

# 3. 提交与推送选项
if ($Push) {
    Write-Host "正在自动提交并推送远程仓库..." -ForegroundColor Yellow
    git add WechatDictationProbe.csproj README.md
    git commit -m "chore: bump version to v$newVersion"
    git push
    Write-Host "已推送到 GitHub！GitHub Actions 将自动触发 Release v$newVersion 流程。" -ForegroundColor Green
} else {
    Write-Host "`n提示：如果要发布此版本，请提交并推送：" -ForegroundColor Yellow
    Write-Host "  git add -A"
    Write-Host "  git commit -m `"chore: bump version to v$newVersion`""
    Write-Host "  git push"
    Write-Host "（或者下次直接加参数：.\bump-version.ps1 -Push）"
}
