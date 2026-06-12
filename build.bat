@echo off
chcp 65001 >nul
echo ========================================
echo Athlon Agent - Velopack 打包脚本
echo ========================================
echo.

set "SCRIPT_DIR=%~dp0"
cd /d "%SCRIPT_DIR%"

set "VERSION=%~1"
if "%VERSION%"=="" set "VERSION=1.0.0-dev"

set "VELOPACK_VERSION=0.0.1298"

echo [1/4] 关闭运行中的应用...
taskkill /F /IM Athlon.Agent.App.exe 2>nul
echo.

echo [2/4] 发布最新代码 (版本 %VERSION%)...
dotnet publish "%SCRIPT_DIR%src\Athlon.Agent.App\Athlon.Agent.App.csproj" -c Release -r win-x64 --self-contained true -o "%SCRIPT_DIR%publish" -p:Version=%VERSION%
if %errorlevel% neq 0 (
 echo 发布失败！
 pause
 exit /b 1
)
echo.

echo [3/4] 安装 vpk 工具 (版本 %VELOPACK_VERSION%)...
dotnet tool install -g vpk --version %VELOPACK_VERSION%
if %errorlevel% neq 0 (
 dotnet tool update -g vpk --version %VELOPACK_VERSION%
)
echo.

echo [4/4] Velopack 打包...
if not exist "%SCRIPT_DIR%Releases" mkdir "%SCRIPT_DIR%Releases"

vpk pack -u AthlonAgent -v %VERSION% -p "%SCRIPT_DIR%publish" -e Athlon.Agent.App.exe --packTitle "Athlon Agent" --icon "%SCRIPT_DIR%src\Athlon.Agent.App\Assets\app-icon.ico" --outputDir "%SCRIPT_DIR%Releases"
if %errorlevel% neq 0 (
 echo Velopack 打包失败！
 pause
 exit /b 1
)

echo.
echo ========================================
echo 打包完成！
echo 产物目录: %SCRIPT_DIR%Releases\
echo ========================================
echo.
dir /b "%SCRIPT_DIR%Releases"
echo.
pause
