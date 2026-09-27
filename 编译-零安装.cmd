@echo off
chcp 65001 >nul
setlocal enabledelayedexpansion
set "ROOT=%~dp0"
if "%ROOT:~-1%"=="\" set "ROOT=%ROOT:~0,-1%"
set "CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

echo === BreadLauncher 编译 ===
if not exist "%CSC%" (
  echo [x] 找不到系统自带的 C# 编译器：%CSC%
  exit /b 1
)

if not exist "%ROOT%\build" mkdir "%ROOT%\build"
if not exist "%ROOT%\build\tools" mkdir "%ROOT%\build\tools"

if not exist "%ROOT%\assets\BreadLauncher.ico" (
  echo - 生成图标...
  "%CSC%" /nologo /target:exe /platform:anycpu /optimize+ /codepage:65001 /out:"%ROOT%\build\tools\MakeIcon.exe" /reference:System.dll,System.Drawing.dll "%ROOT%\tools\MakeIcon.cs" || exit /b 1
  "%ROOT%\build\tools\MakeIcon.exe" "%ROOT%\assets\BreadLauncher.ico" || exit /b 1
)

echo - 编译主程序...
"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 ^
  /win32manifest:"%ROOT%\app.manifest" /win32icon:"%ROOT%\assets\BreadLauncher.ico" ^
  /out:"%ROOT%\build\BreadLauncher.exe" ^
  /reference:System.dll,System.Core.dll,System.Drawing.dll,System.Windows.Forms.dll,System.Web.Extensions.dll ^
  "%ROOT%\src\*.cs"
if errorlevel 1 (
  echo [x] 编译失败
  exit /b 1
)

echo [√] 编译完成：%ROOT%\build\BreadLauncher.exe
endlocal