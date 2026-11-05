@echo off
rem PlcDashboard.exe derlemesi - .NET Framework 4 csc.exe
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
"%CSC%" -nologo -target:winexe -out:PlcDashboard.exe ^
  -r:System.dll -r:System.Core.dll -r:System.Drawing.dll ^
  -r:System.Windows.Forms.dll -r:System.Web.Extensions.dll ^
  src\PlcDashboard.cs
if errorlevel 1 (echo DERLEME BASARISIZ & exit /b 1)
echo PlcDashboard.exe hazir.
