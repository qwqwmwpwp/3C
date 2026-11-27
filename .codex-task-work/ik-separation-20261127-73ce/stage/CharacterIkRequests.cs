using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 保存一个来源最近一次提交的目标和所属动作。
    internal struct CharacterIkRequest
    {
        public long Instance;
        public bool Active;
        public string ActionKey;
        public Transform Target;
        public int Priority;

        // 请求有效且绑定的动作实例仍拥有某个部位时，允许该来源参与。
        public bool Matches(in CharacterAnimationComposition actions)
        {
            return Active && (string.IsNullOrEmpty(ActionKey) || Owns(actions.FullBody) ||
                Owns(actions.UpperBody) || Owns(actions.LowerBody) || Owns(actions.Overlay));
        }

        // 同一动作键的不同实例分别持有各自的请求。
        private bool Owns(in CharacterAnimationDemand action) => action.Key == ActionKey && action.InstanceId == Instance;
    }

    // 按来源和提供者登记目标请求，保留实例编号以拒绝过期的提交或撤销。
    internal sealed class CharacterIkRequests
    {
        private readonly Transform owner, skeleton;
        private readonly CharacterIkRequest[] requests = new CharacterIkRequest[4];
        private readonly List<string> sceneKeys = new List<string>(); // 按提供者隔离实例编号，同级保留注册顺序。
        private readonly List<CharacterIkRequest> sceneRequests = new List<CharacterIkRequest>();
        public int SceneCapacity => sceneRequests.Count;

        // 记录角色与模型边界，用于过滤自身层级中的场景目标。
        internal CharacterIkRequests(Transform owner, Transform skeleton)
        {
            this.owner = owner;
            this.skeleton = skeleton;
        }

        // 验证目标与单调实例号，再登记默认来源或具名场景提供者。
        public bool Submit(CharacterIkSource source, long instance, Transform target, int priority, string key, string provider, bool hasMarker)
        {
            if (!IsSource(source) || instance <= 0 || (source == CharacterIkSource.Scene && !IsExternal(target)) ||
                (source == CharacterIkSource.Animation && !hasMarker))
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

        // 只撤销相同提供者下仍然活跃的指定实例。
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

        // 重置后允许每个提供者重新从初始实例号提交。
        public void Clear()
        {
            Array.Clear(requests, 0, requests.Length);
            sceneKeys.Clear();
            sceneRequests.Clear();
        }

        // 判定指定来源是否由显式请求接管。
        internal bool Active(CharacterIkSource source) => requests[(int)source].Active;

        // 将场景默认目标和具名提供者的世界位姿写入本段候选缓冲。
        internal void SampleScene(in CharacterAnimationComposition actions, Transform defaultTarget,
            NativeArray<CharacterIkCandidate> candidates, ref int cursor, ref CharacterIkInput input)
        {
            ref CharacterIkRequest sceneRequest = ref requests[(int)CharacterIkSource.Scene];
            Transform target = sceneRequest.Active ? sceneRequest.Target : defaultTarget;
            if (IsExternal(target) && target.gameObject.activeInHierarchy &&
                Select(CharacterIkSource.Scene, defaultTarget != null, 10, actions, out var scene))
            {
                scene.ObjectId = target.GetInstanceID();
                scene.Target = new Pose(target.position, target.rotation);
                input.Scene = scene;
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
        }

        // 在选中动作归属下选择显式请求，缺省时使用通道配置。
        internal bool Select(CharacterIkSource source, bool enabled, int priority, in CharacterAnimationComposition actions,
            out CharacterIkCandidate candidate)
        {
            CharacterIkRequest request = requests[(int)source];
            candidate = new CharacterIkCandidate { Source = source, Instance = request.Active ? request.Instance : 0,
                ActionBound = (byte)(request.Active && !string.IsNullOrEmpty(request.ActionKey) ? 1 : 0),
                GripWeight = 1f,
                Priority = request.Active ? request.Priority : priority };
            return request.Active ? request.Matches(actions) : enabled;
        }

        // 场景目标应来自角色与模型层级之外。
        private bool IsExternal(Transform value) => value != null && !value.IsChildOf(owner) &&
            (skeleton == owner || !skeleton || !value.IsChildOf(skeleton));

        // 核对来源是否有对应的请求槽。
        private static bool IsSource(CharacterIkSource source) => (uint)((int)source - 1) < 3u;
    }
}
