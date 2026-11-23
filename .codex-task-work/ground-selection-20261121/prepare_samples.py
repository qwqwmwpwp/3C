from pathlib import Path
import json
p=Path('.codex-task-work/ground-selection-20261121')
src=Path('.codex-task-work/run-motion-match/bake_source.py').read_text()
s=src[:src.index('output =')] + """output = Path('.codex-task-work/ground-selection-20261121')
all_tracks = {}
ranges=json.loads((output/'ranges.json').read_text())
for src, spans in [(output/'LocomotionTurns.fbx',ranges),
    (root/'Normal/SS_Sprint.fbx',[('SS_Sprint',0,18)]),
    (stop,[(m[0],int(m[1]),int(m[2])) for m in re.findall(r'      name: (\\w+)_PLACEHOLDER', '')])]:
""" + src[src.index('    bpy.ops.wm'):src.index('        if name.startswith')] + '(output/"samples.json").write_text(json.dumps(all_tracks))'
import re
ranges=re.findall(r'      name: (\w+_stop_\w+).*?      firstFrame: (\d+).*?      lastFrame: (\d+)',Path('Assets/player/LocomotionStops.fbx.meta').read_text(),re.S)
s=s[:s.index('    (stop,')]+ '    (stop,'+repr([(n,int(a),int(b)) for n,a,b in ranges])+')]:\n'+s[s.index('    bpy.ops.wm'):]
(p/'sample.py').write_text(s)

