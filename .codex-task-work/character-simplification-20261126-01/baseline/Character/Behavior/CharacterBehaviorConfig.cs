using System;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 配置行为准入、执行参数和初始化时装配的打断关系。
    [CreateAssetMenu(menuName = "3C/角色数据/01 角色行为", fileName = "01 角色行为")]
    public sealed class CharacterBehaviorConfig : ScriptableObject
    {
        [Header("短突进 · 按下时请求")]
        [SerializeField, InspectorName("短突进参数"), Tooltip("每次获准时锁定速度、时长、冷却与消耗。初始体力只在角色初始化或显式重置时读取。")]
        internal CharacterDashSettings Dash = new CharacterDashSettings();

        [Header("持物 / 举盾 · 按住维持，松开结束")]
        [SerializeField, InspectorName("持物打断规则"), Tooltip("角色装配行为时固定打断规则；重置状态会沿用已装配的规则。")]
        internal CharacterGuardSettings Guard = new CharacterGuardSettings();

        [SerializeField, InspectorName("附加行为定义")] internal CharacterBehaviorDefinition[] Additional;

        [Header("基础移动 · 按方向输入移动")]
        [SerializeField, InspectorName("默认步态"), Tooltip("未按步行或持续冲刺键时使用的步态；各档速度由移动与物理资产提供。")]
        internal CharacterMoveGait DefaultGait = CharacterMoveGait.Run;
    }

    [Serializable]
    internal sealed class CharacterGuardSettings
    {
        [InspectorName("允许短突进打断持物")] public bool DashInterrupts = true;
        [InspectorName("允许攻击打断持物")] public bool AttackInterrupts = true;
    }

    /// <summary>固定角色初始化时的行为列表和打断关系。数值配置可在创建预测副本时拍快照。</summary>
    internal sealed class CharacterBehaviorRegistration
    {
        internal readonly bool Guard;
        internal readonly CharacterBehaviorDescriptor[] Descriptors;

        // 初始化时冻结角色的参与者及关系，各流程构造时再在主线程解析自己使用的数值。
        internal CharacterBehaviorRegistration(bool allowGuard, CharacterAttackTable attack, CharacterGuardSettings guard, CharacterBehaviorDefinition[] additional = null)
        {
            Guard = allowGuard;
            var entries = new System.Collections.Generic.List<CharacterBehaviorDescriptor> {
                CharacterBehaviorAssembly.Move, CharacterBehaviorAssembly.Dash
            };
            if (allowGuard)
                entries.Add(new CharacterBehaviorDescriptor(CharacterBehaviorId.Guard, _ => new CharacterGuardBehavior(),
                    conflicts: CharacterBehaviorId.Dash, interrupts: guard?.DashInterrupts != false
                        ? new[] { new CharacterBehaviorInterrupt(CharacterBehaviorId.Dash, CharacterBehaviorId.Guard) } : null));
            if (attack?.States is { Length: > 0 })
            {
                var interrupts = new System.Collections.Generic.List<CharacterBehaviorInterrupt> {
                    new CharacterBehaviorInterrupt(CharacterBehaviorId.Attack, CharacterBehaviorId.Move)
                };
                if (allowGuard && guard?.AttackInterrupts != false)
                    interrupts.Add(new CharacterBehaviorInterrupt(CharacterBehaviorId.Attack, CharacterBehaviorId.Guard));
                var phases = attack.DashInterruptPhases & CharacterBehaviorPhase.Any;
                if (phases != 0) interrupts.Add(new CharacterBehaviorInterrupt(CharacterBehaviorId.Dash, CharacterBehaviorId.Attack, phases));
                entries.Add(new CharacterBehaviorDescriptor(CharacterBehaviorId.Attack, snapshot => new CharacterAttackBehavior(
                    snapshot ? attack.Snapshot() : attack),
                    80, allowGuard ? CharacterBehaviorId.Guard : default, motion: CharacterBehaviorMotionCapability.ExactInterval,
                    interrupts: interrupts.ToArray()));
            }
            if (additional != null)
                foreach (var definition in additional)
                {
                    if (!definition) throw new ArgumentException("附加行为定义引用为空。");
                    var entry = definition.Capture() ?? throw new ArgumentException("附加行为缺少注册描述。");
                    if ((entry.Id & (CharacterBehaviorId.Move | CharacterBehaviorId.Dash | CharacterBehaviorId.Guard | CharacterBehaviorId.Attack)) != 0)
                        throw new ArgumentException($"附加行为 {entry.Id} 使用了内置编号。");
                    entries.Add(entry);
                }
            Descriptors = entries.ToArray();
        }
    }
}
