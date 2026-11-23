from pathlib import Path


ids = {'SS_Attack1': -7391068719408809722, 'SS_Attack2': -4705555360703855209,
       'SS_Attack4': -5630466525676994369, 'ShieldRaise': 740381002100001}
for artifact in Path('Library/Artifacts').glob('*/*'):
    if not (20000 <= artifact.stat().st_size <= 10000000):
        continue
    data = artifact.read_bytes()
    hits = [name for name, uid in ids.items() if name.encode() in data and uid.to_bytes(8, 'little', signed=True) in data]
    if hits:
        print(artifact.as_posix(), artifact.stat().st_mtime, len(data), hits)
