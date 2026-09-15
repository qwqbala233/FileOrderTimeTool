@echo off
setlocal
cd /d "%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo [ERROR] Windows .NET Framework C# compiler was not found.
  echo Please enable/install .NET Framework 4.x and run this file again.
  pause
  exit /b 1
)

echo Building...
if not exist "FileOrderTimeTool.ico" (
  echo [ERROR] FileOrderTimeTool.ico was not found.
  pause
  exit /b 1
)
"%CSC%" /nologo /target:winexe /win32manifest:app.manifest /win32icon:"FileOrderTimeTool.ico" /optimize+ /platform:anycpu /out:"FileOrderTimeTool.exe" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll "FileOrderTimeTool.cs"
if errorlevel 1 (
  echo.
  echo Build failed. Please send the error text to ChatGPT.
  pause
  exit /b 1
)

echo.
echo Build completed: FileOrderTimeTool.exe
start "" "FileOrderTimeTool.exe"
endlocal
