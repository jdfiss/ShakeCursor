@echo off
rem Builds ShakeCursor.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
"%CSC%" /nologo /target:winexe /optimize+ /out:ShakeCursor.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll ShakeCursor.cs
