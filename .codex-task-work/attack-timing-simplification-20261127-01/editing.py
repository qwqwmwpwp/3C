from pathlib import Path
import re
root=Path("Assets/Code/Character")
def use(name):
 global path, s, raw
 path=next(root.rglob(name+'.cs'));raw=path.read_bytes();s=raw.decode('utf-8').replace('\r\n','\n')
def write():
 path.write_bytes((s.replace('\n','\r\n') if b'\r\n' in raw else s).encode('utf-8'))
def sub(a,b):
 global s
 assert a in s,(path,a)
 s=s.replace(a,b)
def cut(a,b,rep=''):
 global s
 i=s.index(a);j=s.index(b,i+len(a));s=s[:i]+rep+s[j:]
def lines(*parts):
 global s
 for part in parts:
  assert part in s,(path,part)
  s=''.join(l for l in s.splitlines(True) if part not in l)

