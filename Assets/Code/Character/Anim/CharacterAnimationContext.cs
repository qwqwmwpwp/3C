using System;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 运动来源提交的目标步态，默认使用跑步档。
    public enum AnimationGait { Run, Walk, Sprint }

    // 区分普通移动、可覆盖步态的运动和其他位移来源。
    public enum AnimationMotionSource { None, Locomotion, Override, Other }

    // 保存一组稳定、区分大小写的动画状态键；构造后可安全交给后台选姿。
    public readonly struct AnimationStateSet
    {
        private readonly string[] keys;
        private readonly ulong? activeBits;

        // 复制来源的状态键，剔除空键及重复项，后续提交可以共享此值。
        public AnimationStateSet(params string[] activeKeys)
        {
            activeBits = null;
            if (activeKeys == null || activeKeys.Length == 0)
            {
                keys = null;
                return;
            }
            var values = new System.Collections.Generic.List<string>(activeKeys.Length);
            foreach (string key in activeKeys)
            {
                if (!string.IsNullOrWhiteSpace(key) && !values.Contains(key)) values.Add(key);
            }
            keys = values.ToArray();
        }

        // 共用预先登记的状态键顺序，按不超过 64 位的来源状态切换当前活动集合。
        public AnimationStateSet WithActiveBits(ulong bits) => new AnimationStateSet(keys, bits);

        // 共用不可修改的键表，因此每个新状态组合只复制一个值。
        private AnimationStateSet(string[] keys, ulong bits)
        {
            this.keys = keys;
            activeBits = bits;
        }

        // 在已冻结的状态集合中查找一个候选表要求的标识。
        public bool Contains(string key)
        {
            int index = keys == null ? -1 : Array.IndexOf(keys, key);
            return index >= 0 && (!activeBits.HasValue || index < 64 && (activeBits.Value & (1UL << index)) != 0);
        }

        // 为编辑器诊断列出本次匹配实际使用的活动状态。
        public override string ToString()
        {
            if (keys == null) return "无";
            var label = new System.Text.StringBuilder();
            foreach (var key in keys)
            {
                if (!Contains(key)) continue;
                if (label.Length > 0) label.Append(", ");
                label.Append(key);
            }
            return label.Length > 0 ? label.ToString() : "无";
        }
    }

    // 表达选姿和步态需要的当前状态；覆盖键指向本角色动作表中的资源。
    public struct CharacterAnimationContext
    {
        public AnimationStateSet States;
        public bool MoveApproved, MoveEnded, CanStartMoving;
        public AnimationMotionSource MotionSource;
        public string OverrideKey;
        public bool OverrideStarted, ActionActive, RecoversMovement;
    }
}
