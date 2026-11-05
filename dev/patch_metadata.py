import struct, sys
ORIG_API = b'https://api.battlebit.cloud/'
ORIG_GS = b'ws://server.battlebit.cloud/GameServers'
def patch(src, dst, api=b'http://127.0.0.1:29300/', gs=b'ws://127.0.0.1:29301/GameServers'):
    d = bytearray(open(src, 'rb').read())
    h = struct.unpack_from('<66I', d, 0)
    lit_off, lit_size, data_off = h[2], h[3], h[4]
    todo = {ORIG_API: api, ORIG_GS: gs}
    for k in range(lit_size // 8):
        ln, di = struct.unpack_from('<Ii', d, lit_off + 8 * k)
        s = bytes(d[data_off + di:data_off + di + ln])
        if s in todo:
            new = todo.pop(s)
            d[data_off + di:data_off + di + ln] = new + b'\0' * (ln - len(new))
            struct.pack_into('<I', d, lit_off + 8 * k, len(new))
            print(f'literal {k}: {s.decode()} -> {new.decode()}')
    if todo: raise SystemExit(f'not found: {list(todo)}')
    open(dst, 'wb').write(d)
if __name__ == '__main__':
    a = sys.argv[1:]; patch(a[0], a[1], *(x.encode() for x in a[2:4]))
