# Sweeps the token status byte, restarting the client whenever it stalls on a terminal
# state (e.g. "Steam Is Down"), which is what stopped the earlier blind sweep.
# Success shows up as the client reaching an endpoint beyond Tokens/GetToken.
$root   = "G:\SteamLibrary\steamapps\common\BattleBit Revival"
$rev    = Join-Path $root "_revival"
$master = Join-Path $rev "MasterServer\bin\Release\net10.0\MasterServer.exe"
$game   = Join-Path $root "BattleBit.exe"
$mlog   = Join-Path $rev "master_log.txt"
$clog   = Join-Path $rev "client_log.txt"
$report = Join-Path $rev "status_probe_results.txt"

function Kill-All {
    Get-Process MasterServer, BattleBit -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
}

function Count-Tokens {
    if (-not (Test-Path $mlog)) { return 0 }
    return @(Select-String -Path $mlog -Pattern "Tokens/GetToken" -ErrorAction SilentlyContinue).Count
}

Kill-All
Start-Sleep -Milliseconds 800
Remove-Item $mlog, $clog -ErrorAction SilentlyContinue

Start-Process -FilePath $master -ArgumentList "--local-only","--sweep-status" `
    -WorkingDirectory (Split-Path $master) -WindowStyle Hidden
Start-Sleep -Seconds 3

"=== sweep with client watchdog ===" | Set-Content $report

$deadline  = (Get-Date).AddMinutes(25)
$lastCount = 0
$lastMove  = Get-Date
$restarts  = 0
Start-Process -FilePath $game -ArgumentList "-logFile",$clog -WorkingDirectory $root -WindowStyle Minimized

while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 5
    $n = Count-Tokens

    # Did the client get further than the token endpoint? That is the success signal.
    $beyond = @(Select-String -Path $mlog -Pattern "HTTP POST /(Servers|Player|News|Matchmaking|Inventory)/" -ErrorAction SilentlyContinue)
    if ($beyond.Count -gt 0) {
        $statuses = @(Select-String -Path $mlog -Pattern "status=0x[0-9A-F]{2}" -ErrorAction SilentlyContinue)
        "BREAKTHROUGH after $($statuses.Count) statuses" | Add-Content $report
        $beyond | Select-Object -First 10 | ForEach-Object { $_.Line } | Add-Content $report
        Write-Output "BREAKTHROUGH: client reached $($beyond.Count) post-token endpoint hits"
        break
    }

    if ($n -gt $lastCount) {
        $lastCount = $n
        $lastMove  = Get-Date
        Write-Output "tokens requested: $n"
    }
    elseif (((Get-Date) - $lastMove).TotalSeconds -gt 25) {
        # Client stalled on a terminal state; restart it so the sweep keeps moving.
        $restarts++
        Write-Output "client stalled at $n requests - restart #$restarts"
        Get-Process BattleBit -ErrorAction SilentlyContinue |
            Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
        Start-Process -FilePath $game -ArgumentList "-logFile",$clog -WorkingDirectory $root -WindowStyle Minimized
        $lastMove = Get-Date
    }
}

"statuses tried: $(@(Select-String -Path $mlog -Pattern 'status=0x[0-9A-F]{2}').Count)" | Add-Content $report
"client restarts: $restarts" | Add-Content $report
Kill-All
Write-Output "DONE"
