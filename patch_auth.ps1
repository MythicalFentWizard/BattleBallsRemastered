# Makes the client stop gating on master-server auth state.
# These are static bool property getters; each is replaced with a constant return.
#   b0 01 c3 = mov al,1 ; ret   (true)
#   31 c0 c3 = xor eax,eax; ret (false)
# Offsets are file offsets from Il2CppDumper for build "Production 2.2.5 Hotfix".
# Restore with: copy GameAssembly.dll.orig over GameAssembly.dll

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
    Write-Output '  .\patch_auth.ps1 -GamePath "C:\SteamLibrary\steamapps\common\BattleBit Remastered"'
    exit 1
}
Write-Output "Game folder: $gameRoot"


$dll = Join-Path $gameRoot "GameAssembly.dll"

$ret_true  = [byte[]](0xB0, 0x01, 0xC3)
$ret_false = [byte[]](0x31, 0xC0, 0xC3)

# The bool getters all start with the same class-init prologue.
$getterPrologue = [byte[]](0x48, 0x83, 0xEC, 0x28, 0x80, 0x3D)

# The server-key validator is a full function, so it has its own prologue:
#   mov [rsp+10],rdx ; push rbp ; push rdi ; push r13 ; push r14 ; push r15
$validatorPrologue = [byte[]](0x48, 0x89, 0x54, 0x24, 0x10, 0x55, 0x57, 0x41, 0x55)

