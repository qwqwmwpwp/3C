"""Check the player input, combo route and authored playback end to end without Unity."""
import json
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[2]
SHIELD = ROOT / 'Assets/Data/Player/Shield'

def read(path):
    return path.read_text(encoding='utf-8-sig')

class ShieldComboAssets(unittest.TestCase):
    def test_combat_e_and_sprint_r_are_distinct(self):
        base = ROOT / 'Assets/Data/Player/Input'
        for file, key, action in [(base/'Resources/PlayerCombat.inputactions','e','Skill'),
                                  (base/'GameInput.inputactions','r','Sprint')]:
            maps = json.loads(read(file))['maps']
            self.assertTrue(any(b['path'].lower() == f'<keyboard>/{key}' and b['action'] == action
                                for m in maps for b in m['bindings']))

    def test_authored_routes_and_move_windows(self):
        table = read(SHIELD/'02 连招表.asset').split('  Routes:\n', 1)[1]
        sections = dict(re.findall(r'^    (\w+):(.*?)(?=^    \w+:|\Z)', table, re.M | re.S))
        expected = {
            'Openers': [('1', '1'), ('2', '3')],
            'LightOneRoutes': [('1', '2'), ('2', '3'), ('3', '4')],
            'LightTwoRoutes': [('2', '3')],
            'HeavyRoutes': [], 'SkillTwoRoutes': []
        }
        self.assertEqual(sections.keys(), expected.keys())
        for name, rows in expected.items():
            with self.subTest(move=name):
                section = sections[name]
                self.assertEqual(re.findall(r'- Button: (\d+)\s+To: (\d+)', section), rows)
                if rows and name != 'Openers':
                    timing = dict(re.findall(r'^      (EarlySeconds|LinkStart|LinkEnd): (\S+)', section, re.M))
                    early, start, end = (float(timing[key]) for key in ('EarlySeconds', 'LinkStart', 'LinkEnd'))
                    self.assertGreaterEqual(early, 0)
                    self.assertTrue(0 <= start <= end <= 1 and end > 0)

    def test_skill_clip_and_playback_agree(self):
        meta = read(ROOT / 'Assets/CombatGirlsCharacterPack/CombatGirl_Shield/Animations/Special/SS_Sp_Skill_2.fbx.meta')
        guid = re.search(r'^guid: (\w+)', meta, re.M)[1]
        clip = re.search(r'74: (-?\d+)\s+second: SS_Sp_Skill2', meta)[1]
        reference = rf'fileID: {clip},\s+guid: {guid}'
        self.assertRegex(read(SHIELD / '01 角色行为.asset'), reference)
        modules = read(SHIELD / 'Modules/连招攻击.asset')
        self.assertRegex(modules, r'AttackMove: 4\s+EntryAction: full\.skill_2')
        state = 'Lower Body.Actions.skill_2'
        self.assertIn(state, modules)

        groups_path = SHIELD / '盾牌基础动画组.asset'
        groups_guid = re.search(r'^guid: (\w+)', read(Path(str(groups_path) + '.meta')), re.M)[1]
        library = read(SHIELD / '盾牌地面动作库.asset')
        self.assertRegex(library, rf'Groups: \{{fileID: 11400000,\s+guid: {groups_guid}')
        for asset in (read(groups_path), library):
            self.assertRegex(asset, rf'Path: {re.escape(state)}\s+Clip: \{{{reference}')
        self.assertIn('FromGroups: 1', library)

if __name__=='__main__':
    unittest.main()

