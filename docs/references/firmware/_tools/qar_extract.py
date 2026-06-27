import sys, zlib, struct, os, json

def extract(qar_path, out_dir):
    data = open(qar_path,'rb').read()
    if data[:2] != b'q\x02':
        return None, "bad magic %r" % data[:4]
    count = struct.unpack_from('<H', data, 2)[0]   # entry count
    pos = 4
    files = []
    n = len(data)
    for _ in range(count):
        if pos + 8 > n: break
        comp_size = struct.unpack_from('<I', data, pos)[0]; pos += 4
        unk       = struct.unpack_from('<H', data, pos)[0]; pos += 2
        namelen   = struct.unpack_from('<H', data, pos)[0]; pos += 2
        if namelen == 0 or namelen > 8192 or pos+namelen > n:
            return files, "stop: bad namelen %d at pos %d" % (namelen, pos)
        name = data[pos:pos+namelen].decode('utf-8','replace'); pos += namelen
        blob = data[pos:pos+comp_size]; pos += comp_size
        try:
            raw = zlib.decompress(blob)
        except Exception as e:
            return files, "zlib fail on %s: %s" % (name, e)
        files.append((name, len(raw), raw))
        if out_dir:
            dst = os.path.join(out_dir, name)
            os.makedirs(os.path.dirname(dst), exist_ok=True)
            open(dst,'wb').write(raw)
    return files, None

if __name__ == '__main__':
    qar, out = sys.argv[1], (sys.argv[2] if len(sys.argv)>2 else None)
    files, err = extract(qar, out)
    if files is None: print("FAIL:", err); sys.exit(1)
    print("extracted %d entries (err=%s)" % (len(files), err))
