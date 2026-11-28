using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 策略在装配时取得的骨骼与历史时间索引，默认值没有对应观测。
    public readonly struct CharacterBoneQuery
    {
        private readonly int slot;
        internal readonly long CatalogId;
        internal int Index => slot - 1;

        // 保留角色专属采样布局中的索引。
        internal CharacterBoneQuery(int index, long catalogId)
        {
            slot = index + 1;
            CatalogId = catalogId;
        }
    }

    // 保存相对模型根朝向及位置的三维骨骼姿态。
    public readonly struct CharacterBonePose
    {
        public readonly Vector3 Position;
        public readonly Quaternion Rotation;
        internal readonly byte Valid;

        // 只将有限的位置与有效的旋转标记为可用姿态。
        internal CharacterBonePose(Vector3 position, Quaternion rotation)
        {
            Position = position;
            Rotation = rotation;
            float length = Quaternion.Dot(rotation, rotation);
            Valid = (byte)(position.sqrMagnitude < float.PositiveInfinity && length > 0.000001f &&
                length < float.PositiveInfinity ? 1 : 0);
        }
    }

    // 冻结一个时刻所有已声明的骨骼样本；没有绑定或历史不足时读取失败。
    public readonly struct CharacterBoneObservations
    {
        private readonly CharacterBonePose[] poses;
        private readonly long catalogId;
        public readonly double Time; // 最近一次有效采样时钟，单位秒。

        // 接管采样器刚写完的独立数组，后续帧不会修改此快照。
        internal CharacterBoneObservations(CharacterBonePose[] poses, long catalogId, double time)
        {
            this.poses = poses;
            this.catalogId = catalogId;
            Time = time;
        }

        // 按策略装配时登记的索引读取当前或插值得到的历史姿态。
        public bool TryGet(CharacterBoneQuery query, out CharacterBonePose pose)
        {
            int index = query.Index;
            pose = poses != null && query.CatalogId == catalogId && index >= 0 && index < poses.Length ? poses[index] : default;
            return pose.Valid != 0;
        }
    }

    // 描述一个人形骨或相对路径及所需回溯秒数。
    internal readonly struct CharacterBoneRequest
    {
        internal readonly HumanBodyBones Bone;
        internal readonly string Path;
        internal readonly float SecondsAgo;

        // 将骨骼标识及历史时刻保留到主线程绑定阶段。
        internal CharacterBoneRequest(HumanBodyBones bone, string path, float secondsAgo)
        {
            Bone = bone;
            Path = path;
            SecondsAgo = secondsAgo;
        }
    }
}
