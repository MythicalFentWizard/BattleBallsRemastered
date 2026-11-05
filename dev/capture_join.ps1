# Listens on the port the master advertises in place of the real one, so the client's
# genuine Game_Hail join packet lands here and can be dumped byte for byte.
# Run this, then click the server in the browser.

# Well clear of the game server: it binds Port AND Port+1, so 30001 kills its startup.
$port = 31500
$out  = Join-Path $PSScriptRoot "join_capture.txt"
"=== listening on UDP $port ===" | Set-Content $out

$udp = New-Object System.Net.Sockets.UdpClient $port
$udp.Client.ReceiveTimeout = 900000   # 15 min: must outlive client startup + your click
$ep  = New-Object System.Net.IPEndPoint ([System.Net.IPAddress]::Any), 0

Write-Output "Listening on UDP $port. Click the server in the browser now."
Write-Output "Writing to $out"

for ($n = 1; $n -le 12; $n++) {
    try { $data = $udp.Receive([ref]$ep) }
    catch { Write-Output "timeout - stopping"; break }

    $hex = ($data | ForEach-Object { $_.ToString('X2') }) -join ' '
    $asc = -join ($data | ForEach-Object { if ($_ -ge 32 -and $_ -le 126) { [char]$_ } else { '.' } })

    $type = $data[0]
    $name = switch ($type) {
        127 {"Game_Hail"} 128 {"Browser_ServerInfoRequest"} 129 {"Browser_PingRequest"}
        240 {"MTUDiscovery_Request"} 251 {"SlotReserving_ReserveSlot"} default {"type $type"}
    }

    $msg = "[$n] from $ep  len=$($data.Length)  $name"
    Write-Output $msg
    Add-Content $out $msg
    Add-Content $out "hex:   $hex"
    Add-Content $out "ascii: $asc"
    Add-Content $out ""
}
$udp.Close()
Write-Output "done - see $out"
