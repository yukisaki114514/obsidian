import tarfile, os, sys, shutil
pkg, out = sys.argv[1], sys.argv[2]
t = tarfile.open(pkg, 'r:gz')
items = {}
for m in t.getmembers():
    parts = m.name.strip('./').split('/')
    if len(parts) != 2: continue
    items.setdefault(parts[0], {})[parts[1]] = m
for guid, d in items.items():
    path = t.extractfile(d['pathname']).read().decode().splitlines()[0]
    dst = os.path.join(out, path)
    if 'asset' in d:
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        open(dst, 'wb').write(t.extractfile(d['asset']).read())
    else:
        os.makedirs(dst, exist_ok=True)
    open(dst + '.meta', 'w').write('fileFormatVersion: 2\nguid: %s\n' % guid)
