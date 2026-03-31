:::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::
::Install CMZServerHost Via MSBuild                              ::
::Gihub https://github.com/RussDev7/CMZDedicatedServer           ::
::Developed, Maintained, And Sponsored By RussDev7, unknowghost0 ::
:::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::::
@ECHO OFF

Rem | Set Params
Set "VersionPrefix=1.0.0.0"
Set "filename=CMZServerHost-%VersionPrefix%"

Rem | Put the expected location of vswhere into a variable.
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"

Rem | Ask for the newest VS install that includes Microsoft.Component.MSBuild
Rem | and let vswhere do the glob‑expansion that finds MSBuild.exe.
for /f "usebackq tokens=*" %%I in (`
  "%VSWHERE%" -latest ^
              -products * ^
              -requires Microsoft.Component.MSBuild ^
              -find MSBuild\**\Bin\MSBuild.exe
`) do (
    set "MSBUILD=%%I"
)

Rem | Install SLN under x64 profile.
"%MSBUILD%" ".\src\CMZServerHost.sln" /restore /p:Configuration=Release /p:Platform=x86

Rem | Stop running server host so release files are not locked.
taskkill /IM "CMZServerHost.exe" /F >nul 2>&1
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -Command ^
  "$pattern = [regex]::Escape('CMZServerHost-' + $env:VersionPrefix);" ^
  "Get-CimInstance Win32_Process | Where-Object { $_.Name -eq 'cmd.exe' -and $_.CommandLine -match $pattern } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }" >nul 2>&1

Rem | Delete Paths & Create Paths
if exist ".\release\" rmdir /s /q ".\release"
mkdir ".\release"

Rem | Copy Over Items
xcopy /E /Y ".\src\CMZServerHost\build\ServerHost" ".\release\%filename%\"

Rem | Clean Up Files
if exist ".\release\*.xml"    del /f /q /s ".\release\*.xml"
if exist ".\release\*.pdb"    del /f /q /s ".\release\*.pdb"
if exist ".\release\*.config" del /f /q /s ".\release\*.config"

Rem | Delete & Create ZIP Release
if exist ".\%filename%.zip" (del /f ".\%filename%.zip")
powershell.exe -nologo -noprofile -command "Compress-Archive -Path ".\release\*" -DestinationPath ".\%filename%.zip""

Rem | Operation Complete
echo(
pause

