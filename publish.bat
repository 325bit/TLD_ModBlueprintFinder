@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"

echo [1/2] 发布单文件自包含 exe（约 65 MB，目标机不需要装 .NET）...
dotnet publish "TLD_ModBlueprintFinder\TLD_ModBlueprintFinder.csproj" -p:PublishProfile=SelfContainedSingleFile -v minimal --nologo
if errorlevel 1 (
    echo.
    echo [失败] 发布没成功，先确认 .NET 8 SDK 在不在：dotnet --list-sdks
    pause
    exit /b 1
)

echo.
echo [2/2] 完成，产物：
dir /b "dist\*.exe"
echo.
echo 把这个 exe 单独拷走即可运行；同目录会自动生成 TLD_ModBlueprintFinder_settings.json。
pause
