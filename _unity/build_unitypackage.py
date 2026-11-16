"""FolderLabeler の unitypackage を作る。GUID は guids.json に固定して、更新時も同じ GUID を使う。
使い方: python3 build_unitypackage.py <出力先.unitypackage>"""
import io, json, os, sys, tarfile, time, uuid, gzip

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(HERE, "FolderLabeler")
GUIDS = os.path.join(HERE, "guids.json")
ROOT = "Assets/FolderLabeler"

FOLDER_META = "fileFormatVersion: 2\nguid: {g}\nfolderAsset: yes\nDefaultImporter:\n  externalObjects: {{}}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n"
CS_META = "fileFormatVersion: 2\nguid: {g}\nMonoImporter:\n  externalObjects: {{}}\n  serializedVersion: 2\n  defaultReferences: []\n  executionOrder: 0\n  icon: {{instanceID: 0}}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n"
ASMDEF_META = "fileFormatVersion: 2\nguid: {g}\nAssemblyDefinitionImporter:\n  externalObjects: {{}}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n"
PLAIN_META = "fileFormatVersion: 2\nguid: {g}\n"

def meta_for(path, g, is_dir):
    if is_dir: return FOLDER_META.format(g=g)
    if path.endswith(".cs"): return CS_META.format(g=g)
    if path.endswith(".asmdef"): return ASMDEF_META.format(g=g)
    return PLAIN_META.format(g=g)

def add(tar, name, data, mode=0o644):
    info = tarfile.TarInfo(name)
    info.size = len(data); info.mode = mode; info.mtime = int(time.time())
    tar.addfile(info, io.BytesIO(data))

def add_dir(tar, name):
    info = tarfile.TarInfo(name); info.type = tarfile.DIRTYPE; info.mode = 0o755; info.mtime = int(time.time())
    tar.addfile(info)

def main(out):
    guids = json.load(open(GUIDS)) if os.path.exists(GUIDS) else {}
    items = [(ROOT, None)]
    for dirpath, dirnames, filenames in os.walk(SRC):
        dirnames.sort()
        rel = os.path.relpath(dirpath, SRC).replace(os.sep, "/")
        base = ROOT if rel == "." else ROOT + "/" + rel
        for d in dirnames: items.append((base + "/" + d, None))
        for f in sorted(filenames):
            if f.endswith(".meta") or f.startswith("."): continue
            items.append((base + "/" + f, os.path.join(dirpath, f)))
    buf = io.BytesIO()
    with tarfile.open(fileobj=buf, mode="w", format=tarfile.GNU_FORMAT) as tar:
        for path, src in items:
            g = guids.get(path)
            if not g:
                g = uuid.uuid4().hex; guids[path] = g
            add_dir(tar, g)
            if src: add(tar, g + "/asset", open(src, "rb").read())
            add(tar, g + "/asset.meta", meta_for(path, g, src is None).encode())
            add(tar, g + "/pathname", path.encode())
    with open(out, "wb") as f:
        f.write(gzip.compress(buf.getvalue(), mtime=0))
    json.dump(dict(sorted(guids.items())), open(GUIDS, "w"), indent=2)
    print("wrote", out, len(items), "assets")

if __name__ == "__main__":
    main(sys.argv[1])
