from pathlib import Path
import shutil, uuid, re
rt=Path("Assets/CharacterBehavior/Runtime")
bk=Path(".codex-task-work/character-movement-forecast/before")
def get(n):
    return (rt/(n+".cs")).read_text(encoding="utf-8-sig")
def put(n,s):
    p=rt/(n+".cs")
    if p.exists() and not (bk/p.name).exists(): shutil.copy2(p,bk/p.name)
    raw=p.read_bytes() if p.exists() else b""
    eol="\r\n" if b"\r\n" in raw else "\n"
    p.write_bytes((b"\xef\xbb\xbf" if raw.startswith(b"\xef\xbb\xbf") else b"")+s.replace("\r\n","\n").replace("\n",eol).encode("utf-8"))
    m=Path(str(p)+".meta")
    if not m.exists(): m.write_text(re.sub(r"guid: [a-f0-9]+","guid: "+uuid.uuid4().hex,(rt/"CharacterMotorSettings.cs.meta").read_text()),encoding="utf-8")
def sub(n,a,b):
    s=get(n)
    assert s.count(a)==1,(n,a)
    put(n,s.replace(a,b))
