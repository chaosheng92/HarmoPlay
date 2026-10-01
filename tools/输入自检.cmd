@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"

echo ============================================
echo  口琴谱演奏器 - 输入注入自检
echo ============================================
echo.
echo 正在自检：程序会发送一个无害的测试按键（F24），
echo 检查系统是否接受本程序的模拟输入。
echo.

if exist "口琴谱演奏器.exe" (
    "口琴谱演奏器.exe" --inputtest
) else if exist "HarmoPlay.exe" (
    "HarmoPlay.exe" --inputtest
) else (
    echo [错误] 同目录下找不到 口琴谱演奏器.exe / HarmoPlay.exe
    echo         请把本脚本放在程序目录里再运行。
    pause
    exit /b 1
)

echo.
echo ---- 自检报告 ----
if exist "input-test.log" (
    type "input-test.log"
) else if exist "data\input-test.log" (
    type "data\input-test.log"
) else (
    echo 没找到 input-test.log，请把本脚本和 exe 放在同一目录运行。
)

echo.
echo 提示：
echo   · 正常情况下应显示「注入正常」。
echo   · 若显示「注入被系统丢弃」，按报告里的建议处理：
echo       1) 不要从沙箱/受限环境启动；直接双击 exe 运行（普通用户即可）
echo       2) 游戏以管理员运行时，本程序右键「以管理员身份运行」
echo       3) 检查安全软件的拦截记录
echo.
pause
