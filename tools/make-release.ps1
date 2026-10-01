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
    [string]$PfxPath = "",
    [string]$PfxPassword = "",
    [string]$SigntoolPath = "",
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
$firstRun = Join-Path $root 'docs\首次运行说明.txt'
if (Test-Path $firstRun) { Copy-Item $firstRun (Join-Path $stage '首次运行说明.txt') -Force }
$selftestCmd = Join-Path $root 'tools\输入自检.cmd'
if (Test-Path $selftestCmd) { Copy-Item $selftestCmd (Join-Path $stage '输入自检.cmd') -Force }
$unblockCmd = Join-Path $root 'tools\解除下载锁定.cmd'
if (Test-Path $unblockCmd) { Copy-Item $unblockCmd (Join-Path $stage '解除下载锁定.cmd') -Force }
# 示例曲谱与 AI 转谱要求（软件左下角也能一键另存）
$resDir = Join-Path $root 'Resources'
if (Test-Path $resDir) {
    $docDir = Join-Path $stage '文档与示例'
    New-Item -ItemType Directory -Force -Path $docDir | Out-Null
    Copy-Item (Join-Path $resDir '*') $docDir -Force
}
$exe = Join-Path $stage 'HarmoPlay.exe'
if (Test-Path $exe) { Rename-Item $exe '口琴谱演奏器.exe' }

# ---- 可选：代码签名（消除 SmartScreen"发布者未知"）----
$targetExe = Join-Path $stage '口琴谱演奏器.exe'
if ($PfxPath) {
    if (-not $SigntoolPath) {
        $cands = @()
        $cmd = Get-Command signtool.exe -ErrorAction SilentlyContinue
        if ($cmd) { $cands += $cmd.Source }
        $cands += Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin\*\x64\signtool.exe' -ErrorAction SilentlyContinue |
                  Sort-Object FullName -Descending | Select-Object -ExpandProperty FullName
        $SigntoolPath = $cands | Select-Object -First 1
    }
    if ($SigntoolPath -and (Test-Path $SigntoolPath)) {
        Write-Host "使用 signtool：$SigntoolPath"
        & $SigntoolPath sign /fd SHA256 /td SHA256 /tr http://timestamp.digicert.com `
            /f $PfxPath /p $PfxPassword $targetExe
        if ($LASTEXITCODE -eq 0) { Write-Host "签名完成 ✓" }
        else { Write-Warning "签名失败（退出码 $LASTEXITCODE），继续打未签名包" }
    }
    else {
        Write-Warning "找不到 signtool.exe（安装 Windows SDK 的 Signing Tools，或用 -SigntoolPath 指定），跳过签名"
    }
}
else {
    Write-Host "未提供 -PfxPath，跳过代码签名（用户首次运行会看到 SmartScreen 提示，见 docs/首次运行说明.txt）"
}

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

