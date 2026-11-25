@echo off
rem Publishes Tandem for Windows as Native AOT: publish\win-<arch>\Tandem.exe plus the native Skia, HarfBuzz and
rem ANGLE libraries beside it. No .NET runtime is needed on the target machine.
rem
rem Usage:  build\publish-windows.bat [x64^|arm64^|all]      (default: x64)
rem
rem Needs the .NET 11 SDK and the MSVC linker: Visual Studio or Build Tools with the "Desktop development with C++"
rem workload (for arm64 also "MSVC ... ARM64 build tools"). Native AOT cannot cross-compile between operating
rem systems, so this runs only on Windows. Every run removes and recreates publish\win-<arch>. Exit code 0 on
rem success, 1 on any error.
setlocal EnableExtensions EnableDelayedExpansion

set "ROOT=%~dp0.."
for %%R in ("%ROOT%") do set "ROOT=%%~fR"
set "PROJECT=%ROOT%\src\Tandem.App\Tandem.App.csproj"
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"

set "TARGET=%~1"
if "%TARGET%"=="" set "TARGET=x64"
if /i "%TARGET%"=="-h" goto :usage
if /i "%TARGET%"=="--help" goto :usage
if /i "%TARGET%"=="/?" goto :usage
if /i "%TARGET%"=="all" (
    set "ARCHS=x64 arm64"
) else if /i "%TARGET%"=="x64" (
    set "ARCHS=x64"
) else if /i "%TARGET%"=="arm64" (
    set "ARCHS=arm64"
) else (
    echo error: unknown architecture "%TARGET%". 1>&2
    goto :usage_error
)

where dotnet >nul 2>&1
if errorlevel 1 (
    echo error: 'dotnet' was not found in PATH. Install the .NET 11 SDK: https://dot.net/download 1>&2
    exit /b 1
)
for /f "delims=" %%V in ('dotnet --version 2^>nul') do set "DOTNET_VERSION=%%V"
echo ==^> .NET SDK %DOTNET_VERSION%

for %%A in (%ARCHS%) do (
    call :publish %%A
    if errorlevel 1 (
        echo. 1>&2
        echo FAILED: win-%%A 1>&2
        exit /b 1
    )
)
echo.
echo ==^> Done
exit /b 0

rem ---------------------------------------------------------------------------------------------------------------
rem :publish <x64|arm64>
:publish
setlocal EnableDelayedExpansion
set "ARCH=%~1"
set "RID=win-%ARCH%"
set "OUT=%ROOT%\publish\%RID%"
set "EXTRA="
echo.
echo ==^> %RID%: looking for the MSVC linker

call :find_cpp_tools %ARCH%
if errorlevel 1 exit /b 1

echo ==^> %RID%: cleaning %OUT%
if exist "%OUT%" rmdir /s /q "%OUT%"
if exist "%OUT%" (
    echo error: cannot remove %OUT% - is Tandem.exe still running from there? 1>&2
    exit /b 1
)

echo ==^> %RID%: dotnet publish ^(Release, Native AOT^)
set "STARTED=%TIME%"
dotnet publish "%PROJECT%" -c Release -r %RID% --self-contained true -p:PublishAot=true -p:DebugType=none -p:DebugSymbols=false %EXTRA% -o "%OUT%"
if errorlevel 1 (
    echo error: dotnet publish failed for %RID%. 1>&2
    exit /b 1
)

rem Native AOT leaves no managed assemblies: an apphost with Tandem.App.dll beside it means AOT did not run.
if exist "%OUT%\Tandem.App.dll" (
    echo error: %OUT% contains Tandem.App.dll - this is not a Native AOT build. 1>&2
    exit /b 1
)
if not exist "%OUT%\Tandem.App.exe" (
    echo error: %OUT%\Tandem.App.exe was not produced. 1>&2
    exit /b 1
)
rem The native libraries ship their debug symbols (libSkiaSharp.pdb alone is ~80 MB); users do not need them.
del /q "%OUT%\*.pdb" >nul 2>&1
move /y "%OUT%\Tandem.App.exe" "%OUT%\Tandem.exe" >nul
if errorlevel 1 (
    echo error: cannot rename Tandem.App.exe to Tandem.exe. 1>&2
    exit /b 1
)

echo ==^> %RID%: started %STARTED%, finished %TIME%
for %%F in ("%OUT%\*") do echo     %%~zF	%%~nxF
echo ==^> %RID%: %OUT%\Tandem.exe
endlocal & exit /b 0

