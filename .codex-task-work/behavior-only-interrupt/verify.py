from pathlib import Path
import hashlib,re
root=Path.cwd()
w=(root/'.codex-task-work/behavior-only-interrupt').resolve()
rt=root/'Assets/CharacterBehavior/Runtime'; ed=root/'Assets/CharacterBehavior/Editor'
changed=[(rt/f.name if (rt/f.name).exists() else ed/f.name) for f in (w/'before').glob('*.cs')]
def rd(p):return p.read_text(encoding='utf-8-sig')
def src(n):return rd(next(p for p in changed if p.stem==n))
def body(s, head):
    i=s.index('{',s.index(head));end=i+1;depth=1
    while depth:
        c=s[end];depth+=int(c=='{')-int(c=='}');end+=1
    return s[i:end]
counts=sorted((len(rd(p).splitlines()),p.name) for p in changed)
assert len(counts)==6 and counts[-1][0]<=500,counts
for p in changed:
    assert Path(str(p)+'.meta').exists() and not re.search(r'^(<<<<<<< |>>>>>>> |=======$)',rd(p),re.M)
    assert len(re.findall(r'^#if ',rd(p),re.M))==len(re.findall(r'^#endif',rd(p),re.M))
predict=src('CharacterMovementPredictor');driver=src('CharacterBehaviorDriver');trail=src('CharacterForecastTrail')
assert 'Output.' not in body(predict,'internal void RequestRefresh(')
assert 'predictor.RequestRefresh("攻击按下")' in driver and 'predictor.RequestRefresh("短突进按下")' in driver
assert 'Output.Notify(CharacterForecastRevision.Published)' in body(predict,'internal void Refresh(')
assert 'CharacterForecastRevision.Invalidated' in body(predict,'internal void ObserveSolvedStep(')
assert 'driver.FrameCommitted += Committed;' in trail and 'driver.FrameCommitted -= Committed;' in trail
committed=body(trail,'private void Committed(')
assert committed.index('if (!behavior.HasTransitions) return;') < committed.index('Mark(old')
assert 'behavior.InterruptedBehaviors' in committed and 'SameBehavior(expected, actual)' in committed
published=body(trail,'private void Published(')
assert 'Mark(' not in published and 'Withdraw(' not in published
definition=trail.split('private static bool SameBehavior(',1)[1].split(';',1)[0]
assert all(x not in definition for x in ['.Gait','.Rotation','.Velocity','.Position','.Contacts','.AttackPhase','Direction','Input'])
assert all(x in definition for x in ['.Behaviors','.MoveApproved','.AttackInstance','.ExternallyControlled'])
assert 'FirstBehaviorChange(old, next, now' in body(trail,'internal void CompareRehearsal(')
assert 'CharacterForecastRevision.Takeover' in body(predict,'internal void ConfirmKnockback(')
assert 'CharacterForecastRevision.Withdraw' in body(predict,'internal void Suppress(')
assert 'trail.CompareRehearsal(' in src('CharacterForecastSceneView')
assert 'CharacterForecastRevision.Replace' not in ''.join(rd(p) for p in (root/'Assets/CharacterBehavior').rglob('*.cs'))
print('Verified source gates: raw input / ordinary correction / committed transition / external takeover / edit-mode behavior comparison')
bee=root/'Library/Bee/artifacts/1900b0aE.dag'
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
print('Changed C# files:',len(changed),'maximum:',counts[-1])
