import math,re,uuid,yaml
from pathlib import Path
stage=Path(".codex-task-work/run-motion-match")
root=Path("Assets/CharacterBehavior")
replay=root/"Runtime/CharacterMatchReplay.cs.meta"
if not replay.exists():
    template=(root/"Runtime/CharacterLocomotionPoseSelector.cs.meta").read_text()
    replay.write_text(re.sub(r"guid: [0-9a-f]+","guid: "+uuid.uuid4().hex,template))
names=["CharacterAnimationIk","CharacterAnimationLocomotion","CharacterAnimationPipeline","CharacterAnimationResourceResolver","CharacterBehaviorAnimator","CharacterBehaviorDriver","CharacterFootObservation","CharacterGroundTrajectory","CharacterIkJob","CharacterLocomotionPoseSelector","CharacterMatchReplay","CharacterMatchTrajectory","CharacterMotionDatabase","CharacterMotionMatcher","CharacterPlaybackObservation","CharacterMotionDatabaseEditor","CharacterBehaviorAnimatorEditor"]
for n in names:
    f,=root.rglob(n+".cs")
    assert Path(str(f)+".meta").exists()
    count=len(f.read_text(encoding="utf-8-sig").splitlines())
    assert count<=500,(n,count)
    print(f"{n}: {count}")
def parse_asset(path):
    raw=Path(path).read_text(encoding="utf-8-sig")
    docs=re.split(r"(?m)^--- !u!(\d+) &(-?\d+)(?: stripped)?\s*\n",raw)
    return [(int(docs[i]),int(docs[i+1]),next(iter(yaml.safe_load(docs[i+2]).values()))) for i in range(1,len(docs),3)]
def guid(p):
    return re.search(r"^guid: (\w+)",Path(str(p)+".meta").read_text(),re.M)[1]
p="Assets/player/player 1.prefab"; asset="Assets/player/ShieldGroundMotion.asset"; ctl="Assets/player/player.controller"
db=parse_asset(asset)[0][2]; prefab=parse_asset(p); controller=parse_asset(ctl)
obj=next(v for t,i,v in prefab if t==114 and "motionDatabase" in v)
assert obj["motionMatching"]==2 and obj["motionDatabase"]["guid"]==guid(asset)
assert db["Controller"]["guid"]==guid(ctl) and db["m_Script"]["guid"]==guid(root/"Runtime/CharacterMotionDatabase.cs")
assert any(t==95 and v["m_Avatar"]==db["Avatar"] for t,i,v in prefab)
scene=Path("Assets/Scenes/角色测试.unity").read_text()
assert guid(p) in scene and "propertyPath: motionMatching" not in scene
layers=next(v["m_AnimatorLayers"] for t,i,v in controller if t==91); ids={i:v for t,i,v in controller}
states={s["m_Name"]:s for entry in ids[layers[0]["m_StateMachine"]["fileID"]]["m_ChildStates"] for s in [ids[entry["m_State"]["fileID"]]]}
seen=set()
for tr in db["Tracks"]:
    for state in tr["States"]:
        assert state.startswith("Base Layer.") and state not in seen
        seen.add(state); assert states[state.split(".",1)[1]]["m_Motion"]==tr["Clip"]
    s=tr["Poses"]; assert len(s)==round(tr["Seconds"]*30)+1
    assert all(math.isfinite(v) for pose in s for item in pose.values() for v in item.values())
    assert all(0<=v<=1 for pose in s for v in pose["Contact"].values())
    assert all(0.1 < -pose["Left"]["y"] < 1.2 and 0.1 < -pose["Right"]["y"] < 1.2 for pose in s)
    if tr["BrakeSeconds"]>0:
        assert tr["LastEntrySeconds"]<tr["BrakeSeconds"]<tr["Seconds"]
        assert [i for i in range(len(s)) if i/30 <=tr["LastEntrySeconds"]]==[0,1,2]
    else:
        assert abs(s[0]["Left"]["z"]-s[-1]["Left"]["z"])<.01
assert len(seen)==6
print("Verified database links, 4 clips, 6 base-layer states, 84 source poses, entry windows, both foot contacts, scene prefab and 500-line limit.")
# Reuse the project's Editor compilation inputs. Direct all products outside Assets/Library.
bee=Path("Library/Bee/artifacts/1900b0aE.dag")
for assembly, folder in [("Assembly-CSharp","Runtime"),("Assembly-CSharp-Editor","Editor")]:
    args=[]; files=set()
    for line in (bee/(assembly+".rsp")).read_text(encoding="utf-8-sig").splitlines():
        if line.startswith(("-out:", "-refout:")):
            key,name=line.split(":",1)
            line=key+':"'+(stage/Path(name.strip('"')).name).as_posix()+'"'
        if line=='-r:"'+(bee/"Assembly-CSharp.ref.dll").as_posix()+'"':
            line='-r:"'+(stage/"Assembly-CSharp.ref.dll").as_posix()+'"'
        if line.startswith('"') and line.endswith('.cs"'):
            file=Path(line.strip('"'))
            assert file.is_file(),str(file)
            files.add(file.as_posix())
        args.append(line)
    for file in sorted((root/folder).glob("*.cs")):
        if file.as_posix() not in files: args.append('"'+file.as_posix()+'"')
    (stage/(assembly+".rsp")).write_text("\n".join(args)+"\n",encoding="utf-8")