rem ---------------------------------------------------------------------------------------------------------------
rem :find_cpp_tools <x64|arm64>
rem Sets EXTRA (and, for the fallback, PATH and LIB) in the caller's scope. Exit code 1 with an explanation when no
rem usable MSVC toolset is installed.
:find_cpp_tools
set "COMPONENT=Microsoft.VisualStudio.Component.VC.Tools.x86.x64"
if /i "%~1"=="arm64" set "COMPONENT=Microsoft.VisualStudio.Component.VC.Tools.ARM64"
set "HOSTDIR=Hostx64"
if /i "%PROCESSOR_ARCHITECTURE%"=="ARM64" set "HOSTDIR=Hostarm64"

if not exist "%VSWHERE%" goto :no_cpp_tools

rem 1. The normal case: the "Desktop development with C++" workload is registered with the installer. The ILCompiler
rem    finds it the same way (vswhere -requires ...) and sets up vcvarsall itself.
set "VSPATH="
for /f "usebackq delims=" %%I in (`call "%VSWHERE%" -latest -prerelease -products * -requires %COMPONENT% -property installationPath`) do set "VSPATH=%%I"
if defined VSPATH (
    echo     Visual Studio with %COMPONENT%: !VSPATH!
    exit /b 0
)

rem 2. Fallback: an MSVC toolset is on disk (an older or partial install) but the component is not registered, so
rem    the ILCompiler's own lookup fails. Point PATH and LIB at the linker, the CRT and the Windows SDK and let the
rem    ILCompiler use the environment (IlcUseEnvironmentalTools), as it would in a Developer Command Prompt.
set "MSVC="
for /f "usebackq delims=" %%I in (`call "%VSWHERE%" -all -prerelease -products * -property installationPath`) do (
    for /d %%V in ("%%I\VC\Tools\MSVC\*") do (
        if exist "%%V\bin\%HOSTDIR%\%~1\link.exe" (
            if exist "%%V\lib\%~1\msvcrt.lib" (
                set "MSVC=%%V"
                set "MSVC_LIB=%%V\lib\%~1"
            ) else if exist "%%V\lib\onecore\%~1\msvcrt.lib" (
                set "MSVC=%%V"
                set "MSVC_LIB=%%V\lib\onecore\%~1"
            )
        )
    )
)
if not defined MSVC goto :no_cpp_tools

set "SDKLIB="
for /d %%S in ("%ProgramFiles(x86)%\Windows Kits\10\Lib\10.*") do (
    if exist "%%S\um\%~1\kernel32.lib" if exist "%%S\ucrt\%~1\ucrt.lib" set "SDKLIB=%%S"
)
if defined SDKLIB goto :have_sdk
rem Outside any ( ) block: the ")" of "Program Files (x86)" would end it.
echo error: found the MSVC linker in %MSVC% but no Windows 10/11 SDK libraries for %~1 1>&2
echo        in "%ProgramFiles(x86)%\Windows Kits\10\Lib". Install a Windows SDK with Visual Studio Installer. 1>&2
exit /b 1

:have_sdk

echo     The C++ workload is not registered with Visual Studio Installer; using the toolset found on disk:
echo     linker   !MSVC!\bin\%HOSTDIR%\%~1
echo     CRT      !MSVC_LIB!
echo     SDK      !SDKLIB!
rem In front of PATH: Git for Windows puts its own coreutils link.exe there.
set "PATH=!MSVC!\bin\%HOSTDIR%\%~1;%PATH%"
set "LIB=!MSVC_LIB!;!SDKLIB!\ucrt\%~1;!SDKLIB!\um\%~1"
set "EXTRA=-p:IlcUseEnvironmentalTools=true"
exit /b 0

:no_cpp_tools
echo error: Native AOT needs the MSVC linker, and no Visual Studio C++ build tools for %~1 were found. 1>&2
echo        Install Visual Studio 2022+ or "Build Tools for Visual Studio" with the workload 1>&2
echo        "Desktop development with C++" ^(Microsoft.VisualStudio.Workload.NativeDesktop^). 1>&2
if /i "%~1"=="arm64" echo        For arm64 also select the component "MSVC ... ARM64/ARM64EC build tools". 1>&2
echo        Details: https://aka.ms/nativeaot-prerequisites 1>&2
exit /b 1

:usage
echo Usage: build\publish-windows.bat [x64^|arm64^|all]
echo   x64    publish\win-x64\Tandem.exe ^(default^)
echo   arm64  publish\win-arm64\Tandem.exe ^(needs the MSVC ARM64 build tools^)
echo   all    both
exit /b 0

:usage_error
echo Usage: build\publish-windows.bat [x64^|arm64^|all] 1>&2
exit /b 1
