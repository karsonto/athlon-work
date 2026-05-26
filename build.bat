@echo off
chcp 65001 >nul
echo ========================================
echo Athlon Agent - 本地打包脚本
echo ========================================
echo.

set "SCRIPT_DIR=%~dp0"
cd /d "%SCRIPT_DIR%"

echo [1/3] 关闭运行中的应用...
taskkill /F /IM Athlon.Agent.App.exe 2>nul
echo.

echo [2/3] 发布最新代码...
dotnet publish "%SCRIPT_DIR%src\Athlon.Agent.App\Athlon.Agent.App.csproj" -c Release -r win-x64 --self-contained true -o "%SCRIPT_DIR%publish"
if %errorlevel% neq 0 (
 echo 发布失败！
 pause
 exit /b 1
)
echo.

echo [3/3] 打包安装程序...
if not exist "%SCRIPT_DIR%installer" mkdir "%SCRIPT_DIR%installer"

set "ISCC_PATH="
if exist "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" (
 set "ISCC_PATH=C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
) else if exist "C:\Program Files\Inno Setup 6\ISCC.exe" (
 set "ISCC_PATH=C:\Program Files\Inno Setup 6\ISCC.exe"
) else if exist "D:\Inno Setup 6\ISCC.exe" (
 set "ISCC_PATH=D:\Inno Setup 6\ISCC.exe"
)

if "%ISCC_PATH%"=="" (
 echo 错误：未找到 Inno Setup！
 echo 请从 https://jrsoftware.org/isinfo.php 下载并安装
 pause
 exit /b 1
)

"%ISCC_PATH%" "%SCRIPT_DIR%setup.iss"
if %errorlevel% neq 0 (
 echo 打包失败！
 pause
 exit /b 1
)

echo.
echo ========================================
echo 打包完成！
echo 安装包位置: %SCRIPT_DIR%installer\
echo ========================================
echo.
dir /b "%SCRIPT_DIR%installer\*.exe"
echo.
pause
