from pathlib import Path
r=Path('Assets/CharacterBehavior/Runtime')
p=r/'CharacterMotionDatabase.cs';s=p.read_text().replace('        [HideInInspector] public GroundPathKey[] RootPath;', '''        [HideInInspector] public GroundPathKey[] RootPath;
        [HideInInspector] public bool ScalesWithSpeed;''')
p.write_text(s)
p=r/'GroundMotionPath.cs';s=p.read_text().replace('float rate = track.IsLoop ?', 'float rate = track.IsLoop && track.ScalesWithSpeed ?').replace('=> $"脚位 {Feet:F2}', '=> $"合计 {Total:F2}（脚位 {Feet:F2}').replace('朝向 {Facing:F2}";','朝向 {Facing:F2}）";')
p.write_text(s)
p=r/'CharacterMotionMatcher.cs';s=p.read_text().replace('track.IsLoop ? track.ReferenceSpeed : 0f','track.IsLoop && track.ScalesWithSpeed ? track.ReferenceSpeed : 0f')
s=s.replace('winner.States[0] + " · " + bestCost','winner.States[0] + $" @ {best.Time:F3}s · " + bestCost')
p.write_text(s)
p=Path('Assets/CharacterBehavior/Editor/CharacterMotionDatabaseEditor.cs');s=p.read_text()
s=s.replace('                        ValidatePlayback(next, state, path);','''                        ValidatePlayback(next, state, path);
                        if (path == next.States[0]) next.ScalesWithSpeed = next.IsLoop && state.speedParameterActive;
                        else if (next.IsLoop && next.ScalesWithSpeed != state.speedParameterActive)
                            throw new InvalidOperationException($"{path}：同一轨道的别名需要一致的速度驱动方式。");''')
s=s.replace('if (!track.IsLoop) return;\n            string gaitRate','if (!track.IsLoop || !state.speedParameterActive) return;\n            string gaitRate').replace('if (!state.speedParameterActive || state.speedParameter != gaitRate && state.speedParameter != "LocomotionRate")','if (state.speedParameter != gaitRate && state.speedParameter != "LocomotionRate")')
p.write_text(s)
p=Path('Assets/player/ShieldGroundMotion.asset');s=p.read_text();a,b=s.split('  BakedTracks:\n');p.write_text(a+'  BakedTracks:\n'+b.replace('    Loop: 1\n','    Loop: 1\n    ScalesWithSpeed: 1\n'))
p=Path('.codex-task-work/ground-selection-20261121/verify_assets.py');s=p.read_text().replace("    assert t['Loop']==clip['loopTime']",'''    assert t['Loop']==clip['loopTime']
    for state in t['States']:
        binding=bindings[state[11:]]
        assert binding['m_Speed']==1 and not binding['m_Mirror'] and binding['m_CycleOffset']==0
        assert not any(binding[k] for k in ('m_MirrorParameterActive','m_TimeParameterActive','m_CycleOffsetParameterActive'))
        active=binding['m_SpeedParameterActive']
        assert active==(t.get('ScalesWithSpeed',0) or t['Use']==4)
        if t['Use']==4: assert binding['m_SpeedParameter']=='StopRate'
''')
s=s.replace("q=tr['Poses'][i]\n        rate=", """q=tr['Poses'][i]
        a,b=v(p['Contact']); c,d=v(q['Contact'])
        if (a>=.7 and b<.45 and d>=.7 and c<.45) or (b>=.7 and a<.45 and c>=.7 and d<.45):return math.inf
        rate=""")
p.write_text(s)

