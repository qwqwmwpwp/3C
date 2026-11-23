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
    public sealed class CharacterIkChannel
    {
        [InspectorName("部位")] public CharacterIkLimb Limb;
        [InspectorName("许可曲线"), Tooltip("素材附加曲线及控制器浮点参数的名称，默认参数值应为零。")]
        public string WeightCurve;
        [InspectorName("上段骨骼")] public Transform Root;
        [InspectorName("中段骨骼")] public Transform Mid;
        [InspectorName("末端骨骼")] public Transform Tip;
        [InspectorName("动画目标骨"), Tooltip("绑定在同一动画器下、独立于被求解骨链的目标骨。")]
        public Transform AnimationTarget;
        [InspectorName("默认使用动画目标")] public bool UseAnimationTarget;
        [InspectorName("握持动作键"), Tooltip("全身或上身实际选中此动作时，将动画手目标作为握持约束。")]
        public string GripActionKey;
        [InspectorName("默认场景目标")] public Transform SceneTarget;
        [InspectorName("默认探测地面")] public bool ProbeGround;
        [InspectorName("探测起点"), Tooltip("角色局部空间，单位米。")] public Vector3 ProbeOrigin;
        [InspectorName("探测长度"), Min(0f)] public float ProbeLength = 1f;
        [InspectorName("地面层")] public LayerMask GroundLayers = Physics.DefaultRaycastLayers;
        [InspectorName("脚踝离地高度"), Min(0f)] public float SoleHeight = 0.08f;
        [InspectorName("位置权重"), Range(0f, 1f)] public float PositionWeight = 1f;
        [InspectorName("旋转权重"), Range(0f, 1f)] public float RotationWeight = 1f;
        [InspectorName("目标切换秒数"), Min(0f)] public float BlendSeconds = 0.12f;
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
                Owns(actions.UpperBody) || Owns(actions.LowerBody));
        }

        private bool Owns(in CharacterAnimationDemand action) => action.Key == ActionKey && action.InstanceId == Instance;
    }

    // 场景输入只传值；动画目标骨留到动画作业取得本次基础姿态后采样。
    internal struct CharacterIkCandidate
    {
        public CharacterIkSource Source;
        public byte ActionBound;
        public float GripWeight;
        public long Instance;
        public int ObjectId, Priority, Slot;
        public Pose Target;
    }

    internal struct CharacterIkInput
    {
        public CharacterIkCandidate Animation, Scene, Ground;
        public Matrix4x4 GroundToWorld, WorldToGround;
        public Quaternion GroundRotation;
        public Vector3 GroundNormal, ProbeUp;
        public int SceneStart, SceneCount;
    }

    internal sealed class CharacterIkInputSource
    {
        private readonly CharacterIkChannel channel;
        private readonly Transform owner;
        private readonly Transform skeleton;
        private readonly CharacterIkRequest[] requests = new CharacterIkRequest[4];
        private readonly RaycastHit[] hits = new RaycastHit[24];
        private readonly List<string> sceneKeys = new List<string>(); // 按提供者隔离实例编号，同级保留注册顺序。
        private readonly List<CharacterIkRequest> sceneRequests = new List<CharacterIkRequest>();
        public int SceneCapacity => sceneRequests.Count;

        public CharacterIkInputSource(CharacterIkChannel channel, Transform owner, Transform skeleton)
        {
            this.channel = channel;
            this.owner = owner;
            this.skeleton = skeleton;
        }

        public bool Submit(CharacterIkSource source, long instance, Transform target, int priority, string key, string provider)
        {
            if (!IsSource(source) || instance <= 0 || (source == CharacterIkSource.Scene && !IsExternal(target)) ||
                (source == CharacterIkSource.Animation && channel.AnimationTarget == null))
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
            Array.Clear(requests, 0, requests.Length);
            sceneKeys.Clear();
            sceneRequests.Clear();
        }

        public CharacterIkInput Sample(in CharacterAnimationComposition actions, NativeArray<CharacterIkCandidate> candidates,
            ref int cursor)
        {
            var input = new CharacterIkInput();
            if (channel.AnimationTarget)
            {
                var grip = SelectedGrip(actions);
                bool selected = Select(CharacterIkSource.Animation, channel.UseAnimationTarget, 0, actions, out var animation);
                if (grip.IsValid && (!selected || !requests[(int)CharacterIkSource.Animation].Active))
                {
                    // 有效外部请求优先。若它所属的动作失选，当前真实握持仍可用自己的实例提供目标。
                    animation = new CharacterIkCandidate { Source = CharacterIkSource.Animation, ActionBound = 1,
                        GripWeight = grip.Weight, Instance = grip.InstanceId };
                    selected = true;
                }
                if (selected) input.Animation = animation;
            }

            ref CharacterIkRequest sceneRequest = ref requests[(int)CharacterIkSource.Scene];
            Transform target = sceneRequest.Active ? sceneRequest.Target : channel.SceneTarget;
            if (IsExternal(target) && target.gameObject.activeInHierarchy &&
                Select(CharacterIkSource.Scene, channel.SceneTarget != null, 10, actions, out var scene))
            {
                scene.ObjectId = target.GetInstanceID();
                scene.Target = new Pose(target.position, target.rotation);
                input.Scene = scene;
            }
            if (Select(CharacterIkSource.Ground, channel.ProbeGround, 0, actions, out var ground))
                SampleGround(ref input, ground);
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

        private CharacterAnimationDemand SelectedGrip(in CharacterAnimationComposition actions)
        {
            if (string.IsNullOrEmpty(channel.GripActionKey)) return default;
            if (actions.UpperBody.Key == channel.GripActionKey && actions.UpperBody.Weight > 0f)
                return actions.UpperBody;
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

        private void SampleGround(ref CharacterIkInput input, CharacterIkCandidate ground)
        {
            Vector3 up = owner.up;
            int count = owner.gameObject.scene.GetPhysicsScene().Raycast(owner.TransformPoint(channel.ProbeOrigin),
                -up, hits, channel.ProbeLength, channel.GroundLayers, QueryTriggerInteraction.Ignore);
            if (count == hits.Length) return; // 缓冲区满时可能漏掉最近遮挡，放弃本次锁点。
            int best = -1;
            for (int i = 0; i < count; i++)
                if (IsExternal(hits[i].collider.transform) && Vector3.Dot(hits[i].normal, up) > 0.1f &&
                    (best < 0 || hits[i].distance < hits[best].distance))
                    best = i;
            if (best < 0) return;
            RaycastHit hit = hits[best];
            Transform platform = hit.rigidbody ? hit.rigidbody.transform : hit.collider.transform;
            ground.ObjectId = platform.GetInstanceID();
            ground.Target = new Pose(hit.point + hit.normal * channel.SoleHeight, Quaternion.FromToRotation(up, hit.normal));
            input.Ground = ground;
            input.GroundToWorld = platform.localToWorldMatrix;
            input.WorldToGround = platform.worldToLocalMatrix;
            input.GroundRotation = platform.rotation;
            input.GroundNormal = hit.normal;
            input.ProbeUp = up;
        }

        private bool IsExternal(Transform value) => value != null && !value.IsChildOf(owner) &&
            (skeleton == owner || !skeleton || !value.IsChildOf(skeleton));
        private static bool IsSource(CharacterIkSource source) => (uint)((int)source - 1) < 3u;
    }
}
