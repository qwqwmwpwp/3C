using System;
using System.Threading.Tasks;
using Unity.Profiling;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 持有单个角色的规划任务，仅在主线程交接成功、异常或撤批后的模块状态。
    internal sealed class CharacterPresentationJob
    {
        private readonly Action plan, discard, finish;
        private Task running;

        // 缓存本角色的规划、异常撤批和交接入口。
        internal CharacterPresentationJob(Action plan, Action discard, Action finish)
        {
            this.plan = plan;
            this.discard = discard;
            this.finish = finish;
        }

        // 后台只执行纯规划，当前线程模式在本次调用中完成交接。
        internal void Schedule(bool threaded)
        {
            if (threaded) running = Task.Run(plan);
            else
            {
                try { plan(); }
                catch { discard(); throw; }
                finally { finish(); }
            }
        }

        // 主线程等待已发布任务，异常和完成回调与同步路径保持相同顺序。
        internal void Join()
        {
            if (running == null) return;
            using var wait = CharacterPresentationBatch.Wait.Auto();
            var job = running;
            running = null;
            try { job.GetAwaiter().GetResult(); }
            catch { discard(); throw; }
            finally { finish(); }
        }
    }

    /// <summary>角色 Update 发布私有快照，所有 Update 结束后、引擎动画求值前统一提交。</summary>
    internal static class CharacterPresentationBatch
    {
        internal static readonly ProfilerMarker Planning = new ProfilerMarker("3C.Animation.Plan");
        internal static readonly ProfilerMarker Capture = new ProfilerMarker("3C.Animation.Capture");
        internal static readonly ProfilerMarker Wait = new ProfilerMarker("3C.Animation.Wait");
        private static readonly CharacterFrameBatch<CharacterBehaviorAnimator> batch =
            new CharacterFrameBatch<CharacterBehaviorAnimator>("3C.Animation.Submit", actor => actor.FinishPresentation());

        // 新会话清空上一轮未提交的动画计划。
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset() => batch.Reset();

        // 所有 Update 完成后、动画求值前提交本帧计划。
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install() => batch.Install(typeof(UnityEngine.PlayerLoop.Update.ScriptRunBehaviourUpdate),
            typeof(CharacterPresentationBatch));

        // 将已完成动画采集的角色加入本帧交付。
        internal static void Queue(CharacterBehaviorAnimator actor) => batch.Queue(actor);
    }
}
