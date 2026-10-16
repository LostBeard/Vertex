@echo off
rem Builds Output\VertexSetup-<version>.exe: a self-contained win-x64 publish of Vertex into stage\, then Inno Setup.
rem Needs the .NET SDK and Inno Setup 6.1+ (ISCC.exe). Vertex.exe must not be running from stage\.
setlocal
cd /d "%~dp0"

set ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe
if not exist "%ISCC%" set ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe
if not exist "%ISCC%" set ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe
if not exist "%ISCC%" (
  echo Inno Setup 6 was not found.
  exit /b 1
)

rem The version comes from Vertex.csproj so the installer and the app agree.
for /f "delims=" %%v in ('powershell -NoProfile -Command "([xml](Get-Content ..\Vertex\Vertex.csproj)).Project.PropertyGroup.Version | Where-Object { $_ }"') do set VERSION=%%v
if "%VERSION%"=="" (
  echo Could not read the Version from Vertex.csproj.
  exit /b 1
)

dotnet publish ..\Vertex\Vertex.csproj -c Release -r win-x64 --self-contained true -o stage
if errorlevel 1 exit /b 1

"%ISCC%" /DAppVersion=%VERSION% Vertex.iss
if errorlevel 1 exit /b 1

echo.
echo Built Output\VertexSetup-%VERSION%.exe
