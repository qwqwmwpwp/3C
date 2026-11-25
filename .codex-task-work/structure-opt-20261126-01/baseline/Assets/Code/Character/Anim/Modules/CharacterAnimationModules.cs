using System;
using System.Collections.Generic;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>一个角色的模块实例。重新装配时保留仍在使用的动作、步态时钟和播放图。</summary>
    internal sealed class CharacterAnimationModules
    {
        private readonly Animator animator;
        private readonly CharacterBehaviorAnimator owner;
        private readonly CharacterClipPlayback clips;
        private CharacterAnimationConfiguration config;
        internal CharacterAnimationPlayer Player { get; }
        internal CharacterAnimationComposer Composer { get; }
        internal CharacterAnimationIntentConverter Intent { get; }
        internal CharacterAnimationResourceCatalog Resources { get; private set; }
        internal CharacterMotionMatcher Matcher { get; private set; }
        internal CharacterPlaybackObserver Playback { get; private set; }
        internal CharacterLocomotionPoseSelector Poses { get; private set; }
        internal CharacterBehaviorAnimationBindings Bindings { get; }
        internal CharacterAnimationIk Ik { get; private set; }

        // 以当前行为许可初始化动作模块，物理观测由流水线单独接收。
        internal CharacterAnimationModules(Animator animator, CharacterBehaviorAnimator owner,
            CharacterAnimationConfiguration config, CharacterBehaviorObservation seed)
        {
            this.animator = animator;
            this.owner = owner;
            this.config = config;
            clips = new CharacterClipPlayback(animator, config);
            Player = new CharacterAnimationPlayer(animator, clips);
            BindGround(config);
            Intent = new CharacterAnimationIntentConverter(config);
            Composer = new CharacterAnimationComposer(Resources.Rules, Resources.HasBinding);
            Bindings = new CharacterBehaviorAnimationBindings(config.BehaviorAnimations, Composer, Resources, owner);
            if (seed.FrameNumber != 0) Bindings.Observe(seed, seed: true);
            Ik = CreateIk(config);
        }

        // 按本次组合创建姿态校正、惯性与肢体求解。
        private CharacterAnimationIk CreateIk(CharacterAnimationConfiguration next) =>
            new CharacterAnimationIk(animator, Player, clips, owner.transform, next.IkChannels, next.WalkableSlope,
                next.Orientation, next.PoseInertia, next.InertiaBones, next, owner.modelDropLimit, Ik);

        // 在同一个播放图上装配地面资源、观测与选姿。
        private void BindGround(CharacterAnimationConfiguration next)
        {
            Resources = new CharacterAnimationResourceCatalog(clips, next, owner);
            Matcher = new CharacterMotionMatcher(animator, Resources, next);
            Playback = new CharacterPlaybackObserver(animator, Resources, clips);
            Poses = new CharacterLocomotionPoseSelector(Resources, next);
        }

        // 依据模块差异替换所需阶段，并交接仍有效的动作与姿态输入。
        internal void Reconfigure(CharacterAnimationConfiguration next, CharacterBehaviorObservation seed)
        {
            var before = config.Modules;
            var after = next.Modules;
            bool gaitChanged = before.Gait != after.Gait || before.StartTurn != after.StartTurn || before.Stop != after.Stop;
            bool groundChanged = gaitChanged || before.Match != after.Match;
            var replaced = ChangedKeys(config.Actions, next.Actions);
            bool mappingsChanged = !SameOrder(config.BehaviorAnimations, next.BehaviorAnimations);
            foreach (string key in replaced) Composer.WithdrawKey(key);
            if (groundChanged)
            {
                BindGround(next);
                clips.ConfigurePaces(next);
                Resources.RefreshBindings();
                Poses.BindClips();
                Composer.RebindRules(Resources);
            }
            else if (replaced.Count != 0 || mappingsChanged)
                Resources.ReplaceActions(next, owner);

            if (groundChanged)
            {
                Intent.Configure(next);
                if (before.Gait != after.Gait) Intent.ResetGait(seed.Behavior);
            }
            Bindings.Reconfigure(next.BehaviorAnimations, Resources, seed, replaced);
            if (groundChanged || before.VisualRoot != after.VisualRoot || before.TransitionInertia != after.TransitionInertia ||
                config.Orientation != next.Orientation || config.PoseInertia != next.PoseInertia || config.SamplesFeet != next.SamplesFeet ||
                !SameChannels(config.IkChannels, next.IkChannels))
            {
                var previous = Ik;
                Ik = CreateIk(next);
                Ik.TakeOver(previous);
            }
            Resources.Transitions = next.Transitions;
            config = next;
        }

        private static HashSet<string> ChangedKeys(CharacterAnimationActionSettings[] before, CharacterAnimationActionSettings[] after)
        {
            var previous = new Dictionary<string, CharacterAnimationActionSettings>(StringComparer.Ordinal);
            foreach (var action in before ?? Array.Empty<CharacterAnimationActionSettings>())
                if (action != null && !string.IsNullOrWhiteSpace(action.Key)) previous[action.Key] = action;
            var changed = new HashSet<string>(previous.Keys, StringComparer.Ordinal);
            foreach (var action in after ?? Array.Empty<CharacterAnimationActionSettings>())
            {
                if (action == null || string.IsNullOrWhiteSpace(action.Key)) continue;
                if (previous.TryGetValue(action.Key, out var old) && ReferenceEquals(old, action)) changed.Remove(action.Key);
                else changed.Add(action.Key);
            }
            return changed;
        }

        private static bool SameOrder<T>(T[] a, T[] b) where T : class
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (!ReferenceEquals(a[i], b[i])) return false;
            return true;
        }

        private static bool SameChannels(CharacterBoundIkChannel[] a, CharacterBoundIkChannel[] b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (!ReferenceEquals(a[i].Tuning, b[i].Tuning)) return false;
            return true;
        }
    }
}
