using Unity.Collections;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    public enum CharacterIkLimb
    {
        [InspectorName("左手")] LeftHand, [InspectorName("右手")] RightHand,
        [InspectorName("左脚")] LeftFoot, [InspectorName("右脚")] RightFoot
    }

    public enum CharacterIkSource
    {
        [InspectorName("无")] None, [InspectorName("动画目标骨")] Animation,
        [InspectorName("场景目标")] Scene, [InspectorName("地面接触点")] Ground
    }

    // 场景输入只传值；动画目标骨留到动画作业取得本次基础姿态后采样。
    internal struct CharacterIkCandidate
    {
        public CharacterIkSource Source;
        public byte ActionBound;
        public float GripWeight;
        public float VisiblePermission;
        public byte UsesVisibleGrip;
        public long Instance;
        public int ObjectId, Priority, Slot;
        public Pose Target;
    }

    // 保存本帧各目标来源的候选与实地探测结果。
    internal struct CharacterIkInput
    {
        public CharacterIkCandidate Animation, Scene, Ground;
        public CharacterGroundFootProbes Probes;
        public int SceneStart, SceneCount;
    }

    // 汇集动画握持、外部场景目标和地面探测的主线程输入，同段骨姿由作业补充。
    internal sealed class CharacterIkInputSource
    {
        private CharacterGripIk grip;
        private CharacterIkTuning channel;
        private CharacterIkBones bones;
        private readonly Transform owner, skeleton;
        private float walkableSlope;
        private RaycastHit[] hits = new RaycastHit[24];
        internal CharacterIkRequests Requests { get; }
        internal bool HasMarker => bones.AnimationTarget;

        // 安装该模型的来源和独立的请求登记表。
        public CharacterIkInputSource(CharacterBoundIkChannel binding, Transform owner, Transform skeleton, float walkableSlope)
        {
            this.owner = owner;
            this.skeleton = skeleton;
            Requests = new CharacterIkRequests(owner, skeleton);
            Rebind(binding, walkableSlope);
        }

        // 重装同一通道时更新骨链和参数，保留已登记目标及其去重编号。
        internal void Rebind(CharacterBoundIkChannel binding, float slope)
        {
            channel = binding.Tuning;
            bones = binding.Bones;
            walkableSlope = slope;
        }

        // 清除旧动作的可见握持历史，已登记请求保持有效。
        public void ForgetGrip() => grip = default;

        // 采集各独立来源本段的候选，保留显式动画请求对默认握点的优先权。
        public CharacterIkInput Sample(in CharacterAnimationComposition actions, NativeArray<CharacterIkCandidate> candidates,
            ref int cursor, in CharacterFootProbeMotion probeMotion, float probeDelta, in CharacterFootWarp warp, CharacterClipPlayback clips)
        {
            var input = new CharacterIkInput();
            if (bones.AnimationTarget)
            {
                var visible = grip.Sample(channel, actions, clips);
                bool selected = Requests.Select(CharacterIkSource.Animation, channel.UseAnimationTarget, 0, actions, out var animation);
                if (visible.Source != CharacterIkSource.None && (!selected || !Requests.Active(CharacterIkSource.Animation)))
                    input.Animation = visible;
                else if (selected) input.Animation = animation;
            }
            Requests.SampleScene(actions, bones.SceneTarget, candidates, ref cursor, ref input);
            if (Requests.Select(CharacterIkSource.Ground, channel.ProbeGround, 0, actions, out var ground))
            {
                input.Ground = ground;
                CharacterGroundIk.Sample(channel, owner, skeleton, walkableSlope, probeMotion, probeDelta, warp, ref hits, ref input);
            }
            return input;
        }
    }
}
