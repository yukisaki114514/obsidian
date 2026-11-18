"""配布zipを作成: python3 build_release.py <version> <出力先dir>
unitypackage の GUID は guids.txt で固定(アップデート時の重複を防ぐ)。"""
import io, os, sys, tarfile, zipfile
HERE = os.path.dirname(os.path.abspath(__file__))
ver, out = sys.argv[1], sys.argv[2]
FOLDER = "folderAsset: yes\nDefaultImporter:\n  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n"
IMP = {".cs": "MonoImporter:\n  externalObjects: {}\n  serializedVersion: 2\n  defaultReferences: []\n  executionOrder: 0\n  icon: {instanceID: 0}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n",
       ".asmdef": "AssemblyDefinitionImporter:\n  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n",
       ".md": "TextScriptImporter:\n  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n"}
guids = dict(l.split() for l in open(os.path.join(HERE, "guids.txt")) if l.strip())
pkg = io.BytesIO()
folder_files = []  # folder_version 用 (同じ GUID の .meta を付ける)
with tarfile.open(fileobj=pkg, mode="w:gz") as tar:
    def add(name, data):
        ti = tarfile.TarInfo(name); ti.size = len(data); ti.mode = 0o644
        tar.addfile(ti, io.BytesIO(data))
    for apath, g in sorted(guids.items()):
        rel = apath[len("Assets/HandGestureCreator"):].lstrip("/")
        src = os.path.join(HERE, rel)
        add(f"{g}/pathname", apath.encode())
        if os.path.isdir(src):
            meta = FOLDER
        else:
            add(f"{g}/asset", open(src, "rb").read())
            meta = IMP[os.path.splitext(src)[1]]
        add(f"{g}/asset.meta", f"fileFormatVersion: 2\nguid: {g}\n{meta}".encode())
        folder_files.append((apath, src, f"fileFormatVersion: 2\nguid: {g}\n{meta}".encode()))
root = f"HandGestureCreator_v{ver}"
zpath = os.path.join(out, root + ".zip")
with zipfile.ZipFile(zpath, "w") as z:
    import time; d = zipfile.ZipInfo(root + "/", time.localtime()[:6]); d.external_attr = 0o40755 << 16 | 0x10
    z.writestr(d, b"")  # stored
    for f in ["README.md", "CHANGELOG.md", "FAQ.txt"]:
        z.write(os.path.join(HERE, f), f"{root}/{f}", compress_type=zipfile.ZIP_DEFLATED)
    z.writestr(f"{root}/HandGestureCreator.unitypackage", pkg.getvalue(), compress_type=zipfile.ZIP_DEFLATED)
    # unitypackage を取り込めないとき用のフォルダ版 (中身・GUID は同じ)
    fv = f"{root}/folder_version/"
    for apath, src, meta in folder_files:
        rel = apath[len("Assets/"):]
        z.writestr(fv + rel + ".meta", meta, compress_type=zipfile.ZIP_DEFLATED)
        if os.path.isdir(src):
            d = zipfile.ZipInfo(fv + rel + "/", time.localtime()[:6]); d.external_attr = 0o40755 << 16 | 0x10
            z.writestr(d, b"")
        else:
            z.write(src, fv + rel, compress_type=zipfile.ZIP_DEFLATED)
print(zpath)