$patches = @(
    @{ Name = "get_HasToken";                Offset = 0x13B4AF0; Bytes = $ret_true;  Expect = $getterPrologue },
    @{ Name = "get_IsAccountNew";            Offset = 0x13B4B50; Bytes = $ret_false; Expect = $getterPrologue },
    @{ Name = "get_IsSteamDown";             Offset = 0x13B4CB0; Bytes = $ret_false; Expect = $getterPrologue },
    @{ Name = "get_FamilyShareNotSupported"; Offset = 0x13B4A90; Bytes = $ret_false; Expect = $getterPrologue },
    @{ Name = "get_UnableToRetriveTicket";   Offset = 0x13B4E10; Bytes = $ret_false; Expect = $getterPrologue },

    # Server-key check, used by BOTH sides (client and server share this binary).
    # Original does Aes.Create(), transforms the 16-byte key, BitConverter.ToUInt64 and
    # compares against a challenge ulong. We cannot reproduce that without the mode, IV
    # and plaintext, so the gate is removed and access control lives in the master server
    # instead (password-gated cluster, and it is the only source of server listings).
    @{ Name = "ServerKeyValidator (AES)";    Offset = 0x11D1100; Bytes = $ret_true;  Expect = $validatorPrologue },

    # Rich-text tag stripper (RVA 0x2D761D0), called on the player name from
    # Networking.ServerHandler+<OnClientConnecting>. It throws NullReferenceException on
    # a null string, which kills the connection coroutine and fails every join with
    # NoResponse. The name comes from PlayerStats.MasterServerUsername (offset 0x58),
    # which is null whenever the server takes the path that skips the master-server
    # stats request.
    #
    # The null branch is redirected from the throw helper (0x2D76350) to the loop exit
    # (0x2D762EE). The static StringBuilder is already cleared by that point, so a null
    # name now yields an empty string instead of an exception.
    #   0F 84 F0 00 00 00 = je 0x2D76350 (throw)
    #   0F 84 8E 00 00 00 = je 0x2D762EE (loop exit)
    # String reads from the master server (RVA 0x236BC70) bounds-check the read position
    # against a limit field at [stream+0x20] rather than the buffer length, and that
    # field is not valid on an incoming registry stream - so EVERY string the master
    # sends is dropped, silently, by returning null instead of throwing. The sibling
    # readers (bool/u16/u32/u64) check [array+0x18] instead, which is why numeric stats
    # arrive correctly and only strings vanish.
    #
    # Both guards are short jumps to the return-null path:
    #   7C 4D = jl ret_null  (limit - readPos < 2)
    #   7C 2E = jl ret_null  (remaining < declared length)
    #
    # The first is NOPed. The surviving check at 0x236AEA0 (cmp ecx,[rbx+0x18]) bounds
    # the start of the 2-byte prefix read but not its end, so a read position of
    # len-1 reads one byte past the array. Restoring a correct check there does not fit
    # in the available bytes, and the master pads every reply, so it is left as is.
    @{ Name = "ReadString limit guard"; Offset = 0x236AE95
       Bytes  = [byte[]](0x90, 0x90)
       Expect = [byte[]](0x7C, 0x4D) },

    # The second guard was the ONLY check that the declared string length fits in the
    # buffer. The two array checks around it (cmp against [rbx+0x18]) each compare a
    # POSITION against the length - the start of the read, and the position after the
    # prefix - and neither accounts for the declared length that follows. So NOPing
    # this one left the copy below bounded by nothing but an attacker-supplied uint16:
    # up to 64KB read past the array and handed back as a string. Only reachable from
    # master-server data (PlayerStats.ReadFrom_MasterServer_GameServerSide and four
    # handlers in the master-connection class), so the attacker is a hostile or spoofed
    # master rather than another player - but it reads the victim's heap, which is more
    # than a server operator should be able to do.
    #
    # Rather than restore the original check against the invalid limit field, this
    # rebuilds it against the real array length, reusing the 14 bytes from the dead
    # "sub edx,esi" through the now-redundant second array check:
    #
    #   original: sub edx,esi / mov [rdi+24],esi / cmp edx,ebp / jl ret_null
    #             cmp esi,[rbx+18] / jae throw
    #   patched : mov [rdi+24],esi / lea eax,[rsi+rbp] / cmp eax,[rbx+18]
    #             ja ret_null / nop nop nop
    #
    # esi is the read position after the prefix and ebp the declared length, both
    # zero-extended, so eax is their exact sum and the unsigned compare permits
    # esi+ebp == length. The dropped "cmp esi,[rbx+18]" is subsumed: if esi+ebp fits
    # then so does esi. edx is dead here - rdx is fully overwritten by the lea before
    # the copy. Out-of-bounds now returns null, which the sanitizer patch below turns
    # into an empty string, instead of throwing and killing the join coroutine.
    #
    # Expect covers a pristine build; ExpectAlt covers one where an earlier version of
    # this script already NOPed the guard.
    @{ Name = "ReadString length bound"; Offset = 0x236AEAD
       Verb   = "bounds check restored"
       Bytes  = [byte[]](0x89, 0x77, 0x24,
                         0x8D, 0x04, 0x2E,
                         0x3B, 0x43, 0x18,
                         0x77, 0x2C,
                         0x90, 0x90, 0x90)
       Expect    = [byte[]](0x2B, 0xD6, 0x89, 0x77, 0x24, 0x3B, 0xD5, 0x7C, 0x2E,
                            0x3B, 0x73, 0x18, 0x73, 0x50)
       ExpectAlt = [byte[]](0x2B, 0xD6, 0x89, 0x77, 0x24, 0x3B, 0xD5, 0x90, 0x90,
                            0x3B, 0x73, 0x18, 0x73, 0x50) },

    # Force the player-name read to a known position.
    #
    # ReadFrom_MasterServer_GameServerSide reads the username LAST, after a chain of
    # sub-readers whose widths all verify individually but whose total does not land
    # where it should: an exhaustive scan showed the cursor is nowhere in payload
    # 188-800, so something upstream moves it unpredictably. Rather than keep guessing
    # which field, this overwrites the Role read (8 bytes we do not use) with an
    # instruction that sets the stream's read position immediately before the string
    # is read.
    #
    # The value is buffer-relative, not body-relative: the stream buffer holds the
    # WHOLE packet including the 9-byte header (uint64 sequence + type byte) that the
    # dispatcher has already consumed, so the body starts at buffer position 9. Being
    # 9 short is why every offset tried from the master side missed.
    #
    # 218 = body 209 + 9. This constant and PlayerStats.Write have to change together;
    # if the name comes back empty after editing one of them, they have drifted apart.
    #
    # It was briefly moved to 216, on the mistaken belief that the PlayerProgess length
    # prefix was a ushort byte length. It is a uint32 count - the ushort form belongs to
    # Achievements - so the body offset never moved and neither should this.
    #
    #   original: xor edx,edx / mov rcx,rbx / call ReadUInt64 / xor edx,edx / mov [rbp+50],rax
    #   patched : mov dword ptr [rbx+24], 218 / xor edx,edx / nop padding
    #
    # rbx holds the stream and [rbx+0x24] is its read cursor (confirmed across every
    # reader helper). The xor edx,edx is preserved because ReadString takes it as its
    # second argument. Role is left at its constructed default of 0.
    # ExpectAlt is the same patch with 216 (0xD8), so an install patched during the
    # window when that was briefly believed correct is moved back to 218 rather than
    # reported as already done - which would leave it reading two bytes short.
    @{ Name = "NameReadPosition"; Offset = 0x12A1DE2
       Bytes  = [byte[]](0xC7, 0x43, 0x24, 0xDA, 0x00, 0x00, 0x00,
                         0x33, 0xD2,
                         0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90)
       Expect    = [byte[]](0x33, 0xD2, 0x48, 0x8B, 0xCB, 0xE8, 0xF4, 0x70, 0x0C, 0x01,
                            0x33, 0xD2, 0x48, 0x89, 0x45, 0x50)
       ExpectAlt = [byte[]](0xC7, 0x43, 0x24, 0xD8, 0x00, 0x00, 0x00,
                            0x33, 0xD2,
                            0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90) },

    @{ Name = "NameSanitizer null guard"; Offset = 0x2D7545A
       Bytes  = [byte[]](0x0F, 0x84, 0x8E, 0x00, 0x00, 0x00)
       Expect = [byte[]](0x0F, 0x84, 0xF0, 0x00, 0x00, 0x00) }
)

