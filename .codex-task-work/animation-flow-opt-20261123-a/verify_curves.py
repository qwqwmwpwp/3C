from pathlib import Path
import yaml, math
work=Path(".codex-task-work/animation-flow-opt-20261123-a")
old=work/"before"
loader=yaml.CSafeLoader
art=Path("Assets/CombatGirlsCharacterPack/CombatGirl_Shield/Animations/Normal")
db=next(Path("Assets/Data/Player/Shield").glob("*地面动作库.asset"))
def load_asset(path):
    return yaml.load(path.read_text(encoding="utf-8-sig").split("MonoBehaviour:",1)[1],Loader=loader)
def meta(path):
    return yaml.load(path.read_text(encoding="utf-8-sig"),Loader=loader)
current=load_asset(db)
original=load_asset(old/db)
all_clips=current["Playback"]["Clips"]
before_clips=original["Playback"]["Clips"]
assert len(all_clips)==len(before_clips)
by_guid={c["Clip"]["guid"]:i for i,c in enumerate(all_clips)}
state_count=curve_count=point_count=0
for name in ["SS_Attack_1","SS_Attack_2","SS_Attack_4","SS_Take"]:
    source=art/(name+".fbx.meta")
    new_meta=meta(source)
    old_meta=meta(old/source)
    assert new_meta["guid"]==old_meta["guid"]
    clip_index=by_guid[new_meta["guid"]]
    baked=all_clips[clip_index]
    original_baked=before_clips[clip_index]
    src=new_meta["ModelImporter"]["animations"]["clipAnimations"]
    prev=old_meta["ModelImporter"]["animations"]["clipAnimations"]
    assert [x["name"] for x in src]==[x["name"] for x in prev]
    changed=[(x,y) for x,y in zip(src,prev) if x!=y]
    assert len(changed)==1, (name,changed)
    clip,oldclip=changed[0]
    mapping={c["name"]:c["curve"] for c in clip["curves"]}
    assert len(mapping)==len(clip["curves"])
    assert set(mapping)=={c["Name"] for c in baked["Curves"]},name
    if name=="SS_Take": assert clip["name"]=="ShieldRaise" and "IKLeftHand" in mapping
    else: assert any(c in mapping for c in ["IKLeftFoot","IKRightFoot"])
    for bc in baked["Curves"]:
        assert bc["InSeconds"]==0
        curve=bc["Curve"]
        source_curve=mapping[bc["Name"]]
        assert curve["m_Curve"]==source_curve["curve"] and curve["serializedVersion"]==source_curve["serializedVersion"], (name,bc["Name"])
        assert all(curve["m_"+key[0].upper()+key[1:]]==source_curve[key] for key in ("preInfinity","postInfinity","rotationOrder"))
        times=[k["time"] for k in curve["m_Curve"]]; values=[k["value"] for k in curve["m_Curve"]]
        assert len(times)>=2 and all(0<=t<=1 for t in times) and times==sorted(set(times))
        assert all(0<=v<=1 for v in values)
        for key in curve["m_Curve"]:
            assert all(math.isfinite(key[k]) for k in ("time","value","inSlope","outSlope"))
        curve_count+=1; point_count+=len(times)
    print(clip["name"],"frames",clip["firstFrame"],clip["lastFrame"],list(mapping))
    clip["curves"]=oldclip["curves"]; assert new_meta==old_meta,(name,"noncurve importer settings changed")
    baked["Curves"]=original_baked["Curves"]; state_count+=1
assert current==original,"unrelated baked data changed"
print("Current author/cache agreement:",state_count,"clips",curve_count,"curves",point_count,"keys; original events, IDs, configs and other baked data preserved.")

