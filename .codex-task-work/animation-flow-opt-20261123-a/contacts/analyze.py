import json
from math import dist
from pathlib import Path


clips = json.loads(Path(__file__).with_name('bone-samples.json').read_text())
def rounded(seq):
    return '/'.join(f'{val:+.2f}' for val in seq)


idle = clips['SS_Idle'][0]
print('Idle', {key: rounded(idle[key]) for key in idle})
for name, bounds in [('SS_Attack_1', (0, 45)), ('SS_Attack_2', (0, 62)), ('SS_Attack_4', (0, 55)), ('SS_Take', (8, 26))]:
    all_frames = clips[name]
    print(name, 'frames:', bounds, 'foot and ball height (cm), travel per frame (cm), toes xy (m), hand travel per frame (cm)')
    for i in range(bounds[0], bounds[1] + 1):
        pose = all_frames[i]
        previous = all_frames[max(i - 1, 0)]
        parts = []
        for side in ('l', 'r'):
            foot, toe = 'foot_' + side, 'ball_' + side
            values = [pose[foot][2] * 100, pose[toe][2] * 100, dist(pose[foot], previous[foot]) * 100,
                      dist(pose[toe], previous[toe]) * 100, *pose[toe][:2]]
            parts.append(' '.join(f'{v:5.1f}' if n < 4 else f'{v:+.2f}' for n, v in enumerate(values)))
        hand = dist(pose['hand_l'], previous['hand_l']) * 100
        print(f"{i:2d} {'  |  '.join(parts)}  | hand: {hand:5.1f}  root {rounded(pose['root'])}  hips {rounded(pose['pelvis'])}")
