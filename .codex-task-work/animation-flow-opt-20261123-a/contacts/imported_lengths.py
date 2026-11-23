import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).with_name('deps')))
import UnityPy
UnityPy.config.FALLBACK_UNITY_VERSION = Path('ProjectSettings/ProjectVersion.txt').read_text().split()[1]


artifacts = [
    ('33/333add47b7da2b136212209770852448', -7391068719408809722),
    ('b1/b1381a6950a9a0c90c802f016811e294', -4705555360703855209),
    ('e5/e599b6edb764201e204212e8cf4b1c55', -5630466525676994369),
    ('cb/cb86c23455effd760508fba9a2d74d74', 740381002100001),
]
for path, clip_id in artifacts:
    env = UnityPy.load(str(Path('Library/Artifacts') / path))
    obj = next(item for item in env.objects if item.path_id == clip_id)
    clip = obj.read_typetree()
    row = clip['m_AnimationClipSettings']
    print(clip_id, clip['m_Name'], clip['m_SampleRate'], row['m_StartTime'], row['m_StopTime'])
