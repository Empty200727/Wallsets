$ErrorActionPreference = 'Stop'
$appPath = Join-Path $PSScriptRoot 'Wallsets.exe'
$enginePath = Join-Path $PSScriptRoot 'engine\mpv.exe'
# Ask the running instance to release the desktop and exit normally.
$command = Start-Process -FilePath $appPath -ArgumentList '--stop' -WindowStyle Hidden -PassThru
if (-not $command.WaitForExit(5000)) { $command.Kill() }
Start-Sleep -Milliseconds 800
# Recovery is restricted to this installation; other media players are untouched.
Get-CimInstance Win32_Process -Filter "Name = 'Wallsets.exe' OR Name = 'mpv.exe'" | Where-Object {
    $_.ExecutablePath -eq $appPath -or ($_.ExecutablePath -eq $enginePath -and $_.CommandLine -like '*--input-ipc-server=wallsets-mpv-*')
} | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
