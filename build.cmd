@echo off
rem Builds AJ139Tray.exe with the C# compiler that ships with Windows (.NET Framework 4.x). No SDK needed.
setlocal
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" ( echo csc.exe not found - .NET Framework 4.x is required & exit /b 1 )
cd /d "%~dp0"
"%CSC%" /nologo /target:winexe /optimize+ /out:AJ139Tray.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll aj139-tray.cs
