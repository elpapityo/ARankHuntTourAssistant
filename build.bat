@echo off
chcp 932 >nul
setlocal EnableExtensions

set "ROOT=%~dp0"
set "PROJECT=%ROOT%ARankHuntTourAssistant.csproj"
set "BUILD=%ROOT%_build"
set "RELEASE_ROOT=%ROOT%release"
set "RELEASE=%RELEASE_ROOT%\ARankHuntTourAssistant"
set "ZIP=%RELEASE_ROOT%\ARankHuntTourAssistant_v0.1.1.zip"
set "TARGET=Z:\ARankHuntTourAssistant\Current"

cls
echo ================================================
echo A-Rank Hunt Tour Assistant v0.1.1 ビルド
echo ================================================
echo.

echo [1/5] 復元しています...
dotnet restore "%PROJECT%"
if errorlevel 1 goto :fail

echo.
echo [2/5] ビルドしています...
if exist "%BUILD%" rmdir /s /q "%BUILD%"
mkdir "%BUILD%" >nul 2>&1
dotnet build "%PROJECT%" -c Release -o "%BUILD%" --no-restore
if errorlevel 1 goto :fail
if not exist "%BUILD%\ARankHuntTourAssistant.dll" goto :fail
if not exist "%BUILD%\ARankHuntTourAssistant.deps.json" goto :fail

echo.
echo [3/5] 配布用フォルダーを作成しています...
if exist "%RELEASE%" rmdir /s /q "%RELEASE%"
if not exist "%RELEASE_ROOT%" mkdir "%RELEASE_ROOT%"
mkdir "%RELEASE%"
mkdir "%RELEASE%\images"
copy /y "%BUILD%\ARankHuntTourAssistant.dll" "%RELEASE%\ARankHuntTourAssistant.dll" >nul
if errorlevel 1 goto :fail
copy /y "%BUILD%\ARankHuntTourAssistant.deps.json" "%RELEASE%\ARankHuntTourAssistant.deps.json" >nul
if errorlevel 1 goto :fail
copy /y "%ROOT%ARankHuntTourAssistant.json" "%RELEASE%\ARankHuntTourAssistant.json" >nul
if errorlevel 1 goto :fail
copy /y "%ROOT%images\icon.png" "%RELEASE%\images\icon.png" >nul
if errorlevel 1 goto :fail
if not exist "%RELEASE%\ARankHuntTourAssistant.dll" goto :fail
if not exist "%RELEASE%\images\icon.png" goto :fail

echo.
echo [4/5] ローカルテスト用へ配置しています...
if not exist "Z:\" (
    echo [エラー] Z: ドライブが見つかりません。
    goto :fail
)
if exist "%TARGET%" rmdir /s /q "%TARGET%"
mkdir "%TARGET%"
xcopy "%RELEASE%\*" "%TARGET%\" /E /I /Y >nul
if errorlevel 1 goto :fail
if not exist "%TARGET%\ARankHuntTourAssistant.dll" goto :fail
if not exist "%TARGET%\images\icon.png" goto :fail

echo.
echo [5/5] GitHub upload用ZIPを作成しています...
if exist "%ZIP%" del /q "%ZIP%"
powershell -NoProfile -Command "Compress-Archive -Path '%RELEASE%\*' -DestinationPath '%ZIP%' -Force"
if errorlevel 1 goto :fail
if not exist "%ZIP%" goto :fail
for %%A in ("%ZIP%") do if %%~zA LEQ 0 goto :fail

echo.
echo [成功] DLL: %TARGET%\ARankHuntTourAssistant.dll
echo [成功] アイコン: %TARGET%\images\icon.png
echo [成功] 配布ZIP: %ZIP%
echo.
echo ================================================
echo ビルドと配置が正常に完了しました
echo ================================================
echo.
echo ローカルテスト:
echo   %TARGET%\ARankHuntTourAssistant.dll
echo   %TARGET%\images\icon.png
echo.
echo GitHub upload用:
echo   %ZIP%
echo.
pause
exit /b 0

:fail
echo.
echo ================================================
echo ビルドまたは配置に失敗しました
echo ================================================
echo.
echo この画面の内容をそのまま送ってください。
echo.
pause
exit /b 1