if (-not (Test-Path "$dll.orig")) {
    Copy-Item $dll "$dll.orig"
    Write-Output "saved backup -> GameAssembly.dll.orig"
}

$d = [System.IO.File]::ReadAllBytes($dll)
$applied = 0

foreach ($p in $patches) {
    $off = $p.Offset

    # A patch may accept more than one pre-state: the ReadString bound replaces a
    # region that an earlier version of this script already altered, so a half-patched
    # install has to be recognised too.
    $accept = New-Object System.Collections.ArrayList
    [void]$accept.Add($p.Expect)
    if ($p.ContainsKey('ExpectAlt')) { [void]$accept.Add($p.ExpectAlt) }

    $matches = $false
    foreach ($e in $accept) {
        $actual = $d[$off..($off + $e.Length - 1)]
        if (-not (Compare-Object $actual $e -SyncWindow 0)) { $matches = $true; break }
    }

    # Compared against the replacement, so a patch that is not a constant-return
    # prologue is still recognised as already applied.
    $current = $d[$off..($off + $p.Bytes.Length - 1)]
    $isPatched = -not (Compare-Object $current $p.Bytes -SyncWindow 0)

    if ($matches) {
        for ($i = 0; $i -lt $p.Bytes.Length; $i++) { $d[$off + $i] = $p.Bytes[$i] }
        $verb = if ($p.ContainsKey('Verb')) { $p.Verb }
                elseif ($p.Bytes[0] -eq 0xB0) { "returns true" }
                elseif ($p.Bytes[0] -eq 0x31) { "returns false" }
                else { "branch redirected" }
        "patched {0,-30} @0x{1:X7} -> {2}" -f $p.Name, $off, $verb
        $applied++
    }
    elseif ($isPatched) {
        "already patched {0,-22} @0x{1:X7}" -f $p.Name, $off
    }
    else {
        "SKIPPED {0,-29} @0x{1:X7} unexpected bytes - wrong build?" -f $p.Name, $off
    }
}

if ($applied -gt 0) {
    # The write is the only step that can fail after the patches are staged in memory,
    # and it fails for a predictable reason: the game or a server is still running and
    # holds the DLL open. Reporting success anyway is worse than useless here - these
    # patches include a bounds check, so "patched" has to mean the bytes are on disk.
    try {
        [System.IO.File]::WriteAllBytes($dll, $d)
    }
    catch {
        Write-Output ""
        Write-Warning "FAILED to write GameAssembly.dll - nothing has changed on disk."
        Write-Warning $_.Exception.Message
        $holding = Get-Process -ErrorAction SilentlyContinue |
            Where-Object { $_.ProcessName -in @("BattleBit", "RevivalServer", "RevivalLauncher") }
        if ($holding) {
            Write-Warning ("Close these first, then re-run: " +
                           (($holding | ForEach-Object { "$($_.ProcessName) (pid $($_.Id))" }) -join ", "))
        } else {
            Write-Warning "Check that the file is not read-only and that you can write to the game folder."
        }
        exit 1
    }
    Write-Output ""
    Write-Output "$applied patch(es) written to GameAssembly.dll"
} else {
    Write-Output ""
    Write-Output "nothing to do"
}
