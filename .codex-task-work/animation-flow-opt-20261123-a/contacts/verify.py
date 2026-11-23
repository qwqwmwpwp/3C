from pathlib import Path
import copy
import math
import subprocess

import yaml


root = Path.cwd()
snapshot = Path(__file__).parents[1] / 'before'
normal = Path('Assets/CombatGirlsCharacterPack/CombatGirl_Shield/Animations/Normal')
library_path = Path('Assets/Data/Player/Shield/盾牌地面动作库.asset')
clips = [
    ('SS_Attack_1', 'SS_Attack1', 'Lower Body.Actions.light_attack', -7391068719408809722, 45),
    ('SS_Attack_2', 'SS_Attack2', 'Lower Body.Actions.light_attack_2', -4705555360703855209, 63),
    ('SS_Attack_4', 'SS_Attack4', 'Lower Body.Actions.heavy_attack', -5630466525676994369, 55),
    ('SS_Take', 'ShieldRaise', 'Upper Body.shield_raise', 740381002100001, 18),
]


def asset(path):
    return yaml.safe_load(path.read_text(encoding='utf-8-sig').split('\n', 3)[3])['MonoBehaviour']


def clip_ref(clip):
    return clip['guid'], clip['fileID']


def keys(curves, name):
    return next(curve for curve in curves if curve['Name'] == name)['Curve']['m_Curve']


def evaluate(curve, phase):
    for a, b in zip(curve, curve[1:]):
        if phase <= b['time']:
            delta = (phase - a['time']) / (b['time'] - a['time'])
            smoothed = delta * delta * (3 - 2 * delta)
            return a['value'] + (b['value'] - a['value']) * smoothed
    return curve[-1]['value']


release = asset(library_path)
original = asset(snapshot / library_path)
db = copy.deepcopy(release)
lookup = {clip_ref(row['Clip']): row for row in db['Playback']['Clips']}
states = {row['Path']: clip_ref(row['Clip']) for row in db['Playback']['States']}
for filename, imported_name, state, clip_id, intervals in clips:
    path = normal / (filename + '.fbx.meta')
    imported = yaml.safe_load(path.read_text(encoding='utf-8-sig'))
    prior = yaml.safe_load((snapshot / path).read_text(encoding='utf-8-sig'))
    name_table = {row['first'][74]: row['second'] for row in imported['ModelImporter']['internalIDToNameTable']}
    assert name_table[clip_id] == imported_name
    ref = imported['guid'], clip_id
    assert states[state] == ref
    clip = next(row for row in imported['ModelImporter']['animations']['clipAnimations'] if row['name'] == imported_name)
    baked = lookup[ref]['Curves']
    source = clip['curves']
    assert [x['name'] for x in source] == [x['Name'] for x in baked]
    assert len(baked) == (3 if imported_name == 'ShieldRaise' else 2)
    for s, b in zip(source, baked):
        assert b['InSeconds'] == 0 and s['curve']['curve'] == b['Curve']['m_Curve']
        assert b['Curve']['m_PreInfinity'] == s['curve']['preInfinity']
        assert b['Curve']['m_PostInfinity'] == s['curve']['postInfinity']
        assert b['Curve']['m_RotationOrder'] == s['curve']['rotationOrder']
        curve = b['Curve']['m_Curve']
        times = [x['time'] for x in curve]
        assert times[0] == 0 and times[-1] == 1 and all(a < b for a, b in zip(times, times[1:]))
        assert all(abs(t * intervals - round(t * intervals)) < 1e-6 for t in times)
        assert all(0 <= k['value'] <= 1 and k['inSlope'] == k['outSlope'] == 0 for k in curve)
        if b['Name'].endswith('Foot'):
            assert min(x['value'] for x in curve) < .3 and max(x['value'] for x in curve) > .7
    if imported_name == 'SS_Attack4':
        heavy_front = keys(baked, 'IKRightFoot')
        assert evaluate(heavy_front, 19 / intervals) == 0
        assert evaluate(heavy_front, 31 / intervals) >= .7
        assert evaluate(heavy_front, 44 / intervals) <= .3
    if imported_name == 'ShieldRaise':
        grip = keys(baked, 'IKLeftHand')
        assert evaluate(grip, .75) == 0 and evaluate(grip, 1) == 1
    clip['curves'] = []
    assert imported == prior, filename
    lookup[ref]['Curves'] = []
    print(f'{imported_name}: same {len(baked)} normalized curves, {intervals} import intervals, clip ID/state confirmed')
assert db == original, 'Library changes outside the four target curve arrays'


def support(contacts, confidence):
    lower = [a * b for a, b in zip(contacts, confidence)]
    upper = [a + 1 - b for a, b in zip(lower, confidence)]
    if contacts[0] >= .7 and lower[0] >= .6 and upper[1] < .45:
        return 'left'
    if contacts[1] >= .7 and lower[1] >= .6 and upper[0] < .45:
        return 'right'
    return 'either'


def cost(contacts, confidence, candidate):
    return sum((a - b) ** 2 * c for a, b, c in zip(contacts, candidate, confidence))


for contacts, confidence, expected in [((.92, .1), (1, 1), 'left'), ((.92, .1), (.8, .8), 'left'),
                                        ((.92, 0), (.8, 0), 'either'), ((.92, .1), (.4, .4), 'either'),
                                        ((.1, .92), (.8, .8), 'right'), ((.85, .85), (1, 1), 'either')]:
    assert support(contacts, confidence) == expected
    print(f'Contact {contacts}, coverage {confidence}: {expected}')
assert math.isclose(cost((.92, 0), (.8, 0), (0, 1)), .92 ** 2 * .8)
assert math.isclose(cost((.92, 0), (.8, 0), (0, 0)), .92 ** 2 * .8)
assert math.isclose(cost((.92, 0), (.8, 1), (0, 1)), .92 ** 2 * .8 + 1)
assert math.isclose(.8 * .92 / .8, .92)
print('Independent unknown/airborne scoring and normalization confirmed.')

cs_files = [
    'Anim/CharacterFootObservation.cs', 'Anim/CharacterClipLibrary.cs', 'Anim/CharacterAnimationIk.cs',
    'Anim/CharacterMotionMatcher.cs', 'Anim/CharacterLocomotionPoseSelector.cs', 'Anim/CharacterAnimationPipeline.cs',
    'Editor/CharacterMotionDatabaseBaker.cs', 'Editor/CharacterAnimationOverrideAudit.cs',
]
for name in cs_files:
    path = Path('Assets/Code/Character') / name
    lines = len(path.read_bytes().splitlines())
    assert lines <= 500, name
    assert path.with_suffix('.cs.meta').is_file()
    diff = subprocess.run(['git', '-c', 'core.whitespace=cr-at-eol', 'diff', '--check', '--no-index', str(snapshot / path), str(path)],
                          capture_output=True, check=False)
    assert diff.returncode in (0, 1) and not diff.stdout, (name, diff.stdout, diff.stderr)
    print(f'{path.name}: {lines} lines, meta present, diff whitespace clean')
