@echo off
setlocal
pushd "%~dp0" || exit /b 1

dotnet publish src\Dollhouse.Runtime\Dollhouse.Runtime.csproj -c Release -r win-x64 -p:PublishAot=true -o artifacts\runtime --nologo --disable-build-servers -m:1 -nr:false
if errorlevel 1 goto failed

dotnet publish src\Dollhouse.App\Dollhouse.App.csproj -p:PublishProfile=Windows --nologo --disable-build-servers -m:1 -nr:false
if errorlevel 1 goto failed

echo Ready: %CD%\dist\Dollhouse.exe
popd
exit /b 0

:failed
echo Could not build Dollhouse.exe. 1>&2
popd
exit /b 1
