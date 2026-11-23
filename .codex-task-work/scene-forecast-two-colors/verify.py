from pathlib import Path
import re,collections,subprocess,hashlib
root=Path.cwd()
w=(root/'.codex-task-work/scene-forecast-two-colors').resolve()
changed=[root/s for s in (w/'changed.txt').read_text().splitlines()]
sources=[p for p in changed if p.suffix=='.cs']
new=[p for p in changed if not (w/'before'/p.name).exists()]
def rd(p): return p.read_text(encoding='utf-8-sig')
def find(n):return next(p for p in changed if p.name==n)
def code(n):return rd(find(n))
counts=sorted((len(rd(p).splitlines()),p.name) for p in changed)
assert counts[-1][0]<=500,counts
for p in changed:
    assert p.exists() and Path(str(p)+'.meta').exists()
    s=rd(p);assert not re.search(r'^(<<<<<<< |>>>>>>> |=======$)',s,re.M)
    assert len(re.findall('^#if ',s,re.M))==len(re.findall('^#endif',s,re.M)),p
guids=[re.search(r'^guid: (\w+)',rd(Path(str(p)+'.meta')),re.M)[1] for p in new]
assert len(set(guids))==len(new)
out=subprocess.check_output(['rg','-l','--crlf','-e','^guid: ('+'|'.join(guids)+')$','Assets','-g','*.meta'],text=True)
assert len(out.splitlines())==len(guids)
assert re.search(r'^guid: (\w+)',rd(Path(str(find('CharacterForecastGhost.shader'))+'.meta')),re.M)[1] in code('CharacterForecastGhost.cs')
for p in sources:
    if p.parent.name=='Runtime':
        assert 'using UnityEditor' not in rd(p),p
s=code('CharacterForecastGhost.cs')
assert 'AddComponent<Animator>()' in s and 'AddComponent<SkinnedMeshRenderer>()' in s
assert not re.search(r'Instantiate\(|AddComponent<(Rigidbody|Collider|CharacterBehaviorDriver)>',s)
for token in ['fireEvents = false','applyRootMotion = false','DirectorUpdateMode.Manual','ClosePreviewScene','DestroyImmediate(part.Geometry)','Graphics.DrawMeshNow','BakeMesh(']: assert token in s,token
assert 'DrawActual()' not in code('CharacterBehaviorDriver.cs') and 'Gizmos.color' not in code('CharacterMovementForecast.cs')
s=code('CharacterForecastTrail.cs')
assert 'previous.CopyPublishedFrom(source)' in s and 'source.Revised -= Changed' in s and 'old.TrySample(t, 0f' in s and 'next.TrySample(t, 0f' in s
s=code('CharacterForecastSceneView.cs');assert 'StageUtility.GetCurrentStageHandle()' in s and 'AssemblyReloadEvents.beforeAssemblyReload += Release' in s and 'path.TrySample(end, 0f' in s
s=code('CharacterMovementPredictor.cs');assert s.index('Output.Notify(routine')>s.index('Simulate(origin')
s=code('CharacterForecastRehearsal.cs');assert s.count('predictor.Refresh(')==2 and 'flow.Tick(opening' in s and 'body.position =' not in s
# Resolve model hierarchy and every referenced preview bone on the active test prefab.
prefab=rd(root/'Assets/player/player 1.prefab')
docs=re.split(r'^--- !u!',prefab,flags=re.M)[1:]
parents={}; gos={}; animator_go=None; skins=[]; capsule_go=None; go_props={}
def ref(s,key):return int(re.search(r'^  '+key+r': \{fileID: ([-\d]+)',s,re.M)[1])
for doc in docs:
    t,ident=map(int,re.match(r'(\d+) &(-?\d+)',doc).groups())
    if ' stripped' in doc.splitlines()[0]:
        if t==4:
            inst=ref(doc,'m_PrefabInstance')
            source=next(d for d in docs if d.startswith('1001 &'+str(inst)+'\n'))
            parents[ident]=int(re.search(r'm_TransformParent: \{fileID: (\d+)',source)[1])
        continue
    if t==4:
        parents[ident]=ref(doc,'m_Father');gos[ref(doc,'m_GameObject')]=ident
    if t==1:go_props[ident]=int(re.search(r'^  m_IsActive: (\d+)',doc,re.M)[1])
    if t==95:animator_go=ref(doc,'m_GameObject')
    if t==136:capsule_go=ref(doc,'m_GameObject')
    if t==137:
        body=re.search(r'^  m_Bones:\s*\n((?:  - \{fileID: [^\n]+\}\n)*)',doc,re.M)
        skins.append((ref(doc,'m_GameObject'),[int(x) for x in re.findall(r'fileID: (\d+)',body[1])] if body else []))
