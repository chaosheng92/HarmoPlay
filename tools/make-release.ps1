<#
.SYNOPSIS
  本地打发布包：编译自包含单文件、压缩、算 SHA256、生成 update.json。

.EXAMPLE
  pwsh -File tools/make-release.ps1 -Version 1.0.1 -Notes "新增下落模式"

.EXAMPLE
  pwsh -File tools/make-release.ps1 -Version 1.0.1 -Repo "myuser/HarmoPlay" -DotnetPath "C:\dotnet\dotnet.exe"
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Notes = "见 Release 说明",
    [string]$Repo = "你的用户名/HarmoPlay",
    [string]$DotnetPath = "",
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'dist'
$pkgName = "HarmoPlay-$Version-win-x64"

if (-not $DotnetPath) {
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { $DotnetPath = $cmd.Source }
    elseif ($env:DOTNET_ROOT) { $DotnetPath = Join-Path $env:DOTNET_ROOT 'dotnet.exe' }
    else { throw '找不到 dotnet，请用 -DotnetPath 指定 dotnet.exe 路径' }
}

Write-Host "使用 dotnet：$DotnetPath"
Write-Host "版本：$Version"

if (-not $SkipBuild) {
    if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
    & $DotnetPath publish (Join-Path $root 'HarmoPlay.csproj') -c Release -r win-x64 `
        --self-contained true -p:PublishSingleFile=true -p:DebugType=none `
        -p:Version=$Version -o $dist
    if ($LASTEXITCODE -ne 0) { throw "发布失败，退出码 $LASTEXITCODE" }
}

# 组装
$stage = Join-Path $root "package\$pkgName"
if (Test-Path (Join-Path $root 'package')) { Remove-Item (Join-Path $root 'package') -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null
Copy-Item (Join-Path $dist '*') $stage -Recurse -Force
Copy-Item (Join-Path $root '使用说明.txt') (Join-Path $stage '使用说明.txt') -Force
$exe = Join-Path $stage 'HarmoPlay.exe'
if (Test-Path $exe) { Rename-Item $exe '口琴谱演奏器.exe' }

$zip = Join-Path $root "package\$pkgName.zip"
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -Force
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()

# update.json
$manifest = [ordered]@{
    version   = $Version
    published = (Get-Date -Format 'yyyy-MM-dd')
    notes     = $Notes
    download  = "https://github.com/$Repo/releases/latest"
    mandatory = $false
    sha256    = $hash
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $root 'update.json') -Encoding UTF8

Write-Host ''
Write-Host "发布包：$zip"
Write-Host "SHA256：$hash"
Write-Host "清单：$(Join-Path $root 'update.json')"
Write-Host ''
Write-Host '下一步：把 zip 上传到 GitHub Release，并把 update.json 提交到默认分支。'
