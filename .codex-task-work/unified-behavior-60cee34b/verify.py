from pathlib import Path
import os, subprocess, sys
root = Path.cwd()
work = Path(__file__).resolve().parent
bee = root / 'Library/Bee/artifacts/1900b0aE.dag'
data = Path('E:/unity/2022/2022.3.62f3/Editor/Data')
compiler = [str(data/'NetCoreRuntime/dotnet.exe'), str(data/'DotNetSdkRoslyn/csc.dll')]
mono = str(data/'MonoBleedingEdge/bin/mono.exe')
engine = data / 'Managed/UnityEngine'
api = data / 'MonoBleedingEdge/lib/mono/4.8-api'
env = os.environ.copy()
env['MONO_PATH'] = os.pathsep.join(map(str,[work, engine, data/'Managed', root/'Library/ScriptAssemblies']))
def run(name, args, show=True):
    p = subprocess.run(args, cwd=root, env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    (work/(name+'.log')).write_bytes(p.stdout)
    if show or p.returncode: print(name, p.returncode, p.stdout.decode('utf-8', errors='replace')[-4500:])
    if p.returncode: raise SystemExit(p.returncode)
    return p.stdout

def offline_build():
    for name, folder in [('Assembly-CSharp','Runtime'), ('Assembly-CSharp-Editor','Editor')]:
        rsp = (bee/(name+'.rsp')).read_text(encoding='utf-8-sig')
        for path in (root/'Assets/CharacterBehavior'/folder).glob('*.cs'):
            if '"'+path.relative_to(root).as_posix()+'"' not in rsp:
                raise SystemExit('Bee sources missing: '+str(path))
        for kind, ext in [('out','dll'),('refout','ref.dll')]:
            rsp = rsp.replace(f'-{kind}:"Library/Bee/artifacts/1900b0aE.dag/{name}.{ext}"',
                              f'-{kind}:"{(work/(name+"."+ext)).as_posix()}"')
        if name.endswith('Editor'):
            rsp = rsp.replace('-r:"Library/Bee/artifacts/1900b0aE.dag/Assembly-CSharp.ref.dll"',
                              f'-r:"{(work/"Assembly-CSharp.ref.dll").as_posix()}"')
        rsp_path = work/(name+'.rsp')
        rsp_path.write_text(rsp, encoding='utf-8-sig')
        run(name, compiler+['-noconfig','@'+str(rsp_path)])
def harness(name, files, references=(), defines=()):
    common = ['-nologo','-noconfig','-nostdlib','-langversion:9','-nowarn:0649','-target:exe',
              '-out:'+str(work/(name+'.exe'))]
    refs = [api/'mscorlib.dll',api/'System.dll',api/'System.Core.dll',api/'Facades/netstandard.dll',
            engine/'UnityEngine.CoreModule.dll',engine/'UnityEngine.dll',*references]
    run(name+'-compile', compiler+common+['-reference:'+str(r) for r in refs]+list(defines)+list(map(str,files)))
if __name__=='__main__':
    offline_build()
    harness('BehaviorRegression', [root/'Tools/CharacterBehavior/CharacterBehaviorFlowRegression.cs'], [work/'Assembly-CSharp.dll'])
    run('BehaviorRegression',[mono,str(work/'BehaviorRegression.exe')])

