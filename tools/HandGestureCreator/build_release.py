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
root = f"HandGestureCreator_v{ver}"
zpath = os.path.join(out, root + ".zip")
with zipfile.ZipFile(zpath, "w") as z:
    import time; d = zipfile.ZipInfo(root + "/", time.localtime()[:6]); d.external_attr = 0o40755 << 16 | 0x10
    z.writestr(d, b"")  # stored
    for f in ["README.md", "CHANGELOG.md", "FAQ.txt"]:
        z.write(os.path.join(HERE, f), f"{root}/{f}", compress_type=zipfile.ZIP_DEFLATED)
    z.writestr(f"{root}/HandGestureCreator.unitypackage", pkg.getvalue(), compress_type=zipfile.ZIP_DEFLATED)
print(zpath)
