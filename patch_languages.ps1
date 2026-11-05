# Localization is downloaded from languages.battlebit.cloud, which no longer resolves, so
# every lookup falls back to "Missing [id]" even though English.lang sits next to the exe.
#
# Patch the STRING LITERAL TABLE only, updating each entry's length field. An earlier
# attempt also rewrote the copy in the const-field data further down the file; that stopped
# the client requesting localization at all, so the const copy is deliberately left alone.
#
# Restores from the backup first so repeated runs always start from a known state.


param(
    # Your BattleBit folder. Leave it out and the script looks in the folders above
    # itself, which is right when it sits inside the game install or in a box next to it.
    [string]$GamePath
)

function Find-GameRoot {
    param([string]$Explicit)
    if ($Explicit) {
        if (Test-Path (Join-Path $Explicit "GameAssembly.dll")) { return $Explicit }
        # Write-Warning, not Write-Output: anything written to the output stream
        # inside a function becomes part of its return value, so this message would
        # otherwise be handed back AS the path.
        Write-Warning "No GameAssembly.dll in: $Explicit"
        return $null
    }
    $dir = $PSScriptRoot
    for ($i = 0; $i -lt 8 -and $dir; $i++) {
        if (Test-Path (Join-Path $dir "GameAssembly.dll")) { return $dir }
        $dir = Split-Path $dir -Parent
    }
    return $null
}

$gameRoot = Find-GameRoot $GamePath
if (-not $gameRoot) {
    Write-Output ""
    Write-Output "Could not find your BattleBit folder."
    Write-Output "Run it with the path, for example:"
    Write-Output '  .\patch_languages.ps1 -GamePath "C:\SteamLibrary\steamapps\common\BattleBit Remastered"'
    exit 1
}
Write-Output "Game folder: $gameRoot"

$meta = Join-Path $gameRoot "BattleBit_Data\il2cpp_data\Metadata\global-metadata.dat"
$bak  = "$meta.revival-bak"

$replacements = @{
    "https://languages.battlebit.cloud/Languages.txt" = "http://127.0.0.1:29300/Languages.txt"
    "https://languages.battlebit.cloud/"              = "http://127.0.0.1:29300/"
}

# NOTE: the real cause of "Missing [id]" everywhere was Servers/GetPingServers throwing
# IndexOutOfRange on an empty reply, which aborted the startup path that loads the local
# English.lang. With that fixed the client never fetches localization over HTTP at all.
# This redirect is only a fallback for builds that do fetch it.

if (-not (Test-Path $bak)) {
    Copy-Item $meta $bak
    Write-Output "backup -> global-metadata.dat.revival-bak"
}

$d = [System.IO.File]::ReadAllBytes($meta)

# Header is 66 uint32s; indexes 2/3 locate the literal table, index 4 the string data.
$litOff  = [BitConverter]::ToUInt32($d, 2 * 4)
$litSize = [BitConverter]::ToUInt32($d, 3 * 4)
$dataOff = [BitConverter]::ToUInt32($d, 4 * 4)

$patched = 0
for ($k = 0; $k -lt [int]($litSize / 8); $k++) {
    $entry = $litOff + 8 * $k
    $len   = [BitConverter]::ToUInt32($d, $entry)
    $di    = [BitConverter]::ToInt32($d, $entry + 4)
    if ($len -eq 0 -or $len -gt 512) { continue }

    $s = [System.Text.Encoding]::UTF8.GetString($d, $dataOff + $di, $len)
    if (-not $replacements.ContainsKey($s)) { continue }

    $new   = $replacements[$s]
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($new)
    if ($bytes.Length -gt $len) { Write-Output "SKIP (too long): $s"; continue }

    # Overwrite in place and null-pad, then shorten the recorded length to match.
    for ($i = 0; $i -lt $len; $i++) {
        $d[$dataOff + $di + $i] = if ($i -lt $bytes.Length) { $bytes[$i] } else { 0 }
    }
    [BitConverter]::GetBytes([uint32]$bytes.Length).CopyTo($d, $entry)

    Write-Output "patched literal ${k}: $s -> $new"
    $patched++
}

if ($patched -gt 0) {
    [System.IO.File]::WriteAllBytes($meta, $d)
    Write-Output ""
    Write-Output "$patched literal(s) redirected"
} else {
    Write-Output ""
    Write-Output "nothing patched - literals not found"
}
