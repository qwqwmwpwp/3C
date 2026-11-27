exec((__import__('pathlib').Path(__file__).with_name('editing.py')).read_text('utf-8-sig'))
new('Anim/CharacterPlaybackFrameBudget.cs','''using System;

namespace ThreeC.CharacterBehavior
{
    // 同一个求值区间的角色截止、帧时间和 Animator 时间；各层、素材和 IK 使用同一份。
    internal readonly struct CharacterPlaybackFrameStep
    {
        internal readonly double End, Fraction;
        internal readonly float FrameDelta, GraphDelta;

        // 按本区间在剩余角色时间中的比例分配两种步长。
        internal CharacterPlaybackFrameStep(double end, double fraction, double frame, double graph)
        {
            End = end;
            Fraction = fraction;
            FrameDelta = (float)(frame * fraction);
            GraphDelta = (float)(graph * fraction);
        }
    }

    // 按渲染帧登记播放预算，只有实际图求值才扣账，重选、撤批及同素材重绑都沿用余量。
    internal sealed class CharacterPlaybackFrameBudget
    {
        private int? frameNumber;
        private double evaluatedAt, target, remainingFrame, remainingGraph;

        // 每个渲染帧只计入一次按 Animator 更新方式和速度换算的步长。
        internal void Capture(int frame, double now, float dt, float animationDt)
        {
            if (!frameNumber.HasValue) evaluatedAt = now - dt;
            target = now;
            if (frameNumber == frame) return;
            frameNumber = frame;
            remainingFrame += dt;
            remainingGraph += animationDt;
        }

        // 查询到指定角色时刻的预算；同一候选重复查询不推进实际时钟。
        internal CharacterPlaybackFrameStep Until(double at)
        {
            double end = Math.Max(evaluatedAt, Math.Min(at, target));
            double fraction = end >= target ? 1d : (end - evaluatedAt) / (target - evaluatedAt);
            return new CharacterPlaybackFrameStep(end, fraction, remainingFrame, remainingGraph);
        }

        internal CharacterPlaybackFrameStep Remaining => Until(target);

        // 原生图已经返回后确认这一段的截止与余时；延后重入从这一位置继续。
        internal void Commit(in CharacterPlaybackFrameStep step)
        {
            evaluatedAt = step.End;
            remainingFrame *= 1d - step.Fraction;
            remainingGraph *= 1d - step.Fraction;
        }

        // 新角色代次从下一次采集建立独立的渲染时间基准。
        internal void Reset()
        {
            frameNumber = null;
            evaluatedAt = target = remainingFrame = remainingGraph = 0d;
        }
    }
}
''','CharacterPlaybackClock')
use('CharacterAnimationPlayer')
sub('        private CharacterAnimationPlaybackPlan lastEvaluated;', '''        private CharacterAnimationPlaybackPlan lastEvaluated;
        private readonly CharacterPlaybackFrameBudget budget = new CharacterPlaybackFrameBudget();
        internal CharacterPlaybackFrameStep Remaining => budget.Remaining;

        // 在主线程把本帧普通及非缩放 Animator 时间各登记一次。
        internal CharacterPlaybackFrameStep CaptureFrame(double now, float dt)
        {
            budget.Capture(Time.frameCount, now, dt, clips.LayerBlendDelta(dt));
            return budget.Remaining;
        }
''')
sub('var plan = new CharacterAnimationPlaybackPlan(lastEvaluated, seen, clips, from);','''var step = budget.Until(to);
                var plan = new CharacterAnimationPlaybackPlan(lastEvaluated, seen, clips, composer, step);''')
sub('if (!clips.PrepareSegment(plan, to, (float)(to - from), visibleWeights))','''clips.Apply(plan, step, visibleWeights);
                if (!clips.Graph.IsValid())''')
sub('ik.Prepare(owners, motion, plan, 0f, to, keepClocks: true);','ik.Prepare(owners, motion, plan, step);')
sub('try { clips.Evaluate(() => ObserveSegment(plan, to)); }','try { EvaluatePose(plan, step); }')
cut('        // 图求值后保存完整分层快照','        // 读取四个原生混合源')
sub('private void ObserveSegment(in CharacterAnimationPlaybackPlan plan, double? at)','private void ObserveSegment(in CharacterAnimationPlaybackPlan plan, double at)')
sub('private void Record(in CharacterAnimationLayerPlan layer, double? at)','private void Record(in CharacterAnimationLayerPlan layer, double at)')
sub('double time = at.HasValue ? System.Math.Min(at.Value, playback.Clock.End) : layer.Demand.Timing.Sample.Time;\n            if (at.HasValue && (!playback.Clock.Valid || time <= playback.LastEvaluatedAt)) return;', 'double time = playback.Clock.Valid ? System.Math.Min(at, playback.Clock.End) : at;')
sub('internal CharacterAnimationPlaybackEmission ApplyPlan(in CharacterAnimationPlaybackPlan plan, float deltaTime) =>\n            clips.Apply(plan, deltaTime);','''internal CharacterAnimationPlaybackEmission ApplyPlan(in CharacterAnimationPlaybackPlan plan, in CharacterPlaybackFrameStep step) =>
            clips.Apply(plan, step);''')
