# Capture-only stand-in master. HTTP 29300 (game client), WebSocket 29301 (game server). Logs to master_log.txt
$ErrorActionPreference = 'Continue'
$log = Join-Path $PSScriptRoot 'master_log.txt'
function Log($m) { $line = (Get-Date -Format 'HH:mm:ss ') + $m; Write-Host $line; Add-Content -Path $log -Value $line }
function Hex($b, $o, $n) { if ($n -le 0) { return '' }; return [BitConverter]::ToString($b, $o, $n).Replace('-', '') }
$http = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Any, 29300)
$gs = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Any, 29301)
$http.Start(); $gs.Start()
Log 'stub master v2 up: HTTP 29300, WebSocket 29301. Leave this window open.'
$clients = New-Object System.Collections.ArrayList
while ($true) {
  while ($http.Pending()) { $c = $http.AcceptTcpClient(); [void]$clients.Add(@{c=$c; kind='HTTP'; buf=(New-Object System.IO.MemoryStream)}) }
  while ($gs.Pending()) { $c = $gs.AcceptTcpClient(); [void]$clients.Add(@{c=$c; kind='GS'; buf=(New-Object System.IO.MemoryStream)}); Log "GAMESERVER connect $($c.Client.RemoteEndPoint)" }
  foreach ($e in @($clients)) {
    $c = $e.c
    try {
      $n = $c.Available
      if ($n -gt 0) {
        $b = New-Object byte[] $n; $r = $c.GetStream().Read($b, 0, $n); $e.buf.Write($b, 0, $r)
        $all = $e.buf.ToArray()
        if ($e.kind -eq 'WS') {
          # decode websocket frames from the game server
          $p = 0
          while ($all.Length - $p -ge 2) {
            $op = $all[$p] -band 0x0F; $masked = ($all[$p+1] -band 0x80) -ne 0; $len = $all[$p+1] -band 0x7F; $h = 2
            if ($len -eq 126) { if ($all.Length - $p -lt 4) { break }; $len = $all[$p+2] * 256 + $all[$p+3]; $h = 4 }
            elseif ($len -eq 127) { if ($all.Length - $p -lt 10) { break }; $len = 0; for ($i = 2; $i -lt 10; $i++) { $len = $len * 256 + $all[$p+$i] }; $h = 10 }
            $mk = $p + $h; if ($masked) { $h += 4 }
            if ($all.Length - $p -lt $h + $len) { break }
            $pl = New-Object byte[] $len
            for ($i = 0; $i -lt $len; $i++) { $v = $all[$p+$h+$i]; if ($masked) { $v = $v -bxor $all[$mk + ($i % 4)] }; $pl[$i] = $v }
            Log ("GAMESERVER ws frame op=$op len=$len " + (Hex $pl 0 $len))
            if ($op -eq 9) { $pong = [byte[]](@(0x8A, $len) + $pl); $c.GetStream().Write($pong, 0, $pong.Length) }
            $p += $h + $len
          }
          $rest = New-Object System.IO.MemoryStream; $rest.Write($all, $p, $all.Length - $p); $e.buf = $rest
          continue
        }
        $txt = [Text.Encoding]::ASCII.GetString($all); $hEnd = $txt.IndexOf("`r`n`r`n")
        if ($hEnd -lt 0) { continue }
        $len = 0; if ($txt -match '(?im)^Content-Length:\s*(\d+)') { $len = [int]$Matches[1] }
        if ($all.Length -lt $hEnd + 4 + $len) { continue }
        $head = $txt.Substring(0, $hEnd)
        if ($e.kind -eq 'GS' -and $head -match '(?im)^Sec-WebSocket-Key:\s*(\S+)') {
          $sha = [System.Security.Cryptography.SHA1]::Create()
          $acc = [Convert]::ToBase64String($sha.ComputeHash([Text.Encoding]::ASCII.GetBytes($Matches[1] + '258EAFA5-E914-47DA-95CA-C5AB0DC85B11')))
          $resp = [Text.Encoding]::ASCII.GetBytes("HTTP/1.1 101 Switching Protocols`r`nUpgrade: websocket`r`nConnection: Upgrade`r`nSec-WebSocket-Accept: $acc`r`n`r`n")
          $c.GetStream().Write($resp, 0, $resp.Length)
          Log ("GAMESERVER websocket handshake`r`n" + $head)
          $e.kind = 'WS'; $rest = New-Object System.IO.MemoryStream; $rest.Write($all, $hEnd + 4, $all.Length - $hEnd - 4); $e.buf = $rest
          continue
        }
        Log ("HTTP REQUEST`r`n" + $head + "`r`nBODY($len) " + (Hex $all ($hEnd + 4) $len))
        $resp = [Text.Encoding]::ASCII.GetBytes("HTTP/1.1 200 OK`r`nContent-Length: 0`r`nConnection: close`r`n`r`n")
        $c.GetStream().Write($resp, 0, $resp.Length); $c.Close(); $clients.Remove($e)
      } elseif ($c.Client.Poll(0, [System.Net.Sockets.SelectMode]::SelectRead) -and $c.Available -eq 0) { if ($e.kind -ne 'HTTP') { Log "$($e.kind) closed by other side" }; $c.Close(); $clients.Remove($e) }
    } catch { Log "$($e.kind) error $_"; $clients.Remove($e) }
  }
  Start-Sleep -Milliseconds 20
}
