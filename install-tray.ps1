# Installs the Hatchet tray app and has it start when you sign in to Windows. The tray starts Hatchet from this
# repository, rebuilding it each time it starts, and stops it when you exit the tray.
#
#   ./install-tray.ps1             install, or update after pulling, and start the tray
#   ./install-tray.ps1 -Uninstall  stop the tray and Hatchet, and stop starting them at sign-in
param([switch]$Uninstall)

$ErrorActionPreference = 'Stop'
$installDir = Join-Path $env:LOCALAPPDATA 'Hatchet\tray'
$exe = Join-Path $installDir 'Hatchet.Tray.exe'

# Close a running tray, which stops the Hatchet it started, so its files can be replaced.
if (Test-Path $exe) {
    $exit = Start-Process -FilePath $exe -ArgumentList '--exit' -Wait -PassThru
    if ($exit.ExitCode -ne 0) { throw 'The running tray did not exit. Exit it from its menu and try again.' }
}

if ($Uninstall) {
    Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'Hatchet' -ErrorAction SilentlyContinue
    if (Test-Path $installDir) { Remove-Item -Recurse -Force $installDir }
    Write-Host 'Removed the Hatchet tray. Hatchet no longer starts at sign-in.'
    return
}

# The tray runs from its own folder, so building this repository never finds its files in use.
dotnet publish (Join-Path $PSScriptRoot 'src/Hatchet.Tray/Hatchet.Tray.csproj') -c Release -o $installDir --nologo
if ($LASTEXITCODE -ne 0) { throw 'Building the tray failed.' }

# --start-at-login records the tray, with this repository, in your Run key, then starts Hatchet as usual.
Start-Process -FilePath $exe -ArgumentList '--repo', ('"{0}"' -f $PSScriptRoot.TrimEnd('\')), '--start-at-login'
Write-Host "Installed the Hatchet tray in $installDir. It starts Hatchet now, unless it's already running, and at each sign-in."
Write-Host 'Look for the hatchet icon in the notification area. You may need to drag it out of the overflow (^) menu.'
