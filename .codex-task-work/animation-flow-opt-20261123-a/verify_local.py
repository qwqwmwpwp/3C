from pathlib import Path
import hashlib, re, sys, subprocess
root = Path.cwd()
work = root / ".codex-task-work/animation-flow-opt-20261123-a"
before = work / "before"
read = lambda p: p.read_text(encoding="utf-8-sig")
roots = [root/"Assets/Code/Character",root/"Assets/Data/Player/Shield"]
existing = {p.relative_to(before) for p in before.rglob("*") if p.is_file()}
current = {p.relative_to(root) for location in roots for p in location.rglob("*") if p.is_file()} | existing
changes = sorted(p for p in current if not (before/p).exists() or not (root/p).exists() or (before/p).read_bytes() != (root/p).read_bytes())
def digest(p):
    return hashlib.sha256((root/p).read_bytes()).hexdigest()
script_changes = {p for p in changes if p.suffix == ".cs"}
if "list" in sys.argv:
    for p in changes: print(p.as_posix())
    sys.exit()
assert changes and script_changes
new_metas = []
for p in changes:
    f = root/p
    assert f.is_file(), p
    if p.suffix == ".cs":
        data=read(f)
        assert len(data.splitlines()) <= 500 and "<<<<<<<" not in data and ">>>>>>>" not in data, p
        assert data.count("#if ") == data.count("#endif"), p
        assert Path(str(f)+".meta").is_file(), p
    if p.suffix == ".meta":
        guid=re.search(r"(?m)^guid: ([0-9a-f]{32})$",read(f)).group(1)
        if (before/p).exists():
            assert guid in read(before/p), p
        else: new_metas.append(guid)
if new_metas:
    matches=subprocess.check_output(["rg","-l","--no-ignore","-g","*.meta","guid: ("+"|".join(new_metas)+")","Assets"],encoding="utf-8")
    assert len(matches.splitlines())==len(set(new_metas))==len(new_metas), matches
print("Changed files",len(changes),"C#",len(script_changes),"max lines",max((len(read(root/p).splitlines()),p.name) for p in script_changes))
out = work/"compile"
out.mkdir(exist_ok=True)
bee=root/"Library/Bee/artifacts/1900b0aE.dag"
for assembly in ["Assembly-CSharp","Assembly-CSharp-Editor"]:
    editor=assembly.endswith("-Editor")
    lines=read(bee/(assembly+".rsp")).splitlines()
    refdll=(out/"Assembly-CSharp.ref.dll").as_posix()
    for i,line in enumerate(lines):
        if line.startswith("-out:"): lines[i]='-out:"'+(out/(assembly+".dll")).as_posix()+'"'
        elif line.startswith("-refout:"): lines[i]='-refout:"'+(out/(assembly+".ref.dll")).as_posix()+'"'
        elif editor and line.startswith('-r:') and line.endswith('Assembly-CSharp.ref.dll"'): lines[i]='-r:"'+refdll+'"'
    for p in sorted((root/"Assets/Code/Character").rglob("*.cs")):
        rel=p.relative_to(root)
        if ("Editor" in rel.parts) != editor: continue
        flag='"'+rel.as_posix()+'"'
        if flag not in lines: lines.append(flag)
        assert lines.count(flag)==1
    assert "-define:UNITY_EDITOR" in lines
    if editor: assert '-r:"'+refdll+'"' in lines
    for line in lines:
        if line.startswith(('-r:"','-analyzer:"','/additionalfile:"','"Assets/')):
            fname=line.split('"')[1]; assert Path(fname).is_file() or fname==refdll, fname
    (out/(assembly+".rsp")).write_text("\n".join(lines)+"\n",encoding="utf8")
    print(assembly,"total source files",sum(x.startswith('"Assets/') for x in lines))
if "compile" in sys.argv:
    ed=Path("E:/unity/2022/2022.3.62f3/Editor/Data")
    for name in ["Assembly-CSharp","Assembly-CSharp-Editor"]:
        p=subprocess.run([str(ed/"NetCoreRuntime/dotnet.exe"),str(ed/"DotNetSdkRoslyn/csc.dll"),"@"+str(out/(name+".rsp"))],stdout=subprocess.PIPE,stderr=subprocess.STDOUT)
        (out/(name+".log")).write_bytes(p.stdout)
        print(p.stdout.decode("utf-8-sig",errors="replace"),end="")
        print(name,"exit",p.returncode)
        if p.returncode: sys.exit(p.returncode)
    (out/"verified-files.txt").write_text("\n".join(digest(p)+" "+p.as_posix() for p in changes),encoding="utf8")

