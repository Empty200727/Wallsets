param([int]$Seconds = 30)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Test-Playback.ps1')
$initial = App '--status'
if (-not $initial.attached -or $initial.renderer.parentClass -ne 'WorkerW') { throw 'Renderer is not in the wallpaper layer.' }
$samples = for ($i = 0; $i -lt $Seconds; $i++) {
    Start-Sleep -Seconds 1
    $sample = App '--status'
    if ($sample.playbackFault -or -not $sample.attached -or $sample.playerPid -ne $initial.playerPid -or $sample.starts -ne $initial.starts) {
        throw ('Wallpaper restarted or detached: ' + ($sample | ConvertTo-Json -Compress -Depth 5))
    }
    [pscustomobject]@{ second = $i + 1; pid = $sample.playerPid; starts = $sample.starts; attached = $sample.attached; renderer = $sample.renderer }
}
$samples | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $PSScriptRoot 'desktop-stability.json') -Encoding utf8
"PASS: renderer stayed attached without any restart for $Seconds seconds."
