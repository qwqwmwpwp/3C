using System;
using ThreeC.Player;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 汇总角色攻击状态的定义、起手入口和各状态的可选转移。
    [CreateAssetMenu(menuName = "3C/角色数据/02 攻击状态表", fileName = "02 攻击状态表")]
    public sealed class CharacterComboTable : ScriptableObject
    {
        [SerializeField, InspectorName("攻击状态总表")] internal CharacterAttackTable Data = new CharacterAttackTable();

#if UNITY_EDITOR
        // 编辑表时检查纯数据引用；片段读取留给主线程的角色配置检查。
        private void OnValidate() => CharacterComboValidation.CheckTable(Data, false, problem => Debug.LogWarning($"{name} / {problem}", this));
#endif
    }

    // 持有四个具名攻击状态及其公共起手规则，状态身份由字段固定。
    [Serializable]
    internal sealed class CharacterAttackTable
    {
        [InspectorName("允许短突进打断的阶段")] public CharacterBehaviorPhase DashInterruptPhases =
            CharacterBehaviorPhase.Startup | CharacterBehaviorPhase.Recovery;
        [Min(0f), InspectorName("移动起手小步（米）")] public float OpeningStepDistance = 0.25f;
        [InspectorName("起手路线"), Tooltip("只接受按键路线，按列表顺序匹配。")] public CharacterAttackLink[] Openers = Array.Empty<CharacterAttackLink>();

        [InspectorName("轻击一")] public CharacterAttackMoveSettings LightOne = new CharacterAttackMoveSettings { Name = "轻击一" };
        [InspectorName("轻击二")] public CharacterAttackMoveSettings LightTwo = new CharacterAttackMoveSettings { Name = "轻击二" };
        [InspectorName("重击")] public CharacterAttackMoveSettings Heavy = new CharacterAttackMoveSettings { Name = "重击" };
        [InspectorName("技能二")] public CharacterAttackMoveSettings SkillTwo = new CharacterAttackMoveSettings { Name = "技能二" };

        // 将路线和动画映射使用的稳定编号解析到对应的具名字段。
        internal CharacterAttackDefinition Find(int id) => (CharacterAttackMove)id switch {
            CharacterAttackMove.LightOne => LightOne,
            CharacterAttackMove.LightTwo => LightTwo,
            CharacterAttackMove.Heavy => Heavy,
            CharacterAttackMove.SkillTwo => SkillTwo,
            _ => null
        };

        // 冻结四招的执行参数与路线，预测线程只读取本次捕获的数据。
        internal CharacterAttackTable Snapshot()
        {
            var copy = (CharacterAttackTable)MemberwiseClone();
            copy.Openers = (CharacterAttackLink[])Openers?.Clone();
            copy.LightOne = LightOne?.Capture(true);
            copy.LightTwo = LightTwo?.Capture(true);
            copy.Heavy = Heavy?.Capture(true);
            copy.SkillTwo = SkillTwo?.Capture(true);
            return copy;
        }
    }

    // 指定按键、归一化时刻、自然完成或自定义结果触发的转移。
    internal enum CharacterAttackTrigger
    {
        [InspectorName("按键")] Input,
        [InspectorName("到达指定进度")] Time,
        [InspectorName("状态完成")] Completed,
        [InspectorName("状态结果")] Result
    }

    // 定义单条转移的触发、目标、预输入窗口与目标动画权重。
    [Serializable]
    internal struct CharacterAttackLink
    {
        [InspectorName("触发方式")] public CharacterAttackTrigger Trigger;
        [InspectorName("按键")] public AttackButton Button;
        [InspectorName("目标状态")] public CharacterAttackMove To;
        internal int TargetId => (int)To;
        [InspectorName("首招移动条件")] public CharacterComboOpeningCondition OpeningCondition;
        [Range(0f, 1f), InspectorName("开放接续 / 定时转移进度"), Tooltip("使用状态报告的归一化进度。自定义进度由状态报告变化边界。")] public float LinkStart;
        [Range(0f, 1f), InspectorName("关闭输入进度")] public float LinkEnd;
        [Min(0f), InspectorName("提前输入（秒）"), Tooltip("按固定线性时钟换算开放时刻。无固定时长或自定义进度的状态填 0，仅在窗口内即时接续。")] public float EarlySeconds;
        [InspectorName("匹配状态结果（非零）")] public int Result;
        [Range(0f, 1f), InspectorName("目标动画缓入结束比例")] public float BlendInEnd;
        [Range(0f, 1f), InspectorName("目标动画缓出开始比例")] public float BlendOutStart;
        internal bool HasValidBlend => BlendInEnd >= 0f && BlendInEnd <= BlendOutStart && BlendOutStart <= 1f;
        internal Vector2 BlendRange => HasValidBlend ? new Vector2(BlendInEnd, BlendOutStart) : new Vector2(0.1f, 0.9f);
        internal bool HasValidTime => LinkStart >= 0f && LinkStart <= 1f;
        internal bool HasValidWindow => HasValidTime && LinkStart <= LinkEnd && LinkEnd > 0f && LinkEnd <= 1f &&
            EarlySeconds >= 0f && !float.IsInfinity(EarlySeconds);

        // 按本次连招最初获得许可时的地速分类匹配路线。
        internal bool AllowsOpening(bool moving) => OpeningCondition == CharacterComboOpeningCondition.Any ||
            OpeningCondition == (moving ? CharacterComboOpeningCondition.Moving : CharacterComboOpeningCondition.Standing);

        // 为专属状态的动态请求提供统一目标及默认表现交接。
        internal static CharacterAttackLink ToState(int id) => new CharacterAttackLink { To = (CharacterAttackMove)id, BlendInEnd = 0.1f, BlendOutStart = 0.9f };

    }

    // 以最初的首招分类限制本条路线。
    internal enum CharacterComboOpeningCondition
    {
        [InspectorName("不限")] Any,
        [InspectorName("移动起手")] Moving,
        [InspectorName("待机起手")] Standing
    }
}
