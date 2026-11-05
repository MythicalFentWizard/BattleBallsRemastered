"""Make a COPY of BattleBit's GameAssembly.dll skip the client-side Easy Anti-Cheat check.
Build: Production 2.2.5 Hotfix. Two AntiCheatManager checks (state ok / client ready) are made to return true.
Usage: python patch_gameassembly.py <in GameAssembly.dll> <out GameAssembly.dll>"""
import sys
PATCHES = [  # (file offset, expected original bytes, new bytes)
    (0xdf2f30, bytes.fromhex('40534883ec20803daf9d4803'), bytes.fromhex('b001c3')),  # AntiCheatManager state check
    (0xdf10c0, bytes.fromhex('4883ec28803d22bc48030075'), bytes.fromhex('b001c3')),  # AntiCheatManager client-ready check
]
src, dst = sys.argv[1], sys.argv[2]
d = bytearray(open(src, 'rb').read())
for off, old, new in PATCHES:
    if d[off:off + len(old)] != old:
        raise SystemExit(f'unexpected bytes at {off:#x}: wrong game version or already patched')
    d[off:off + len(new)] = new
    print(f'patched {off:#x}')
open(dst, 'wb').write(d)
