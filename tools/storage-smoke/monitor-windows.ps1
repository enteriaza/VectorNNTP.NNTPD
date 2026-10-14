#Requires -Version 5.1
<#
.SYNOPSIS
    Samples a running Windows StorageServer into the smoke/soak results directory.

.DESCRIPTION
    Writes counters.csv next to client.csv. This script does not read the SQLite index and does not
    change server behaviour. Spool publication and index figures are copied from the latest matching
    lines in the last 2 MiB of -LogPath. Those cells are blank when the line is absent, including
    after the server log rolls to a new file. Pass the log file that is currently being written.

    diskActivePct is Windows \PhysicalDisk(n)\% Disk Time. That counter is
    100 * transfers/sec * average seconds/transfer. It is not a 0-100 utilization percentage and it
    can exceed 100. diskQueue is the instantaneous Current Disk Queue Length. diskLatencyMs is
    Avg. Disk sec/Write multiplied by 1000, from the same spot sample, not an average over the
    script interval. diskReadBps and diskWriteBps are bytes per second from that spot sample.

    The utc column is the sample time in UTC (ISO-8601). It is not the timestamp printed in the
    server log line. Server log lines keep the server's local clock.

    stored, storedBytes, queued, queuedBytes, and reservedBytes come from the spool persistence
    summary. queued is the queued article count from that line. deferred and activeWorkers are
    present in the log line and are not columns here.

    indexInserts, indexFailures, indexWalBytes, and indexMembership come from the article index
    summary. indexFailures is the log's failures field. retries is not a column.

    The script writes stop.request and monitor-stop.txt when the server process is gone or free
    bytes on the spool volume fall below -StopBelowFreeBytes. It also stops when client.pid exists
    and that process is gone. It does not delete results.
#>
param(
    [Parameter(Mandatory = $true)][int]$ServerPid,
    [Parameter(Mandatory = $true)][string]$Results,
    [Parameter(Mandatory = $true)][string]$SpoolPath,
    [Parameter(Mandatory = $true)][string]$LogPath,
    [int]$IntervalSeconds = 45,
    [long]$StopBelowFreeBytes = 500GB,
    [string]$DiskInstance = '0'
)

$ErrorActionPreference = 'Stop'
if ($IntervalSeconds -lt 1) { throw 'IntervalSeconds must be at least 1.' }
if (-not (Test-Path -LiteralPath $Results -PathType Container)) { throw "Results directory does not exist: $Results" }
if (-not (Test-Path -LiteralPath $SpoolPath)) { throw "Spool path does not exist: $SpoolPath" }
if (-not (Test-Path -LiteralPath $LogPath -PathType Leaf)) { throw "Log file does not exist: $LogPath" }

$counters = Join-Path $Results 'counters.csv'
if (-not (Test-Path -LiteralPath $counters)) {
    'utc,serverAlive,cpuSeconds,workingSet,availableMB,diskReadBps,diskWriteBps,diskActivePct,diskQueue,diskLatencyMs,spoolFreeBytes,stored,storedBytes,queued,queuedBytes,reservedBytes,indexInserts,indexMembership,indexWalBytes,indexFailures' |
        Set-Content -LiteralPath $counters -Encoding ascii
}

function Latest-Match([string]$pattern) {
    $stream = [IO.File]::Open($LogPath, 'Open', 'Read', 'ReadWrite')
    try {
        $length = $stream.Length
        $take = [Math]::Min(2097152, $length)
        [void]$stream.Seek($length - $take, 'Begin')
        $reader = New-Object IO.StreamReader($stream)
        try {
            $text = $reader.ReadToEnd()
        } finally {
            $reader.Dispose()
        }

        $found = ''
        foreach ($line in ($text -split "`n")) {
            if ($line -match $pattern) { $found = $line.TrimEnd("`r") }
        }

        return $found
    } finally {
        if ($stream) { $stream.Dispose() }
    }
}

