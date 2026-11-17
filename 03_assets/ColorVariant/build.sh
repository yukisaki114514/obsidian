#!/bin/bash
# unitypackage と配布 zip を作る（ShapeFit と同じ tar 構成）。使い方: ./build.sh <version>
set -e; V=$1; cd "$(dirname "$0")"
ST=$(mktemp -d); O=dist/ColorVariant_v$V; rm -rf dist; mkdir -p $O
python3 -I - "$ST" <<'PY'
import os,sys,hashlib
src='src'; st=sys.argv[1]
def guid(p): return hashlib.md5(('Znakisaki.ColorVariant/'+p).encode()).hexdigest()
tail="  userData: \n  assetBundleName: \n  assetBundleVariant: \n"
for root,dirs,files in os.walk(src):
  for n,isdir in [(d,1) for d in dirs]+[(f,0) for f in files]:
    p=os.path.relpath(os.path.join(root,n),src).replace(os.sep,'/')
    if p=='Assets': continue
    g=guid(p); d=os.path.join(st,g); os.makedirs(d)
    if isdir: body="folderAsset: yes\nDefaultImporter:\n  externalObjects: {}\n"
    elif p.endswith('.cs'): body="MonoImporter:\n  externalObjects: {}\n  serializedVersion: 2\n  defaultReferences: []\n  executionOrder: 0\n  icon: {instanceID: 0}\n"
    elif p.endswith('.asmdef'): body="AssemblyDefinitionImporter:\n  externalObjects: {}\n"
    else: body="TextScriptImporter:\n  externalObjects: {}\n"
    open(os.path.join(d,'asset.meta'),'w',newline='\n').write(f"fileFormatVersion: 2\nguid: {g}\n{body}{tail}")
    open(os.path.join(d,'pathname'),'w',newline='').write(p)
    if not isdir: open(os.path.join(d,'asset'),'wb').write(open(os.path.join(src,p),'rb').read())
PY
chmod -R 755 $ST; (cd $ST && tar --owner=root --group=root -czf "$OLDPWD/$O/ColorVariant_v$V.unitypackage" *); rm -rf $ST
cp src/Assets/Znakisaki/ColorVariant/README.txt CHANGELOG.md 実装・検証報告.md $O/
python3 -I - $O $V <<'PY'
import os,sys,json,hashlib
o,v=sys.argv[1:3]; h={f:hashlib.sha256(open(os.path.join(o,f),'rb').read()).hexdigest() for f in sorted(os.listdir(o))}
json.dump({"package":"ColorVariant","version":v,"algorithm":"SHA-256","files":h},open(os.path.join(o,'SHA256.json'),'w',encoding='utf-8'),ensure_ascii=False,indent=2)
PY
(cd dist && zip -qr ColorVariant_v${V}_BOOTH.zip ColorVariant_v$V)
