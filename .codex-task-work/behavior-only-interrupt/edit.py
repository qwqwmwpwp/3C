from pathlib import Path
rt=Path("Assets/CharacterBehavior/Runtime")
ed=rt.parent/"Editor"
def path(n):return (ed if (ed/(n+".cs")).exists() else rt)/(n+".cs")
def read(n):return path(n).read_text(encoding="utf-8-sig")
def put(n,s):
    p=path(n);raw=p.read_bytes()
    p.write_bytes((b"\\xef\\xbb\\xbf" if raw.startswith(b"\\xef\\xbb\\xbf") else b"")+s.replace("\\r\\n","\\n").replace("\\n","\\r\\n" if b"\\r\\n" in raw else "\\n").encode("utf-8"))
