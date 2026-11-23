from pathlib import Path
import re


base = Path('Assets/CombatGirlsCharacterPack/CombatGirl_Shield/Animations/Normal')
database = Path('Assets/Data/Player/Shield/盾牌地面动作库.asset')
left = 'IKLeftFoot'
right = 'IKRightFoot'
hand = 'IKLeftHand'

# Keys are source frames at 30 Hz. Clip lengths use the existing Unity import artifacts.
clips = {
    'SS_Attack_1': ('SS_Attack1', -7391068719408809722, 0, 45, {
        left: [(0, .45), (3, .08), (13, .08), (16, .60), (18, .86), (24, .86), (26, .95), (33, .95),
               (35, .55), (36, .20), (38, 0), (45, .10)],
        right: [(0, .45), (3, .25), (5, 0), (12, .06), (16, .18), (18, .04), (30, .08),
                (32, .85), (35, .95), (45, .85)],
    }),
    'SS_Attack_2': ('SS_Attack2', -4705555360703855209, 0, 63, {
        left: [(0, .60), (2, .20), (4, 0), (19, 0), (20, .28), (24, .62), (27, .90), (36, .91),
               (40, .80), (42, .30), (44, .25), (47, .87), (50, .80), (52, .35), (57, 0), (63, 0)],
        right: [(0, .10), (7, .05), (19, .18), (21, .48), (26, .90), (35, .90), (37, .74),
                (39, .45), (43, .20), (47, .04), (49, .28), (51, .78), (56, .90), (63, .90)],
    }),
    'SS_Attack_4': ('SS_Attack4', -5630466525676994369, 0, 55, {
        left: [(0, .75), (2, .40), (4, .28), (8, .06), (11, 0), (32, 0), (33, .10),
               (38, .30), (41, .62), (43, .76), (45, .79), (47, .22), (48, .07), (55, .07)],
        right: [(0, .04), (4, .22), (6, .58), (9, .20), (12, 0), (24, 0), (26, .22), (28, .76),
                (31, .90), (37, .90), (39, .85), (40, .75), (41, .45), (43, .18), (47, .12),
                (50, .53), (52, .85), (55, .95)],
    }),
    'SS_Take': ('ShieldRaise', 740381002100001, 8, 26, {
        left: [(8, .12), (13, .10), (15, .48), (17, .90), (26, .90)],
        right: [(8, .88), (16, .88), (18, .58), (20, .28), (23, .12), (26, .18)],
        hand: [(8, 0), (22, 0), (24, .30), (26, 1)],
    }),
}


def write_original_eol(path, content):
    original = path.read_bytes()
    eol = '\r\n' if b'\r\n' in original else '\n'
    prefix = b'\xef\xbb\xbf' if original.startswith(b'\xef\xbb\xbf') else b''
    path.write_bytes(prefix + content.replace('\n', eol).encode('utf-8'))


def encode(curves, start, end, baked):
    lines = ['      Curves:' if baked else '      curves:']
    for name, keys in curves.items():
        if baked:
            lines.extend([f'      - Name: {name}', '        InSeconds: 0', '        Curve:'])
        else:
            lines.extend([f'      - name: {name}', '        curve:'])
        lines.append('          serializedVersion: 2')
        lines.append('          m_Curve:' if baked else '          curve:')
        for frame, value in keys:
            t = (frame - start) / (end - start)
            lines.extend(['          - serializedVersion: 3', f'            time: {t:.9g}',
                          f'            value: {value:.2g}', '            inSlope: 0', '            outSlope: 0',
                          '            tangentMode: 0', '            weightedMode: 0', '            inWeight: 0', '            outWeight: 0'])
        fields = ('m_PreInfinity', 'm_PostInfinity', 'm_RotationOrder') if baked else ('preInfinity', 'postInfinity', 'rotationOrder')
        lines.extend(f'          {field}: {value}' for field, value in zip(fields, (2, 2, 4)))
    return '\n'.join(lines) + '\n'


asset = database.read_text(encoding='utf-8-sig')
for filename, (clip, file_id, start, end, curves) in clips.items():
    meta = base / (filename + '.fbx.meta')
    source = meta.read_text(encoding='utf-8-sig')
    start_at = source.index(f'      name: {clip}\n')
    curve_at = source.index('      curves: []\n', start_at)
    source = source[:curve_at] + encode(curves, start, end, False) + source[curve_at + len('      curves: []\n'):]
    guid = re.search(r'^guid: (\w+)$', source, re.M)[1]
    anchor = f'    - Clip: {{fileID: {file_id}, guid: {guid}, type: 3}}\n'
    assert asset.count(anchor + '      Curves: []\n') == 1, clip
    asset = asset.replace(anchor + '      Curves: []\n', anchor + encode(curves, start, end, True))
    write_original_eol(meta, source)
write_original_eol(database, asset)