while ($true) {
    $process = Get-Process -Id $ServerPid -ErrorAction SilentlyContinue
    $alive = $null -ne $process
    $cpu = if ($process) { [math]::Round($process.CPU, 1) } else { '' }
    $workingSet = if ($process) { $process.WorkingSet64 } else { '' }
    $operatingSystem = Get-CimInstance Win32_OperatingSystem
    $availableMb = [int]($operatingSystem.FreePhysicalMemory / 1024)
    $readBytesPerSecond = 0
    $writeBytesPerSecond = 0
    $diskActive = 0
    $diskQueue = 0
    $latencyMs = 0
    $disk = Get-Counter `
        "\PhysicalDisk($DiskInstance)\Disk Read Bytes/sec", `
        "\PhysicalDisk($DiskInstance)\Disk Write Bytes/sec", `
        "\PhysicalDisk($DiskInstance)\% Disk Time", `
        "\PhysicalDisk($DiskInstance)\Current Disk Queue Length", `
        "\PhysicalDisk($DiskInstance)\Avg. Disk sec/Write" `
        -ErrorAction SilentlyContinue
    if ($disk) {
        foreach ($sample in $disk.CounterSamples) {
            switch -Wildcard ($sample.Path) {
                '*Disk Read Bytes/sec' { $readBytesPerSecond = [int64]$sample.CookedValue }
                '*Disk Write Bytes/sec' { $writeBytesPerSecond = [int64]$sample.CookedValue }
                '*% Disk Time' { $diskActive = [math]::Round($sample.CookedValue, 2) }
                '*Current Disk Queue Length' { $diskQueue = [math]::Round($sample.CookedValue, 3) }
                '*Avg. Disk sec/Write' { $latencyMs = [math]::Round($sample.CookedValue * 1000, 3) }
            }
        }
    }

    $free = (Get-Volume -FilePath $SpoolPath).SizeRemaining
    $stored = ''
    $storedBytes = ''
    $queued = ''
    $queuedBytes = ''
    $reservedBytes = ''
    $persist = Latest-Match 'Spool persistence stored='
    if ($persist -match 'stored=(\d+) storedBytes=(\d+) deferred=(\d+) activeWorkers=(\d+) queued=(\d+) queuedBytes=(\d+) reservedBytes=(\d+)') {
        $stored = $Matches[1]
        $storedBytes = $Matches[2]
        $queued = $Matches[5]
        $queuedBytes = $Matches[6]
        $reservedBytes = $Matches[7]
    }

    $inserts = ''
    $membership = ''
    $walBytes = ''
    $indexFailures = ''
    $index = Latest-Match 'Article index inserts='
    if ($index -match 'inserts=(\d+).*failures=(\d+).*walBytes=(\d+) membership=(\d+)') {
        $inserts = $Matches[1]
        $indexFailures = $Matches[2]
        $walBytes = $Matches[3]
        $membership = $Matches[4]
    }

    $utc = [DateTime]::UtcNow.ToString('o')
    "$utc,$alive,$cpu,$workingSet,$availableMb,$readBytesPerSecond,$writeBytesPerSecond,$diskActive,$diskQueue,$latencyMs,$free,$stored,$storedBytes,$queued,$queuedBytes,$reservedBytes,$inserts,$membership,$walBytes,$indexFailures" |
        Add-Content -LiteralPath $counters -Encoding ascii

    if (-not $alive -or $free -lt $StopBelowFreeBytes) {
        New-Item -ItemType File -Path (Join-Path $Results 'stop.request') -Force | Out-Null
        "stop utc=$utc alive=$alive freeBytes=$free" | Add-Content -LiteralPath (Join-Path $Results 'monitor-stop.txt') -Encoding ascii
        break
    }

    $clientPidPath = Join-Path $Results 'client.pid'
    if (Test-Path -LiteralPath $clientPidPath) {
        $clientPid = [int](Get-Content -LiteralPath $clientPidPath)
        if (-not (Get-Process -Id $clientPid -ErrorAction SilentlyContinue)) { break }
    }

    Start-Sleep -Seconds $IntervalSeconds
}
