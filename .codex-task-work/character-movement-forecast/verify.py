from pathlib import Path
import re, subprocess, hashlib
root=Path.cwd()
work=(root/'.codex-task-work/character-movement-forecast').resolve()
rt=root/'Assets/CharacterBehavior/Runtime'
ed=root/'Assets/CharacterBehavior/Editor'
before=work/'before'
new='CharacterInputSnapshot CharacterControllerFrameResult CharacterForecastDiagnostics CharacterForecastGeometry CharacterForecastQuery CharacterMovementForecast CharacterMovementPredictor CharacterMotorStep CharacterStepProbe'.split()
changed={rt/(n+'.cs') for n in new}
for p in before.glob('*.cs'):
    changed.add((ed if p.name=='CharacterAnimationDebugWindow.cs' else rt)/p.name)
def read(p): return p.read_text(encoding='utf-8-sig')
counts=sorted((len(read(p).splitlines()),p.name) for p in changed)
assert counts[-1][0]<=500,counts
for p in changed:
    assert p.is_file() and Path(str(p)+'.meta').is_file()
    text=read(p)
    assert not any(line.startswith(('<<<<<<< ','>>>>>>> ')) for line in text), p
    assert text.count('#if ')==text.count('#endif'),p
guids=[re.search(r'^guid: (\w+)',read(rt/(name+'.cs.meta')),re.M)[1] for name in new]
assert len(set(guids))==len(guids)
out=subprocess.check_output(['rg','-l','-e','^guid: ('+'|'.join(guids)+')$','--crlf','-g','*.meta','Assets'],text=True)
assert len(out.splitlines())==len(guids),out
def code(n):return read(rt/(n+'.cs'))
for n in ['CharacterMovementPredictor','CharacterMotorStep','CharacterMovementForecast']:
    assert not re.search(r'(?<!Character)\b(Rigidbody|Animator|CharacterBehaviorDriver)\b|\.Consume\(|\.MoveRotation\(|\.AddForce\(|\.PlayActionOnce\(',code(n)),n
assert 'live.CopyStateTo(simulation)' in code('CharacterMovementPredictor')
assert 'simulation.Tick(forced ? default : input' in code('CharacterMovementPredictor')
assert 'inputs.AtStep(i)' in code('CharacterMovementPredictor') and 'index == 0 ? locked : index == 1 ? Buffered : held' in code('CharacterMovementPredictor')
assert 'inputBlackboard.Peek()' in code('CharacterBehaviorDriver') and code('CharacterBehaviorDriver').count('inputBlackboard.Consume()')==1
for n in ['CharacterRigidbodyMotor','CharacterMovementPredictor']:
    assert 'Advance(' in code(n) and 'CharacterMotorStep.Target' in code(n)
for n in ['CharacterRigidbodyMotor','CharacterForecastGeometry']:
    assert '.TryRise(' in code(n)
assert 'drive.Gravity' in code('CharacterMotorStep') and 'Quaternion Facing' in code('CharacterMotorStep')
assert '0.15f' in code('CharacterMatchTrajectory') and '0.30f' in code('CharacterMatchTrajectory')
assert 'Fixed Timestep: 0.02' in read(root/'ProjectSettings/TimeManager.asset')
assert 'Interval: 0.2\n    Horizon: 2' in read(root/'Assets/player/ShieldCharacterProfile.asset')
assert len(re.findall('MovementForecast', code('CharacterBehaviorFlow')))==0
assert 'PrepareReturn(forecast' in code('CharacterAnimationPipeline') and 'ShowDialog' not in code('CharacterMovementPredictor')
assert 'GroundReplayCase.DashTurn' in read(ed/'CharacterAnimationDebugWindow.cs')
for n in ['CharacterAttackCombo','CharacterDashBehavior']:
    c=code(n);assert 'base.CopyStateTo(target)' in c
    assigned=set(re.findall(r'copy\.(\w+) = \1;',c))
    # All mutable runtime fields in the built-in timed actions are accounted for.
    fields=('move bufferedMove instanceId openingStep duration elapsed startProgress strike recovery recoveryMoveStart recoveryMoveBlendSeconds attackMotion stepMotion OrdinaryMovementSeconds hasAttackMotion MoveStarted' if n=='CharacterAttackCombo' else '_speed IsPending RemainingDuration Direction StaminaCost Cooldown').split()
    assert set(fields)<=assigned,(n,assigned)
print('Scopes: input latch / independent behavior copy / shared motor and step geometry / animation query / replay / serialized defaults')
print('Changed:', len(changed),'C# files; new:',len(new),'unique Unity GUIDs; maximum:',counts[-1])
bee=root/'Library/Bee/artifacts/1900b0aE.dag'
for assembly,folder in [('Assembly-CSharp',rt),('Assembly-CSharp-Editor',ed)]:
    lines=read(bee/(assembly+'.rsp')).splitlines()
    for i,line in enumerate(lines):
        if line.startswith('-out:'):lines[i]=f'-out:"{(work/(assembly+".dll")).as_posix()}"'
        elif line.startswith('-refout:'):lines[i]=f'-refout:"{(work/(assembly+".ref.dll")).as_posix()}"'
        elif assembly.endswith('Editor') and line.startswith('-r:') and line.endswith('Assembly-CSharp.ref.dll"'):
            lines[i]=f'-r:"{(work/"Assembly-CSharp.ref.dll").as_posix()}"'
    for p in folder.glob('*.cs'):
        arg='"'+p.relative_to(root).as_posix()+'"'
        if arg not in lines:lines.append(arg)
        assert lines.count(arg)==1,p
    assert '-define:UNITY_EDITOR' in lines
    if assembly.endswith('Editor'):assert f'-r:"{(work/"Assembly-CSharp.ref.dll").as_posix()}"' in lines
    for line in lines:
        if line.startswith(('-r:"','-analyzer:"','/additionalfile:"','"Assets/')):
            path=Path(line.split('"')[1]); assert path.is_file() or path==work/'Assembly-CSharp.ref.dll',path
    (work/(assembly+'.rsp')).write_text('\n'.join(lines)+'\n',encoding='utf-8')
    print(assembly, 'verified source arguments:',sum(line.startswith('"Assets/') for line in lines))
(work/'checked-sha256.txt').write_text('\n'.join(f'{hashlib.sha256(p.read_bytes()).hexdigest()}  {p.relative_to(root)}' for p in sorted(changed)),encoding='utf8')
