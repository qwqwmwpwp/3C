using System.Diagnostics;
using System;
using Unity.Profiling;

namespace ThreeC.CharacterBehavior
{
    // 只计本角色的片段节点和片段混合器；整体销毁图时结清仍连接或暂存的节点。
    internal sealed class CharacterPlaybackCounters
    {
        public int MixersCreated, MixersDestroyed, ClipsCreated, ClipsDestroyed;
        public bool Measuring;
        public long NodeTicks;
        private static readonly ProfilerMarker CreateNode = new ProfilerMarker("3C.Animation.CreateNode");
        private static readonly ProfilerMarker DestroyNode = new ProfilerMarker("3C.Animation.DestroyNode");

        internal readonly struct NodeScope : IDisposable
        {
            private readonly CharacterPlaybackCounters counters;
            private readonly long start;
            private readonly ProfilerMarker.AutoScope marker;

            internal NodeScope(CharacterPlaybackCounters counters, bool create)
            {
                marker = (create ? CreateNode : DestroyNode).Auto();
                this.counters = counters.Measuring ? counters : null;
                start = this.counters == null ? 0 : Stopwatch.GetTimestamp();
            }

            public void Dispose()
            {
                if (counters != null) counters.NodeTicks += Stopwatch.GetTimestamp() - start;
                marker.Dispose();
            }
        }

        internal NodeScope Measure(bool create) => new NodeScope(this, create);

        [Conditional("UNITY_EDITOR")]
        public void Mixer(bool created)
        {
            if (created) MixersCreated++;
            else MixersDestroyed++;
        }

        [Conditional("UNITY_EDITOR")]
        public void Clip(bool created)
        {
            if (created) ClipsCreated++;
            else ClipsDestroyed++;
        }

        [Conditional("UNITY_EDITOR")]
        public void Closed()
        {
            MixersDestroyed = MixersCreated;
            ClipsDestroyed = ClipsCreated;
        }
    }
}