sub('internal void EvaluatePose(CharacterAnimationPlaybackPlan plan) => clips.Evaluate(() => RecordEvaluated(plan));','''internal void EvaluatePose(CharacterAnimationPlaybackPlan plan, CharacterPlaybackFrameStep step) => clips.Evaluate(() =>
        {
            budget.Commit(step);
            lastEvaluated = plan;
            ObserveSegment(plan, step.End);
        });''')
sub('        // 释放图并恢复由暂停暂存的 Animator 速度。','''        // 角色硬重置同时清除旧角色的已消费时刻和分层快照。
        internal void ResetPlayback()
        {
            clips.Close();
            budget.Reset();
            lastEvaluated = default;
            System.Array.Clear(seen, 0, seen.Length);
            System.Array.Clear(visibleWeights, 0, visibleWeights.Length);
        }

        // 释放图并恢复由暂停暂存的 Animator 速度。''')
sub('using UnityEngine;','using UnityEngine;\nusing UnityEngine.Playables;')
write()
use('CharacterClipPlayback')
sub('public CharacterAnimationPlaybackEmission Apply(in CharacterAnimationPlaybackPlan plan, float dt)\n        {\n            Open();\n            delta = LayerBlendDelta(dt);\n            clockScale = dt > 0f ? delta / dt : 0f;', '''public CharacterAnimationPlaybackEmission Apply(in CharacterAnimationPlaybackPlan plan, in CharacterPlaybackFrameStep step,
            float[] restoreWeights = null)
        {
            bool rebuilding = body == null;
            Open();
            if (rebuilding && restoreWeights != null) body?.RestoreWeights(restoreWeights);
            delta = step.GraphDelta;
            clockScale = step.FrameDelta > 0f ? delta / step.FrameDelta : 0f;''')
cut('        // 先恢复正常分层图','        // 图求值发生重入')
write()
use('CharacterAnimationIk')
sub('in CharacterAnimationPlaybackPlan plan, float deltaTime, double sampleTime, bool keepClocks = false)', 'in CharacterAnimationPlaybackPlan plan, in CharacterPlaybackFrameStep step)')
sub('float animationDelta = keepClocks ? 0f : clips.LayerBlendDelta(deltaTime);','float deltaTime = step.FrameDelta, animationDelta = step.GraphDelta;\n            double sampleTime = step.End;')
write()
use('CharacterAnimationResourceCatalog')
sub('public void CaptureWeights(float dt)\n        {','''public void CaptureWeights(in CharacterPlaybackFrameStep step)
        {
            CaptureOneShots();
            RefreshWeights(step);
        }

        // 尾段求值或新帧采集后，从真实覆盖与剩余图步长重新预测各层的接管。
        internal void RefreshWeights(in CharacterPlaybackFrameStep step)
        {''')
sub('            CaptureOneShots();\n            blendDelta = clips.LayerBlendDelta(dt);\n            PlaybackClockScale = dt > 0f ? blendDelta / dt : 0f;', '''            blendDelta = step.GraphDelta;
            PlaybackClockScale = step.FrameDelta > 0f ? blendDelta / step.FrameDelta : 0f;''')
write()
use('CharacterAnimationPipeline')
sub('modules.Resources.CaptureWeights(deltaTime);','modules.Resources.CaptureWeights(modules.Player.CaptureFrame(now, deltaTime));')
sub('            if (drained) batch.Observed = modules.Playback.Capture(committedPlan.FullBody, batch.Input.Feet).ForPhase(modules.Intent.Phase);','''            var step = modules.Player.Remaining;
            if (drained)
            {
                modules.Resources.RefreshWeights(step);
                batch.Observed = modules.Playback.Capture(committedPlan.FullBody, batch.Input.Feet).ForPhase(modules.Intent.Phase);
            }''')
sub('            float deltaTime = batch.Input.DeltaTime;\n','')
sub('modules.Player.ApplyPlan(plan, deltaTime);','modules.Player.ApplyPlan(plan, step);')
sub('Ik.Prepare(selection, motion, plan, deltaTime, batch.Input.Time);','Ik.Prepare(selection, motion, plan, step);')
sub('modules.Player.EvaluatePose(plan);','modules.Player.EvaluatePose(plan, step);')
sub('Ik.Reset(); modules.Player.CloseGraph();','Ik.Reset(); modules.Player.ResetPlayback();')
write()