assert animator_go and capsule_go and skins
def below(t,a):
    while t:
        if t==a:return True
        t=parents.get(t,0)
    return False
meshes=[(g,b) for g,b in skins if below(gos[g],gos[animator_go])]
assert meshes and len(meshes)==len(skins)
assert all(below(t,gos[animator_go]) for _,b in meshes for t in b if t)
profile=rd(root/'Assets/player/ShieldCharacterProfile.asset');lib=rd(root/'Assets/player/ShieldGroundMotion.asset')
paths=re.findall(r'^    - Path: (.+)',lib,re.M)
for key in ['idleStateName','walkStateName','moveStateName','sprintStateName','dashStateName']:
    path=re.search(r'^    '+key+r': (.+)',profile,re.M)[1];assert path in paths,path
for key in ['light_attack','light_attack_2','heavy_attack']:
    assert 'Lower Body.Actions.'+key in paths
print('Prefab:',len(meshes),'skinned meshes and',sum(len(b) for _,b in meshes),'bone references in preview animator subtree; 8 motion/action state paths resolved.')
rt=root/'Assets/CharacterBehavior/Runtime';ed=root/'Assets/CharacterBehavior/Editor';bee=root/'Library/Bee/artifacts/1900b0aE.dag'
for asm,folder in [('Assembly-CSharp',rt),('Assembly-CSharp-Editor',ed)]:
    lines=rd(bee/(asm+'.rsp')).splitlines()
    for i,l in enumerate(lines):
        if l.startswith('-out:'):lines[i]=f'-out:"{(w/(asm+".dll")).as_posix()}"'
        elif l.startswith('-refout:'):lines[i]=f'-refout:"{(w/(asm+".ref.dll")).as_posix()}"'
        elif asm.endswith('Editor') and l.startswith('-r:') and l.endswith('Assembly-CSharp.ref.dll"'):lines[i]=f'-r:"{(w/"Assembly-CSharp.ref.dll").as_posix()}"'
    for p in folder.glob('*.cs'):
        arg='"'+p.relative_to(root).as_posix()+'"'
        if arg not in lines:lines.append(arg)
        assert lines.count(arg)==1
    assert '-define:UNITY_EDITOR' in lines
    if asm.endswith('Editor'):assert f'-r:"{(w/"Assembly-CSharp.ref.dll").as_posix()}"' in lines
    for line in lines:
        if line.startswith(('-r:"','-analyzer:"','/additionalfile:"','"Assets/')):
            path=Path(line.split('"')[1]);assert path.is_file() or path==w/'Assembly-CSharp.ref.dll',path
    (w/(asm+'.rsp')).write_text('\n'.join(lines)+'\n',encoding='utf8')
(w/'checked-sha256.txt').write_text('\n'.join(f'{hashlib.sha256(p.read_bytes()).hexdigest()}  {p.relative_to(root)}' for p in changed),encoding='utf8')
print('Sources:',len(sources),'C# + 1 shader;',len(new),'new GUIDs; maximum:',counts[-1])
