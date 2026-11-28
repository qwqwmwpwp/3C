#if UNITY_EDITOR
using System;

namespace ThreeC.CharacterBehavior
{
    // 将请求、真实起播和实际终态记录为不会随句柄继续变化的诊断值。
    public readonly struct CharacterPlaybackHistoryEntry
    {
        public readonly string Key;
        public readonly long PlaybackId;
        public readonly CharacterActionPlaybackStatus Status;
        public readonly CharacterPlaybackEndSource EndSource;
        public readonly bool HasStarted;

        // 在状态边沿复制当前回执。
        internal CharacterPlaybackHistoryEntry(CharacterActionPlayback receipt)
        {
            Key = receipt.Key;
            PlaybackId = receipt.PlaybackId;
            Status = receipt.Status;
            EndSource = receipt.EndSource;
            HasStarted = receipt.HasStarted;
        }
    }

    // 保存该动画宿主最近的播放边沿，调试器打开时才分配环形历史。
    public sealed class CharacterPlaybackHistory
    {
        public const int Capacity = 64;
        private readonly object gate = new object();
        private CharacterPlaybackHistoryEntry[] entries;
        private int next, count;

        // 开始或结束短历史采集，重复开启保留当前记录。
        public void Enable(bool enabled)
        {
            lock (gate)
            {
                if ((entries != null) == enabled) return;
                entries = enabled ? new CharacterPlaybackHistoryEntry[Capacity] : null;
                next = count = 0;
            }
        }

        // 只记录已经发布的状态。
        internal void Record(CharacterActionPlayback receipt)
        {
            lock (gate)
            {
                if (entries == null) return;
                entries[next] = new CharacterPlaybackHistoryEntry(receipt);
                next = (next + 1) % Capacity;
                if (count < Capacity) count++;
            }
        }

        // 按从新到旧的顺序复制可见行。
        public int CopyTo(CharacterPlaybackHistoryEntry[] destination)
        {
            lock (gate)
            {
                int length = Math.Min(count, destination.Length);
                for (int i = 0; i < length; i++) destination[i] = entries[(next + Capacity - i - 1) % Capacity];
                return length;
            }
        }
    }
}
#endif
