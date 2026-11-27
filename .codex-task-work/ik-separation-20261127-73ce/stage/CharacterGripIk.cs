using UnityEngine;
using UnityEngine.Animations;

namespace ThreeC.CharacterBehavior
{
    // 记录上一次可见的握持动作和权重，按真实片段贡献生成握持候选。
    internal struct CharacterGripIk
    {
        private long lastGrip;
        private float lastVisibleWeight;

        // 按动作实例当前可见的混合贡献计算握点许可，退出时沿用该实例直到贡献归零。
        internal CharacterIkCandidate Sample(CharacterIkTuning channel, in CharacterAnimationComposition actions, CharacterClipPlayback clips)
        {
            if (string.IsNullOrEmpty(channel.GripActionKey)) return default;
            var selected = SelectedGrip(actions, channel.GripActionKey);
            long instance = selected.IsValid ? selected.InstanceId : lastGrip;
            if (instance == 0) return default;
            var visible = clips.Permission(channel.Limb, channel.WeightCurve, channel.GripActionKey, instance);
            float weight = selected.IsValid ? visible.Coverage : Mathf.Min(visible.Coverage, lastVisibleWeight);
            lastGrip = weight > 0f ? instance : 0;
            lastVisibleWeight = weight;
            if (weight <= 0f) return default;
            return new CharacterIkCandidate
            {
                Source = CharacterIkSource.Animation,
                ActionBound = 1,
                Instance = instance,
                UsesVisibleGrip = 1,
                GripWeight = weight,
                VisiblePermission = visible.Value * (weight / visible.Coverage)
            };
        }

        // 依次读取可见上身、叠加层和全身的握持动作。
        private static CharacterAnimationDemand SelectedGrip(in CharacterAnimationComposition actions, string key)
        {
            if (actions.UpperBody.Key == key && actions.UpperBody.Weight > 0f)
                return actions.UpperBody;
            if (actions.Overlay.Key == key && actions.Overlay.Weight > 0f)
                return actions.Overlay;
            return actions.FullBody.Key == key ? actions.FullBody : default;
        }

        // 离开可见握持时重置旧目标淡出量，新的目标从自身位置进入。
        internal static float Permission(in CharacterIkCandidate best, float fallback, ref CharacterIkState state)
        {
            if (state.WasVisibleGrip != 0 && best.UsesVisibleGrip == 0)
            {
                state.Envelope = 0f;
                state.Source = CharacterIkSource.None;
            }
            state.WasVisibleGrip = best.UsesVisibleGrip;
            return best.UsesVisibleGrip != 0 ? best.VisiblePermission : fallback;
        }

        // 保存惯性前的手部目标及握持权重，用于惯性后的骨链求解。
        internal struct HandReference
        {
            public float Grip;
            public bool HasDefault, Selected;
            public CharacterIkCandidate Best;
            public Pose Tip, Marker;

            // 按胸臂惯性产生的手端变化搬运默认动画标记。
            public void TransportDefault(in Pose tip, ref Pose target)
            {
                if (!HasDefault) return;
                Vector3 position = tip.position;
                Quaternion rotation = tip.rotation;
                if (position.Equals(Tip.position) && rotation.Equals(Tip.rotation)) return;
                // 保留手端到根节点标记的同帧偏差，避免满权重 IK 抹去自由手的惯性。
                Quaternion change = rotation * Quaternion.Inverse(Tip.rotation);
                target = new Pose(position + change * (Marker.position - Tip.position), change * Marker.rotation);
            }
        }

        // 采集惯性前的标记和手端，或用可见候选权重限制当前胸臂惯性。
        internal static HandReference ReadHand(AnimationStream stream, in CharacterIkBinding binding, in CharacterIkWeights weights,
            in CharacterIkCandidate best, in CharacterIkState state)
        {
            var hand = new HandReference { Selected = true, Best = best };
            if (best.Source == CharacterIkSource.Animation && best.ActionBound == 0)
            {
                hand.HasDefault = true;
                hand.Tip = binding.ReadTip(stream);
                hand.Marker = binding.ReadMarker(stream);
            }
            else if (best.Source == CharacterIkSource.Animation || best.Source == CharacterIkSource.Scene)
            {
                float from = state.WasVisibleGrip != 0 && best.UsesVisibleGrip == 0 ? 0f : state.Envelope;
                float envelope = Mathf.MoveTowards(from, 1f, CharacterIkDecision.BlendStep(weights, stream.deltaTime));
                float permission = best.UsesVisibleGrip != 0 ? best.VisiblePermission : weights.Permission *
                    (best.Source == CharacterIkSource.Animation ? best.GripWeight : 1f);
                hand.Grip = permission * envelope * Mathf.Max(weights.PositionWeight, weights.RotationWeight);
            }
            return hand;
        }
    }
}
