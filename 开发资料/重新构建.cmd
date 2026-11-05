@echo off
chcp 65001 >nul
setlocal
pushd "%~dp0"
set "CSC_PATH=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC_PATH%" (
  echo Windows .NET Framework C# compiler was not found.
  popd
  exit /b 1
)
"%CSC_PATH%" /nologo /codepage:65001 /target:winexe /platform:x64 /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Core.dll /win32manifest:"源码\app.manifest" /win32icon:"源码\app.ico" /out:"..\全屏助手.exe" "源码\Program.cs" "源码\MainForm.cs" "源码\WindowController.cs"
set "BUILD_RESULT=%ERRORLEVEL%"
if "%BUILD_RESULT%"=="0" echo Build completed.
popd
exit /b %BUILD_RESULT%
