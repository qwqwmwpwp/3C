using System;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 固定四个攻击状态的序列化编号，供路线、动画与公开观测共用。
    public enum CharacterAttackMove
    {
        [InspectorName("无")] None = 0,
        [InspectorName("轻击一")] LightOne = 1,
        [InspectorName("轻击二")] LightTwo = 2,
        [InspectorName("重击")] Heavy = 3,
        [InspectorName("技能二")] SkillTwo = 4
    }

    // 保存本步最终执行状态及其动画时钟、实例号和预输入观测。
    public readonly struct CharacterAttackState
    {
        public int StateId { get; }
        public string Name { get; }
        [Obsolete("请使用 StateId。")]
        public CharacterAttackMove Move => (CharacterAttackMove)StateId;
        public long InstanceId { get; }
        public CharacterBehaviorPhase Phase { get; }
        public bool ReturnsToMovement { get; }
        public float StartProgress { get; }
        public float Progress { get; }
        public float Duration { get; }
        internal CharacterActionHandoff Handoff { get; }
        public int BufferedStateId { get; }
        [Obsolete("请使用 BufferedStateId。")]
        public CharacterAttackMove BufferedMove => (CharacterAttackMove)BufferedStateId;
        public float LinkAtSeconds { get; }
        public string BufferResult { get; }

        // 将运行实例此刻的时钟及当前转移的交接参数冻结成公开观测。
        internal CharacterAttackState(int id, string name, long instance, CharacterAttackExecution execution, float start,
            Vector2 blend, in CharacterAttackPending pending, string result)
        {
            StateId = id;
            Name = name;
            InstanceId = instance;
            Phase = execution.Phase;
            ReturnsToMovement = execution.ReturnsToMovement;
            StartProgress = start;
            Progress = execution.Progress;
            Duration = execution.Duration;
            Handoff = new CharacterActionHandoff(Duration, blend, execution.RecoveryMoveStart);
            BufferedStateId = pending.Target.Id;
            LinkAtSeconds = (float)pending.At;
            BufferResult = result;
        }
    }

    // 携带获准候选的目标编号、冻结参数及本路线的表现交接。
    internal readonly struct CharacterAttackTarget
    {
        internal readonly int Id;
        internal readonly string Name;
        internal readonly Vector2 BlendRange;
        internal readonly CharacterAttackDefinition Definition;

        // 锁定目标身份和定义，后续编辑不会改写已受理的请求。
        internal CharacterAttackTarget(int id, string name, Vector2 blendRange, CharacterAttackDefinition frozenDefinition)
        {
            Id = id;
            Name = name;
            BlendRange = blendRange;
            Definition = frozenDefinition;
        }

        // 为另一个流程复制待执行目标定义。
        internal CharacterAttackTarget Snapshot() => Id == 0 ? default : new CharacterAttackTarget(
            Id, Name, BlendRange, Definition.Snapshot());
    }

    // 保存一个已接受输入的执行时刻、有效期及请求身份。
    internal struct CharacterAttackPending
    {
        internal CharacterAttackTarget Target;
        internal double At, Expires;
        internal long Request;
    }

    // 将已经选定的首招随外层行为准入申请交给攻击状态机。
    internal sealed class CharacterAttackStart : CharacterBehaviorStart
    {
        internal readonly CharacterAttackTarget Target;

        // 保存攻击准入时的角色上下文、报价和目标。
        internal CharacterAttackStart(CharacterBehaviorContext context, float cost, CharacterAttackTarget target)
            : base(context, cost) => Target = target;
    }
}
