from pathlib import Path
import shutil, uuid, re
root=Path("Assets/CharacterBehavior")
w=Path(".codex-task-work/scene-forecast-two-colors")
def name(n):
    return root/(n if "/" in n else "Runtime/"+n+".cs")
def get(n):
    return name(n).read_text(encoding="utf-8-sig")
def put(n,s):
    p=name(n)
    record=w/"changed.txt"
    known=record.read_text().splitlines() if record.exists() else []
    if str(p) not in known:
        if p.exists(): shutil.copy2(p,w/"before"/p.name)
        with record.open("a") as f: f.write(str(p)+"\n")
    raw=p.read_bytes() if p.exists() else b""
    p.write_bytes((b"\xef\xbb\xbf" if raw.startswith(b"\xef\xbb\xbf") else b"")+s.replace("\r\n","\n").replace("\n","\r\n" if b"\r\n" in raw else "\n").encode("utf8"))
    meta=Path(str(p)+".meta")
    if not meta.exists():
        template=(root/"Editor/CharacterAnimationDebugWindow.cs.meta").read_text() if p.suffix==".cs" else "fileFormatVersion: 2\nguid: 00000000\nShaderImporter:\n  externalObjects: {}\n  defaultTextures: []\n  nonModifiableTextures: []\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n"
        meta.write_text(re.sub(r"guid: [0-9a-f]+","guid: "+uuid.uuid4().hex,template),encoding="utf8")
def sub(n,a,b):
    s=get(n)
    assert s.count(a)==1,(n,a)
    put(n,s.replace(a,b))
