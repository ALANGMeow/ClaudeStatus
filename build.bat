@echo off
setlocal
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set DIR=%~dp0
"%CSC%" /nologo /target:winexe /out:"%DIR%ClaudeStatus.exe" /win32manifest:"%DIR%app.manifest" /reference:System.Windows.Forms.dll,System.Drawing.dll,System.Web.Extensions.dll "%DIR%ClaudeStatus.cs"
if errorlevel 1 (
  echo BUILD FAILED
  exit /b 1
)
echo BUILD OK: %DIR%ClaudeStatus.exe
endlocal
