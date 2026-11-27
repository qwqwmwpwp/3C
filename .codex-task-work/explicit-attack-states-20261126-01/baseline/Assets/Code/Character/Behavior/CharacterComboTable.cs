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

    // 按稳定正整数编号查找招式，列表顺序决定同条件路线的优先级。
    [Serializable]
    internal sealed class CharacterAttackTable
    {
        [InspectorName("允许短突进打断的阶段")] public CharacterBehaviorPhase DashInterruptPhases =
            CharacterBehaviorPhase.Startup | CharacterBehaviorPhase.Recovery;
        [Min(0f), InspectorName("移动起手小步（米）")] public float OpeningStepDistance = 0.25f;
        [InspectorName("起手路线"), Tooltip("只接受按键路线，按列表顺序匹配。")] public CharacterAttackLink[] Openers = Array.Empty<CharacterAttackLink>();
        [InspectorName("攻击状态")] public CharacterAttackRow[] States = Array.Empty<CharacterAttackRow>();

        // 查找指定状态，拒绝未配置编号和重复编号。
        internal CharacterAttackRow Find(int id)
        {
            if (id <= 0 || States == null) return null;
            CharacterAttackRow match = null;
            foreach (var row in States)
                if (row != null && row.Id == id)
                {
                    if (match != null) return null;
                    match = row;
                }
            return match;
        }

        // 捕获整个表的定义与转移数据，后台预测只查阅独立快照。
        internal CharacterAttackTable Snapshot() => new CharacterAttackTable {
            DashInterruptPhases = DashInterruptPhases, OpeningStepDistance = OpeningStepDistance,
            Openers = (CharacterAttackLink[])Openers?.Clone(),
            States = States == null ? null : Array.ConvertAll(States, row => row?.Snapshot())
        };
    }

    // 绑定稳定身份、具体状态数据与由该状态发起的可选转移。
    [Serializable]
    internal sealed class CharacterAttackRow
    {
        [Min(1), InspectorName("状态编号")] public int Id;
        [InspectorName("名称")] public string Name;
        [SerializeReference, InspectorName("状态定义")] public CharacterAttackDefinition Definition = new CharacterAttackMoveSettings();
        [InspectorName("后续路线"), Tooltip("同类候选按列表顺序选择。输入按路线自己的窗口受理。")] public CharacterAttackLink[] Links = Array.Empty<CharacterAttackLink>();

        // 独立复制该行的作者参数和可选路线。
        internal CharacterAttackRow Snapshot() => new CharacterAttackRow { Id = Id, Name = Name,
            Definition = Definition?.Snapshot(), Links = (CharacterAttackLink[])Links?.Clone() };
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
        [Min(1), InspectorName("目标状态编号")] public int To;
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
        internal static CharacterAttackLink ToState(int id) => new CharacterAttackLink { To = id, BlendInEnd = 0.1f, BlendOutStart = 0.9f };

    }

    // 以最初的首招分类限制本条路线。
    internal enum CharacterComboOpeningCondition
    {
        [InspectorName("不限")] Any,
        [InspectorName("移动起手")] Moving,
        [InspectorName("待机起手")] Standing
    }
}
