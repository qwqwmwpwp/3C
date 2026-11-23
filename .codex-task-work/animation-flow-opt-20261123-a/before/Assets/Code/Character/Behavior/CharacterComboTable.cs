using System;
using ThreeC.Player;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    [CreateAssetMenu(menuName = "3C/角色数据/02 连招表", fileName = "02 连招表")]
    public sealed class CharacterComboTable : ScriptableObject
    {
        [SerializeField, InspectorName("接续配置")] internal CharacterComboRoutes Routes = new CharacterComboRoutes();
    }

    [Serializable]
    internal struct CharacterAttackLink
    {
        [InspectorName("当前招式")] public CharacterAttackMove From;
        [InspectorName("按键")] public AttackButton Button;
        [InspectorName("下一招")] public CharacterAttackMove To;

        public CharacterAttackLink(CharacterAttackMove from, AttackButton button, CharacterAttackMove to)
        {
            From = from;
            Button = button;
            To = to;
        }
    }

    [Serializable]
    internal class CharacterComboRoutes
    {
        [Header("输入窗口 · 招式归一化进度")]
        [Range(0f, 1f), InspectorName("开始预输入")] public float BufferStart = 0.40f;
        [Range(0f, 1f), InspectorName("开放接续")] public float LinkStart = 0.58f;
        [Range(0f, 1f), InspectorName("关闭接续")] public float LinkEnd = 0.85f;
        [Header("接续列表 · 当前招式 → 输入 → 下一招")]
        [InspectorName("接续表"), Tooltip("当前招式选“无（起手）”表示第一招。同一招式和按键取首条匹配。")]
        public CharacterAttackLink[] Links =
        {
            new CharacterAttackLink(CharacterAttackMove.None, AttackButton.Light, CharacterAttackMove.LightOne),
            new CharacterAttackLink(CharacterAttackMove.None, AttackButton.Heavy, CharacterAttackMove.Heavy),
            new CharacterAttackLink(CharacterAttackMove.LightOne, AttackButton.Light, CharacterAttackMove.LightTwo),
            new CharacterAttackLink(CharacterAttackMove.LightOne, AttackButton.Heavy, CharacterAttackMove.Heavy),
            new CharacterAttackLink(CharacterAttackMove.LightTwo, AttackButton.Heavy, CharacterAttackMove.Heavy)
        };

        public CharacterAttackMove Next(CharacterAttackMove current, AttackButtonPresses presses)
        {
            var first = Find(current, presses.First);
            return first != CharacterAttackMove.None ? first : Find(current, presses.Second);
        }

        private CharacterAttackMove Find(CharacterAttackMove current, AttackButton button)
        {
            if (button == AttackButton.None || Links == null) return CharacterAttackMove.None;
            foreach (var link in Links)
                if (link.From == current && link.Button == button) return link.To;
            return CharacterAttackMove.None;
        }
    }
}
