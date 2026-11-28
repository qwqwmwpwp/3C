namespace ThreeC.CharacterBehavior
{
    // 标识装配时声明的相对轨迹时刻，索引只可用于同一角色的当前装配。
    public readonly struct CharacterTrajectoryQuery
    {
        private readonly int slot;
        internal int Index => slot - 1;
        internal readonly long CatalogId;

        // 保存轨迹样本在声明表中的位置。
        internal CharacterTrajectoryQuery(int index, long catalogId)
        {
            slot = index + 1;
            CatalogId = catalogId;
        }
    }

    // 保存某个模拟时刻的已声明轨迹样本及原始不可变轨迹快照。
    public readonly struct CharacterTrajectoryObservations
    {
        public readonly IAnimationTrajectorySnapshot Snapshot;
        public readonly double Time;
        private readonly long catalogId;
        private readonly AnimationTrajectorySample?[] samples;

        // 在采集阶段按声明时间向同一个冻结来源取值，缺失样本保持空值。
        internal CharacterTrajectoryObservations(IAnimationTrajectorySnapshot snapshot, double time, long catalogId, float[] offsets)
        {
            Snapshot = snapshot;
            Time = time;
            this.catalogId = catalogId;
            samples = null;
            if (snapshot == null || offsets.Length == 0) return;
            samples = new AnimationTrajectorySample?[offsets.Length];
            for (int i = 0; i < offsets.Length; i++)
                if (snapshot.TrySample(time, offsets[i], out var sample)) samples[i] = sample;
        }

        // 读取已声明时刻的完整三维样本；撤回、越界或其他角色的句柄返回失败。
        public bool TryGet(CharacterTrajectoryQuery query, out AnimationTrajectorySample sample)
        {
            int i = query.Index;
            if (query.CatalogId == catalogId && samples != null && i >= 0 && i < samples.Length && samples[i].HasValue)
            {
                sample = samples[i].Value;
                return true;
            }
            sample = default;
            return false;
        }
    }
}
