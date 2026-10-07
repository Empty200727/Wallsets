$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$engine = Join-Path $root 'Wallsets\engine\mpv.exe'
$pipeName = 'wallsets-media-probe'
$proc = Start-Process -FilePath $engine -ArgumentList @('--no-config', '--idle=yes', '--pause=yes', '--vo=null', '--ao=null', '--keep-open=yes', '--terminal=no', "--input-ipc-server=$pipeName") -WindowStyle Hidden -PassThru
function Command([object[]]$command) {
    $pipe = [System.IO.Pipes.NamedPipeClientStream]::new('.', $pipeName, [System.IO.Pipes.PipeDirection]::InOut)
    try {
        $pipe.Connect(3000)
        $writer = [System.IO.StreamWriter]::new($pipe, [System.Text.UTF8Encoding]::new($false), 1024, $true)
        $reader = [System.IO.StreamReader]::new($pipe)
        $writer.AutoFlush = $true
        $writer.WriteLine((@{ command = $command; request_id = 1 } | ConvertTo-Json -Compress -Depth 6))
        do { $reply = $reader.ReadLine() | ConvertFrom-Json } while ($null -eq $reply.request_id)
        if ($reply.error -ne 'success') { return $null }
        return $reply.data
    } finally { $pipe.Dispose() }
}
try {
    Start-Sleep -Milliseconds 500
    $result = foreach ($file in Get-ChildItem -LiteralPath (Join-Path $root ([string][char]0x412 + [char]0x438 + [char]0x434 + [char]0x435 + [char]0x43e)), (Join-Path $root ([string][char]0x412 + [char]0x438 + [char]0x434 + [char]0x435 + [char]0x43e + '2')) -File) {
        $null = Command @('loadfile', $file.FullName, 'replace')
        $duration = $null
        for ($i = 0; $i -lt 60; $i++) {
            Start-Sleep -Milliseconds 100
            if ((Command @('get_property', 'path')) -eq $file.FullName) {
                $duration = Command @('get_property', 'duration')
                $params = Command @('get_property', 'video-params')
                if ($duration -and $params) { break }
            }
        }
        [pscustomobject]@{ Path = $file.FullName; Duration = $duration; Width = $params.w; Height = $params.h; Codec = (Command @('get_property', 'video-codec')) }
    }
    $result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'media-probe.json') -Encoding utf8
    $result | Sort-Object Duration | Select-Object -First 6 Path,Duration,Width,Height | Format-List
    'Probed: ' + $result.Count
    'Failed: ' + @($result | Where-Object { -not $_.Duration -or -not $_.Width }).Count
} finally {
    if (-not $proc.HasExited) { $proc.Kill(); $proc.WaitForExit() }
    $proc.Dispose()
}
