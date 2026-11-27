using System;
using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.LowLevel;

namespace ThreeC.CharacterBehavior
{
    /// <summary>在指定的 Unity 阶段边界交付排队的角色。</summary>
    internal sealed class CharacterFrameBatch<T> where T : Behaviour
    {
        private readonly List<T> pending = new List<T>();
        private readonly Action<T> submit;
        private readonly ProfilerMarker marker;
        private bool installed;

        // 固定批次的采样名和单个角色的提交入口。
        internal CharacterFrameBatch(string sample, Action<T> submit)
        {
            marker = new ProfilerMarker(sample);
            this.submit = submit;
        }

        // 进入新的运行会话时放弃上一次排队的对象。
        internal void Reset()
        {
            pending.Clear();
            installed = false;
        }

        // 将本批次安装到目标阶段前或后，保留原有 PlayerLoop。
        internal void Install(Type stage, Type batch, bool before = false)
        {
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            installed = Insert(ref loop, stage, new PlayerLoopSystem { type = batch, updateDelegate = Finish }, before);
            if (installed) PlayerLoop.SetPlayerLoop(loop);
        }

        // 缺少阶段时可就地提交；依赖物理求解时刻的调用方必须等待真实阶段。
        internal void Queue(T actor, bool fallback = true)
        {
            if (installed) pending.Add(actor);
            else if (fallback) submit(actor);
        }

        // 保留各系统的相对次序；关闭域重载时刷新已有的回调。
        private static bool Insert(ref PlayerLoopSystem parent, Type stage, PlayerLoopSystem batch, bool before)
        {
            var children = parent.subSystemList;
            if (children == null) return false;
            for (int i = 0; i < children.Length; i++)
            {
                if (children[i].type != stage)
                {
                    if (Insert(ref children[i], stage, batch, before)) return true;
                    continue;
                }
                int existing = Array.FindIndex(children, child => child.type == batch.type);
                if (existing >= 0) children[existing] = batch;
                else
                {
                    int index = before ? i : i + 1;
                    var expanded = new PlayerLoopSystem[children.Length + 1];
                    Array.Copy(children, expanded, index);
                    expanded[index] = batch;
                    Array.Copy(children, index, expanded, index + 1, children.Length - index);
                    parent.subSystemList = expanded;
                }
                return true;
            }
            return false;
        }

        // 跳过本帧内已退出的角色，单个角色的异常不会中断其余交付。
        private void Finish()
        {
            using var sample = marker.Auto();
            try
            {
                foreach (var actor in pending)
                {
                    if (!actor || !actor.isActiveAndEnabled) continue;
                    try { submit(actor); }
                    catch (Exception error) { Debug.LogException(error, actor); }
                }
            }
            finally { pending.Clear(); }
        }
    }
}
