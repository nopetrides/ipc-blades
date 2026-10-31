@echo off
rem Configure + build RigidIPCBackend (Release) and copy the DLL into Assets/Plugins/x86_64.
rem Close the Unity editor first: a loaded native plugin DLL cannot be overwritten.
setlocal
set VSROOT=G:\Tools\VisualStudio2022
call "%VSROOT%\VC\Auxiliary\Build\vcvars64.bat" >nul || exit /b 1
set CMAKE="%VSROOT%\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"
set HERE=%~dp0
%CMAKE% -S "%HERE%." -B "%HERE%build" -G Ninja -DCMAKE_BUILD_TYPE=Release || exit /b 1
%CMAKE% --build "%HERE%build" || exit /b 1
