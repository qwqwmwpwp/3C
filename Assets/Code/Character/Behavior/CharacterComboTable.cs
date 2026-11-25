using System;
using ThreeC.Player;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 提供角色起手及接续请求查阅的招式路线与输入窗口。
    [CreateAssetMenu(menuName = "3C/角色数据/02 连招表", fileName = "02 连招表")]
    public sealed class CharacterComboTable : ScriptableObject
    {
        [SerializeField, InspectorName("接续配置"), Tooltip("下一次输入按当前路线与窗口判断，已接受的预输入保留原招式与接入时刻。")]
        internal CharacterComboRoutes Routes = new CharacterComboRoutes();

#if UNITY_EDITOR
        private void OnValidate() =>
            CharacterComboValidation.CheckRoutes(Routes, problem => Debug.LogWarning($"{name} / {problem}", this));
#endif
    }

    [Serializable]
    internal struct CharacterAttackLink
    {
        [InspectorName("按键")] public AttackButton Button;
        [InspectorName("下一招")] public CharacterAttackMove To;
        [InspectorName("起手条件")] public CharacterComboOpeningCondition OpeningCondition;
        [Range(0f, 1f), InspectorName("缓入结束比例"), Tooltip("下一招的动画在此进度完成缓入。")]
        public float BlendInEnd;
        [Range(0f, 1f), InspectorName("缓出开始比例"), Tooltip("下一招的动画从此进度开始缓出。")]
        public float BlendOutStart;
        private const float DefaultBlendIn = 0.1f, DefaultBlendOut = 0.9f;

        internal bool AllowsOpening(bool moving) => OpeningCondition == CharacterComboOpeningCondition.Any ||
            OpeningCondition == (moving ? CharacterComboOpeningCondition.Moving : CharacterComboOpeningCondition.Standing);

        internal bool HasValidBlend => BlendInEnd >= 0f && BlendInEnd <= BlendOutStart && BlendOutStart <= 1f;
        internal Vector2 BlendRange => HasValidBlend ? new Vector2(BlendInEnd, BlendOutStart) :
            new Vector2(DefaultBlendIn, DefaultBlendOut);

        public CharacterAttackLink(AttackButton button, CharacterAttackMove to)
        {
            Button = button;
            To = to;
            OpeningCondition = CharacterComboOpeningCondition.Any;
            BlendInEnd = DefaultBlendIn;
            BlendOutStart = DefaultBlendOut;
        }
    }

    internal enum CharacterComboOpeningCondition
    {
        [InspectorName("无")] Any,
        [InspectorName("移动起手")] Moving,
        [InspectorName("待机起手")] Standing
    }

    [Serializable]
    internal sealed class CharacterComboMoveRoutes
    {
        [Min(0f), InspectorName("提前输入（秒）"), Tooltip("允许在开放接续前多少秒记住按键。")]
        public float EarlySeconds;
        [Range(0f, 1f), InspectorName("开放接续"), Tooltip("按当前招式进度计算，0.6 表示 60%。")]
        public float LinkStart;
        [Range(0f, 1f), InspectorName("关闭接续"), Tooltip("按当前招式进度计算，0.7 表示 70%。")]
        public float LinkEnd;
        [InspectorName("后续路线"), Tooltip("同一按键按列表顺序匹配；列表为空表示本招没有后续路线。")]
        public CharacterAttackLink[] Links = Array.Empty<CharacterAttackLink>();

        internal bool HasValidWindow => EarlySeconds >= 0f && !float.IsInfinity(EarlySeconds) &&
            LinkStart >= 0f && LinkStart <= LinkEnd && LinkEnd > 0f && LinkEnd <= 1f;

        internal CharacterComboMoveRoutes Snapshot()
        {
            var copy = (CharacterComboMoveRoutes)MemberwiseClone();
            copy.Links = (CharacterAttackLink[])Links?.Clone();
            return copy;
        }
    }

    [Serializable]
    internal struct CharacterComboRouteEntry
    {
        [InspectorName("来源招式")] public CharacterAttackMove Id;
        [InspectorName("接续配置")] public CharacterComboMoveRoutes Routes;

        internal CharacterComboRouteEntry Snapshot() =>
            new CharacterComboRouteEntry { Id = Id, Routes = Routes?.Snapshot() };
    }

    [Serializable]
    internal class CharacterComboRoutes
    {
        [InspectorName("起手路线"), Tooltip("从空闲状态出招。起手只检查按键和起手条件。")]
        public CharacterAttackLink[] Openers = Array.Empty<CharacterAttackLink>();
        [InspectorName("轻击一")] public CharacterComboMoveRoutes LightOneRoutes = new CharacterComboMoveRoutes();
        [InspectorName("轻击二")] public CharacterComboMoveRoutes LightTwoRoutes = new CharacterComboMoveRoutes();
        [InspectorName("重击")] public CharacterComboMoveRoutes HeavyRoutes = new CharacterComboMoveRoutes();
        [InspectorName("技能 2")] public CharacterComboMoveRoutes SkillTwoRoutes = new CharacterComboMoveRoutes();

        [InspectorName("追加招式路线")] public CharacterComboRouteEntry[] Additional;

        internal CharacterComboMoveRoutes ForMove(CharacterAttackMove move) => move switch
        {
            CharacterAttackMove.LightOne => LightOneRoutes,
            CharacterAttackMove.LightTwo => LightTwoRoutes,
            CharacterAttackMove.Heavy => HeavyRoutes,
            CharacterAttackMove.SkillTwo => SkillTwoRoutes,
            CharacterAttackMove.None => null,
            _ => AdditionalRoute(move)
        };

        private CharacterComboMoveRoutes AdditionalRoute(CharacterAttackMove move)
        {
            if (Additional != null)
                foreach (var row in Additional)
                    if (row.Id == move) return row.Routes;
            return null;
        }

        internal CharacterComboRoutes Snapshot()
        {
            var copy = (CharacterComboRoutes)MemberwiseClone();
            copy.Openers = (CharacterAttackLink[])Openers?.Clone();
            copy.LightOneRoutes = LightOneRoutes?.Snapshot();
            copy.LightTwoRoutes = LightTwoRoutes?.Snapshot();
            copy.HeavyRoutes = HeavyRoutes?.Snapshot();
            copy.SkillTwoRoutes = SkillTwoRoutes?.Snapshot();
            copy.Additional = Additional == null ? null : Array.ConvertAll(Additional, row => row.Snapshot());
            return copy;
        }

        internal static CharacterAttackLink Find(CharacterAttackLink[] links, AttackButton button, bool? moving = null)
        {
            if (button == AttackButton.None || links == null) return default;
            foreach (var link in links)
                if (link.Button == button && (!moving.HasValue || link.AllowsOpening(moving.Value))) return link;
            return default;
        }
    }
}
