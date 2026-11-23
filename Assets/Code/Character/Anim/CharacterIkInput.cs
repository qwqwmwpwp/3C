using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

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

    /// <summary>一条两骨链的输入绑定。动画许可由实际混合的同名素材曲线提供，默认值为零。</summary>
    [Serializable]
    public sealed class CharacterIkChannel : CharacterIkTuning
    {
        [InspectorName("上段骨骼")] public Transform Root;
        [InspectorName("中段骨骼")] public Transform Mid;
        [InspectorName("末端骨骼")] public Transform Tip;
        [InspectorName("动画目标骨"), Tooltip("绑定在同一动画器下、独立于被求解骨链的目标骨。")]
        public Transform AnimationTarget;
        [InspectorName("默认场景目标")] public Transform SceneTarget;

        internal CharacterBoundIkChannel BindModel()
        {
            return new CharacterBoundIkChannel(this, new CharacterIkBones
            {
                Limb = Limb, Root = Root, Mid = Mid, Tip = Tip,
                AnimationTarget = AnimationTarget, SceneTarget = SceneTarget
            });
        }
    }

    internal struct CharacterIkRequest
    {
        public long Instance;
        public bool Active;
        public string ActionKey;
        public Transform Target;
        public int Priority;

        public bool Matches(in CharacterAnimationComposition actions)
        {
            return Active && (string.IsNullOrEmpty(ActionKey) || Owns(actions.FullBody) ||
                Owns(actions.UpperBody) || Owns(actions.LowerBody) || Owns(actions.Overlay));
        }

        private bool Owns(in CharacterAnimationDemand action) => action.Key == ActionKey && action.InstanceId == Instance;
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

    internal struct CharacterIkInput
    {
        public CharacterIkCandidate Animation, Scene, Ground;
        public CharacterGroundFootProbes Probes;
        public int SceneStart, SceneCount;
#if UNITY_EDITOR
        public long DebugGripInstance;
        public float DebugGripCoverage;
        public byte DebugGripCoverageKnown;
#endif
    }

    internal sealed class CharacterIkInputSource
    {
        private long lastGrip;
        private float lastVisibleWeight;
        public void ForgetGrip() { lastGrip = 0; lastVisibleWeight = 0f; }
        private readonly CharacterIkTuning channel;
        private readonly CharacterIkBones bones;
        private readonly Transform owner;
        private readonly Transform skeleton;
        private readonly float walkableSlope;
        private readonly CharacterIkRequest[] requests = new CharacterIkRequest[4];
        private RaycastHit[] hits = new RaycastHit[24];
        private readonly List<string> sceneKeys = new List<string>(); // 按提供者隔离实例编号，同级保留注册顺序。
        private readonly List<CharacterIkRequest> sceneRequests = new List<CharacterIkRequest>();
        public int SceneCapacity => sceneRequests.Count;

        public CharacterIkInputSource(CharacterBoundIkChannel binding, Transform owner, Transform skeleton, float walkableSlope)
        {
            channel = binding.Tuning;
            bones = binding.Bones;
            this.owner = owner;
            this.skeleton = skeleton;
            this.walkableSlope = walkableSlope;
        }

        public bool Submit(CharacterIkSource source, long instance, Transform target, int priority, string key, string provider)
        {
            if (!IsSource(source) || instance <= 0 || (source == CharacterIkSource.Scene && !IsExternal(target)) ||
                (source == CharacterIkSource.Animation && bones.AnimationTarget == null))
                return false;
            bool named = !string.IsNullOrEmpty(provider);
            if (named && source != CharacterIkSource.Scene) return false;
            int slot = named ? sceneKeys.IndexOf(provider) : -1;
            var old = named ? (slot < 0 ? default : sceneRequests[slot]) : requests[(int)source];
            if (instance < old.Instance || (instance == old.Instance && (!old.Active || old.ActionKey != key)))
                return false;
            var request = new CharacterIkRequest { Active = true, Instance = instance, ActionKey = key, Target = target, Priority = priority };
            if (!named) requests[(int)source] = request;
            else if (slot >= 0) sceneRequests[slot] = request;
            else
            {
                sceneKeys.Add(provider);
                sceneRequests.Add(request);
            }
            return true;
        }

        public bool Withdraw(CharacterIkSource source, long instance, string provider)
        {
            if (!IsSource(source)) return false;
            bool named = !string.IsNullOrEmpty(provider);
            int slot = named ? sceneKeys.IndexOf(provider) : -1;
            if (named && (source != CharacterIkSource.Scene || slot < 0)) return false;
            var old = named ? sceneRequests[slot] : requests[(int)source];
            if (!old.Active || old.Instance != instance) return false;
            old.Active = false;
            old.Target = null;
            if (named) sceneRequests[slot] = old;
            else requests[(int)source] = old;
            return true;
        }

        public void Clear()
        {
            ForgetGrip();
            Array.Clear(requests, 0, requests.Length);
            sceneKeys.Clear();
            sceneRequests.Clear();
        }

        public CharacterIkInput Sample(in CharacterAnimationComposition actions, NativeArray<CharacterIkCandidate> candidates,
            ref int cursor, in CharacterFootProbeMotion probeMotion, float probeDelta, CharacterClipPlayback clips)
        {
            var input = new CharacterIkInput();
            if (bones.AnimationTarget)
            {
                var grip = GripCandidate(actions, clips);
#if UNITY_EDITOR
                input.DebugGripInstance = grip.Instance;
#endif
                bool selected = Select(CharacterIkSource.Animation, channel.UseAnimationTarget, 0, actions, out var animation);
                if (grip.Source != CharacterIkSource.None && (!selected || !requests[(int)CharacterIkSource.Animation].Active))
                {
                    // 有效外部请求优先。若它所属的动作失选，当前真实握持仍可用自己的实例提供目标。
                    animation = grip;
                    selected = true;
                }
                if (selected) input.Animation = animation;
            }
#if UNITY_EDITOR
            if (clips != null && !string.IsNullOrEmpty(channel.GripActionKey))
            {
                input.DebugGripCoverageKnown = 1;
                input.DebugGripCoverage = clips.DebugActionCoverage(channel.Limb, channel.GripActionKey);
            }
#endif

            ref CharacterIkRequest sceneRequest = ref requests[(int)CharacterIkSource.Scene];
            Transform target = sceneRequest.Active ? sceneRequest.Target : bones.SceneTarget;
            if (IsExternal(target) && target.gameObject.activeInHierarchy &&
                Select(CharacterIkSource.Scene, bones.SceneTarget != null, 10, actions, out var scene))
            {
                scene.ObjectId = target.GetInstanceID();
                scene.Target = new Pose(target.position, target.rotation);
                input.Scene = scene;
            }
            if (Select(CharacterIkSource.Ground, channel.ProbeGround, 0, actions, out var ground))
            {
                input.Ground = ground;
                input.Probes.Cast(owner.gameObject.scene.GetPhysicsScene(), channel, owner, skeleton, walkableSlope, probeMotion, probeDelta, ref hits);
                if (channel.Limb != CharacterIkLimb.LeftFoot && channel.Limb != CharacterIkLimb.RightFoot)
                    input.Probes.AcceptCenter(ref input.Ground);
            }
            input.SceneStart = cursor;
            for (int i = 0; i < sceneRequests.Count; i++)
            {
                var request = sceneRequests[i];
                if (!request.Matches(actions) || !IsExternal(request.Target) || !request.Target.gameObject.activeInHierarchy)
                    continue;
                candidates[cursor++] = new CharacterIkCandidate { Source = CharacterIkSource.Scene, Slot = i + 1,
                    Priority = request.Priority, Instance = request.Instance, ObjectId = request.Target.GetInstanceID(),
                    Target = new Pose(request.Target.position, request.Target.rotation) };
            }
            input.SceneCount = cursor - input.SceneStart;
            return input;
        }

        private CharacterIkCandidate GripCandidate(in CharacterAnimationComposition actions, CharacterClipPlayback clips)
        {
            if (string.IsNullOrEmpty(channel.GripActionKey)) return default;
            var selected = SelectedGrip(actions);
            long instance = selected.IsValid ? selected.InstanceId : lastGrip;
            if (instance == 0) return default;
            var candidate = new CharacterIkCandidate { Source = CharacterIkSource.Animation, ActionBound = 1,
                Instance = instance, GripWeight = selected.Weight };
            if (clips == null) return selected.IsValid ? candidate : default;
            var visible = clips.Permission(channel.Limb, channel.WeightCurve, channel.GripActionKey, instance);
            float weight = selected.IsValid ? visible.Coverage : Mathf.Min(visible.Coverage, lastVisibleWeight);
            lastGrip = weight > 0f ? instance : 0;
            lastVisibleWeight = weight;
            if (weight <= 0f) return default;
            candidate.UsesVisibleGrip = 1;
            candidate.GripWeight = weight;
            candidate.VisiblePermission = visible.Value * (weight / visible.Coverage);
            return candidate;
        }

        private CharacterAnimationDemand SelectedGrip(in CharacterAnimationComposition actions)
        {
            if (string.IsNullOrEmpty(channel.GripActionKey)) return default;
            if (actions.UpperBody.Key == channel.GripActionKey && actions.UpperBody.Weight > 0f)
                return actions.UpperBody;
            if (actions.Overlay.Key == channel.GripActionKey && actions.Overlay.Weight > 0f)
                return actions.Overlay;
            return actions.FullBody.Key == channel.GripActionKey ? actions.FullBody : default;
        }

        private bool Select(CharacterIkSource source, bool enabled, int priority, in CharacterAnimationComposition actions,
            out CharacterIkCandidate candidate)
        {
            CharacterIkRequest request = requests[(int)source];
            candidate = new CharacterIkCandidate { Source = source, Instance = request.Active ? request.Instance : 0,
                ActionBound = (byte)(request.Active && !string.IsNullOrEmpty(request.ActionKey) ? 1 : 0),
                GripWeight = 1f,
                Priority = request.Active ? request.Priority : priority };
            return request.Active ? request.Matches(actions) : enabled;
        }

        private bool IsExternal(Transform value) => value != null && !value.IsChildOf(owner) &&
            (skeleton == owner || !skeleton || !value.IsChildOf(skeleton));
        private static bool IsSource(CharacterIkSource source) => (uint)((int)source - 1) < 3u;
    }
}
