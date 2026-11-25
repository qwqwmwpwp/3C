using System;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 为当前层进入或退出选择纯淡变、纯惯性或受控组合。
    internal enum TransitionMethod { Crossfade, Inertial, Combined }
    // 区分声部进入和可见层退出边沿。
    internal enum TransitionEdge { Any, Enter, Exit }
    // 在基础、上下身和全身接管层之间限定策略适用范围。
    internal enum TransitionLayer { Any, Baseline, UpperBody, LowerBody, FullBodyOverlay }
    // 按已选择的动作语义匹配过渡策略。
    internal enum TransitionPurpose { Any, Locomotion, Start, Stop, Turn, Action, Idle }

    // 指定一个骨骼分组的衰减、旋转和平移限位与最终约束抑制。
    [Serializable] internal struct TransitionGroupSettings
    {
        [Min(0.02f)] public float Seconds;
        [Min(0f)] public float Degrees, Position;
        [Range(0f, 1f)] public float Constraint;
    }

    // 将切换上下文映射到片段淡变方式和各部位的余势参数。
    [Serializable] internal sealed class CharacterTransitionProfile
    {
        public string Name, ActionKey;
        public TransitionMethod Method = TransitionMethod.Combined;
        public TransitionEdge Edge;
        public TransitionLayer Layer;
        public TransitionPurpose Purpose;
        [Tooltip("负数沿用原时长，非负秒数覆盖。动作交接时钟仍由动作拥有。")] public float FadeSeconds = -1f;
        [Min(0.001f)] public float InertiaFadeWindow = 0.14f;
        [Range(0f, 1f)] public float InertiaStrength = 1f;
        public TransitionRegions Regions = TransitionRegions.All;
        [Tooltip("留空选中所有已绑定骨骼，可按人形骨逐项指定。")] public HumanBodyBones[] Bones;
        public TransitionGroupSettings Body = new TransitionGroupSettings { Seconds = 0.14f, Degrees = 16f, Position = 0.005f, Constraint = 0.65f };
        public TransitionGroupSettings Arms = new TransitionGroupSettings { Seconds = 0.16f, Degrees = 20f, Constraint = 1f };
        public TransitionGroupSettings Legs = new TransitionGroupSettings { Seconds = 0.09f, Degrees = 12f, Constraint = 0.85f };
        [Tooltip("允许局部位置余势的骨骼；留空仅允许脊柱。")] public HumanBodyBones[] PositionBones;
        private ulong boneMask, positionMask;

        // 在主线程把逐骨列表固定为可复制到动画作业的掩码。
        internal CharacterTransitionProfile Snapshot()
        {
            var copy = (CharacterTransitionProfile)MemberwiseClone();
            copy.Bones = (HumanBodyBones[])Bones?.Clone();
            copy.boneMask = Bones == null || Bones.Length == 0 ? ulong.MaxValue : 0;
            if (Bones != null) foreach (var b in Bones) if ((int)b >= 0 && b < HumanBodyBones.LastBone) copy.boneMask |= 1UL << (int)b;
            copy.positionMask = PositionBones == null || PositionBones.Length == 0 ? 1UL << (int)HumanBodyBones.Spine : 0;
            if (PositionBones != null) foreach (var b in PositionBones)
                if ((int)b >= 0 && b < HumanBodyBones.LastBone) copy.positionMask |= 1UL << (int)b;
            return copy;
        }

        // 在原动作允许修改的混合段上应用所选方法。
        internal float Duration(float original) => Method == TransitionMethod.Inertial ? 0f :
            FadeSeconds >= 0f ? FadeSeconds : original;

        // 根据实际在播时长控制淡入与惯性的叠加，总强度在每个部位单独消费。
        internal float Strength(float actualFade) => Method switch {
            TransitionMethod.Crossfade => 0f, TransitionMethod.Inertial => Mathf.Clamp01(InertiaStrength),
            _ => Mathf.Clamp01(InertiaStrength) * (1f - Mathf.Clamp01(actualFade / Mathf.Max(0.001f, InertiaFadeWindow))) };

        // 将特定身体遮罩部位编译成不含托管引用的作业系数。
        internal TransitionPart Part(AvatarMaskBodyPart part, float weight)
        {
            var region = part switch { AvatarMaskBodyPart.LeftArm => TransitionRegions.LeftArm, AvatarMaskBodyPart.RightArm => TransitionRegions.RightArm,
                AvatarMaskBodyPart.LeftLeg => TransitionRegions.LeftLeg, AvatarMaskBodyPart.RightLeg => TransitionRegions.RightLeg, _ => TransitionRegions.Body };
            if ((Regions & region) == 0) return default;
            bool legs = region == TransitionRegions.LeftLeg || region == TransitionRegions.RightLeg;
            return new TransitionPart { Weight = weight, Bones = boneMask, Positions = positionMask,
                Settings = legs ? Legs : region == TransitionRegions.Body ? Body : Arms };
        }
    }

    // 固定本角色全部策略并按配置顺序，以第一个匹配项决定当前进入或退出。
    internal sealed class CharacterTransitionProfiles
    {
        private readonly CharacterTransitionProfile[] profiles;
        private readonly CharacterTransitionProfile fallback;
        internal TransitionRegions Regions => fallback?.Regions ?? default;

        // 向骨骼绑定提供本次装配已冻结的附加骨列表。
        internal System.Collections.Generic.IEnumerable<HumanBodyBones> AdditionalBones()
        {
            foreach (var profile in profiles)
                if (profile?.Bones != null)
                    foreach (var bone in profile.Bones) yield return bone;
        }

        // 保留没有专用策略的旧模块参数并生成不可变的匹配列表。
        internal CharacterTransitionProfiles(TransitionInertiaModule module)
        {
            if (!module) { profiles = Array.Empty<CharacterTransitionProfile>(); return; }
            var input = module.Profiles ?? Array.Empty<CharacterTransitionProfile>();
            profiles = new CharacterTransitionProfile[input.Length];
            for (int i = 0; i < profiles.Length; i++) profiles[i] = input[i]?.Snapshot();
            fallback = new CharacterTransitionProfile { Regions = module.Regions, InertiaFadeWindow = module.DefaultFadeWindow,
                Body = new TransitionGroupSettings { Seconds = module.BodySeconds, Degrees = module.AngleLimit, Position = module.PositionLimit, Constraint = 0.65f },
                Arms = new TransitionGroupSettings { Seconds = module.ArmSeconds, Degrees = module.AngleLimit, Constraint = 1f },
                Legs = new TransitionGroupSettings { Seconds = module.LegSeconds, Degrees = module.AngleLimit, Constraint = 0.85f } }.Snapshot();
        }

        // 只读已经仲裁的动作键、用途和目标层，不修改动作归属。
        internal CharacterTransitionProfile Select(in CharacterAnimationLayerPlan current, bool exit)
        {
            if (!current.HasState) return null;
            var d = current.Demand;
            var purpose = current.Binding.IsStop ? TransitionPurpose.Stop : current.Binding.Variant switch {
                CharacterLocomotionVariant.Start => TransitionPurpose.Start,
                CharacterLocomotionVariant.TurnLeft or CharacterLocomotionVariant.TurnRight => TransitionPurpose.Turn,
                _ => d.IsLocomotion ? TransitionPurpose.Locomotion : d.Key == CharacterAnimationIntentConverter.IdleKey ? TransitionPurpose.Idle : TransitionPurpose.Action };
            foreach (var p in profiles)
                if (p != null && (p.Edge == TransitionEdge.Any || (p.Edge == TransitionEdge.Exit) == exit) &&
                    (p.Layer == TransitionLayer.Any || (int)p.Layer == (int)d.Source + 1) &&
                    (p.Purpose == TransitionPurpose.Any || p.Purpose == purpose) &&
                    (string.IsNullOrEmpty(p.ActionKey) || p.ActionKey == d.Key)) return p;
            return fallback;
        }
    }

    // 保存本次实际可见切换选中的部位调校和贡献。
    internal struct TransitionPart
    {
        internal float Weight;
        internal ulong Bones, Positions;
        internal TransitionGroupSettings Settings;
    }

    // 保留基础、下身、上身和全身四个实际声部的独立骨掩码及可见贡献。
    internal struct TransitionSources
    {
        internal TransitionPart Baseline, Lower, Upper, Overlay;
        internal bool Visible => Baseline.Weight > 0f || Lower.Weight > 0f || Upper.Weight > 0f || Overlay.Weight > 0f;

        // 当前层先遮蔽底层贡献，再登记自己的真实进入或退出量。
        internal void Cover(float coverage)
        {
            float remaining = 1f - coverage;
            Baseline.Weight *= remaining;
            Lower.Weight *= remaining;
            Upper.Weight *= remaining;
        }

        // 先按照单骨及平移/旋转掩码筛每个声部，只合成这一骨和这一自由度的贡献。
        internal TransitionPart ForBone(ulong bit, bool position)
        {
            TransitionPart primary = default;
            float total = 0f;
            Consider(Baseline, bit, position, ref primary, ref total);
            Consider(Lower, bit, position, ref primary, ref total);
            Consider(Upper, bit, position, ref primary, ref total);
            Consider(Overlay, bit, position, ref primary, ref total);
            primary.Weight = Mathf.Clamp01(total);
            return primary;
        }

        // 匹配骨骼的多个声部贡献相加，该自由度的限制与衰减采用主要可见贡献。
        private static void Consider(TransitionPart part, ulong bit, bool position, ref TransitionPart primary, ref float total)
        {
            if (part.Weight <= 0f || (part.Bones & bit) == 0 || (position ?
                (part.Positions & bit) == 0 || part.Settings.Position <= 0f : part.Settings.Degrees <= 0f)) return;
            total += part.Weight;
            if (part.Weight > primary.Weight) primary = part;
        }
    }
}
