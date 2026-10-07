param([switch]$Loop, [switch]$PauseTimer)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
function Request([string]$pipeName, [string]$payload, [switch]$Mpv) {
    $pipe = [System.IO.Pipes.NamedPipeClientStream]::new('.', $pipeName, [System.IO.Pipes.PipeDirection]::InOut)
    try {
        $pipe.Connect(2000)
        $writer = [System.IO.StreamWriter]::new($pipe, [System.Text.UTF8Encoding]::new($false), 1024, $true)
        $reader = [System.IO.StreamReader]::new($pipe)
        $writer.AutoFlush = $true; $writer.WriteLine($payload)
        do { $reply = $reader.ReadLine() | ConvertFrom-Json } while ($Mpv -and $null -eq $reply.request_id)
        if ($Mpv) { if ($reply.error -ne 'success') { throw $reply.error }; return $reply.data }
        return $reply
    } finally { $pipe.Dispose() }
}
function App([string]$command) { Request ('Wallsets-control-' + $env:USERNAME) $command }
$state = App '--status'
function Mpv([object[]]$command) { Request $state.playerPipe (@{ command = $command; request_id = 1 } | ConvertTo-Json -Compress -Depth 8) -Mpv }
if ($PauseTimer) {
    if (-not $state.paused -or $state.intervalSeconds -ne 5) { throw 'Set video pause and 5-second interval before this test.' }
    Start-Sleep -Seconds 6
    $after = App '--status'
    if ($after.currentFile -eq $state.currentFile -or -not $after.paused -or -not (Mpv @('get_property', 'pause'))) { throw 'Pause/timer independence failed' }
    'PASS wallpaper changes while video stays paused'
    $after | Select-Object currentFile,paused,intervalSeconds
}
if ($Loop) {
    $short = Get-Content (Join-Path $PSScriptRoot 'media-probe.json') -Raw | ConvertFrom-Json | Sort-Object Duration | Select-Object -First 1
    $null = Mpv @('loadfile', $short.Path, 'replace')
    $null = Mpv @('set_property', 'pause', $false)
    Start-Sleep -Milliseconds 400
    $before = Get-Process -Id $state.playerPid
    $cpu = $before.CPU
    $positions = for ($i = 0; $i -lt 16; $i++) {
        Start-Sleep -Milliseconds 500
        Mpv @('get_property', 'time-pos')
    }
    $wraps = 0
    for ($i = 1; $i -lt $positions.Count; $i++) { if ($positions[$i] -lt $positions[$i-1]) { $wraps++ } }
    if ($wraps -lt 2) { throw "Expected repeated loops; saw $wraps" }
    $p = Get-Process -Id $state.playerPid
    [pscustomobject]@{ Result = 'PASS'; Duration = $short.Duration; Wraps = $wraps; Positions = $positions; HardwareDecoder = (Mpv @('get_property','hwdec-current')); CPUPercent = [math]::Round(($p.CPU-$cpu)/8/[Environment]::ProcessorCount*100,2); MemoryMB = [math]::Round($p.WorkingSet64/1MB); Loop = (Mpv @('get_property','loop-file')) } | Format-List
    $null = Mpv @('screenshot-to-file', (Join-Path $PSScriptRoot 'wallpaper-frame.png'), 'video')
    $null = App '--next'
}
