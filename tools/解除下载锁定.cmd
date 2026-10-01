@echo off
chcp 65001 >nul
setlocal
echo ============================================
echo  解除 Windows 下载标记（Mark of the Web）
echo ============================================
echo.
echo 作用：把 zip / 解压后的文件夹里的"来自 Internet"标记去掉，
echo       这样就不会再弹「已保护你的电脑」或「Internet 安全设置阻止打开」。
echo.
echo 用法：
echo   1) 把本脚本放到要处理的文件夹里（或压缩包所在目录）双击；
echo   2) 或拖拽一个文件夹 / 压缩包到本脚本图标上。
echo.

if "%~1"=="" (
    set "TARGET=%~dp0"
) else (
    set "TARGET=%~1"
)

echo 处理目标：%TARGET%
echo.

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$t='%TARGET%'; if(Test-Path -LiteralPath $t -PathType Container){ Get-ChildItem -LiteralPath $t -Recurse -File -ErrorAction SilentlyContinue | Unblock-File -ErrorAction SilentlyContinue; Write-Host '已解除该目录下所有文件的锁定' -ForegroundColor Green } else { Unblock-File -LiteralPath $t -ErrorAction SilentlyContinue; Write-Host '已解除该文件的锁定' -ForegroundColor Green }; Write-Host '完成。现在可以正常解压 / 运行了。'"

echo.
pause
