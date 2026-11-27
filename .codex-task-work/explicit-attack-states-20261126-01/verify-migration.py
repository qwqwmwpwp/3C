from pathlib import Path
import yaml

task = Path(__file__).resolve().parent
project = task.parent.parent
asset = Path("Assets/Data/Player/Shield/02 连招表.asset")

def load(path):
    return yaml.safe_load(path.read_text("utf-8").split("MonoBehaviour:\n", 1)[1])

old = load(task / "baseline" / asset)
new = load(project / asset)
old_data = old.pop("Data")
new_data = new.pop("Data")
references = {entry["rid"]: entry["data"] for entry in old.pop("references")["RefIds"]}
assert old == new
states = old_data.pop("States")
assert len(states) == len(references) == 4
for state, name, stable_id in zip(states, ("LightOne", "LightTwo", "Heavy", "SkillTwo"), range(1, 5)):
    slot = new_data.pop(name)
    assert state["Id"] == stable_id
    assert slot == {"Name": state["Name"], "Links": state["Links"], **references[state["Definition"]["rid"]]}
    print(name, stable_id, len(slot["Links"]), "links,", slot["CooldownSeconds"], "cooldown,", slot["Clip"]["guid"])
assert old_data == new_data
meta = asset.with_suffix(".asset.meta")
assert (task / "baseline" / meta).read_bytes() == (project / meta).read_bytes()
print("Every slot, route, gameplay value and the asset metadata match the baseline.")
