from pathlib import Path
import re,uuid
root=Path('Assets/Code/Character')
def use(name):
 global path,s,raw
 path=next(root.rglob(name+'.cs'));raw=path.read_bytes();s=raw.decode().replace('\r\n','\n')
def write():
 path.write_bytes(s.replace('\n','\r\n' if b'\r\n' in raw else '\n').encode())
def sub(a,b):
 global s
 assert a in s,(path,a)
 s=s.replace(a,b)
def cut(a,b,rep=''):
 global s
 i=s.index(a);j=s.index(b,i+len(a));s=s[:i]+rep+s[j:]
def new(name,code,example):
 p=root/name
 assert not p.exists()
 p.write_text(code,encoding='utf-8')
 old=next(root.rglob(example+'.cs.meta')).read_text()
 p.with_suffix('.cs.meta').write_text(re.sub('guid: [a-f0-9]+','guid: '+uuid.uuid4().hex,old),encoding='utf-8')

