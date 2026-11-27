from pathlib import Path
import json,hashlib,re,yaml
t=Path(__file__).resolve().parent;p=t.parent.parent
manifest=json.loads((t/'implementation-manifest.json').read_bytes())
base=json.loads((t/'baseline-manifest.json').read_bytes())
shield=[r for r in base if r['Path'].startswith('Assets/Data/Player/Shield/')]
for r in shield: assert hashlib.sha256((p/r['Path']).read_bytes()).hexdigest().upper()==r['SHA256']
new=[p/r['Path'] for r in manifest if r['Status']=='Added' and r['Path'].endswith('.cs')]
for source in new:
 guid=re.search('guid: ([a-z0-9]+)',source.with_suffix('.cs.meta').read_text())[1]
 assert sum(guid in meta.read_text(errors='ignore') for meta in (p/'Assets').rglob('*.meta'))==1
layers=('CharacterAnimationResourceCatalog','CharacterAnimationComposer','CharacterLocomotionPoseSelector','CharacterClipMixer','CharacterClipPlayback','CharacterAnimationPlayer')
for name in layers:
 contents=next((p/'Assets/Code/Character').rglob(name+'.cs')).read_text()
 assert not re.search(r'CharacterAttack|AttackTiming|IsAttack|AttackElapsed|FinishEvaluatedAttacks|AttackStateId|AttackTable',contents)
table=yaml.safe_load((p/'Assets/Data/Player/Shield/02 连招表.asset').read_text().split('MonoBehaviour:\n',1)[1])['Data']
rows=[]
for slot in ('LightOne','LightTwo','Heavy','SkillTwo'):
 s=table[slot];rows.append(f"{slot}: state=({s['EntryBlendSeconds']}, {s['ActiveSeconds']}, {s['RecoverySeconds']}), animation={s['AnimationDurationSeconds']}, links={len(s['Links'])}")
lines=[f'Shield resources and metadata matched byte-for-byte: {len(shield)}.',f'New scripts have paired, unique Unity GUIDs: {len(new)}.',f'No attack-table/state/timing dependency in {", ".join(layers)}.']+rows
(t/'scope-result.log').write_text('\n'.join(lines)+'\n',encoding='utf-8');print('\n'.join(lines))

