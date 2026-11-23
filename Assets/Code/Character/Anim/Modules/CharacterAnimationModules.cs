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
        private CharacterAnimationConfiguration config;
        internal CharacterAnimationPlayer Player { get; }
        internal CharacterAnimationComposer Composer { get; }
        internal CharacterAnimationIntentConverter Intent { get; private set; }
        internal CharacterAnimationResourceCatalog Resources { get; private set; }
        internal CharacterMotionMatcher Matcher { get; private set; }
        internal CharacterPlaybackObserver Playback { get; private set; }
        internal CharacterLocomotionPoseSelector Poses { get; private set; }
        internal CharacterBehaviorAnimationBindings Bindings { get; }
        internal CharacterAnimationIk Ik { get; private set; }

        internal CharacterAnimationModules(Animator animator, CharacterBehaviorAnimator owner,
            CharacterAnimationConfiguration config, CharacterControllerFrameResult seed)
        {
            this.animator = animator;
            this.owner = owner;
            this.config = config;
            Player = new CharacterAnimationPlayer(animator, config);
            BindGround(config);
            Intent = new CharacterAnimationIntentConverter(config);
            Composer = new CharacterAnimationComposer(Resources.Rules, Resources.HasBinding);
            Bindings = new CharacterBehaviorAnimationBindings(config.BehaviorAnimations, Composer, Resources, owner);
            if (seed.FrameNumber != 0) Bindings.Observe(seed, seed: true);
            Ik = CreateIk(config);
        }

        private CharacterAnimationIk CreateIk(CharacterAnimationConfiguration next) =>
            new CharacterAnimationIk(animator, Player, owner.transform, next.IkChannels, next.WalkableSlope,
                next.PoseInertia, next.InertiaBones, next.SamplesFeet, owner.modelDropLimit, Ik);

        private void BindGround(CharacterAnimationConfiguration next)
        {
            Resources = new CharacterAnimationResourceCatalog(Player, next, owner);
            Matcher = new CharacterMotionMatcher(animator, Resources, next);
            Playback = new CharacterPlaybackObserver(animator, Resources, Player);
            Poses = new CharacterLocomotionPoseSelector(Resources, next);
        }

        internal void Reconfigure(CharacterAnimationConfiguration next, CharacterControllerFrameResult seed)
        {
            var before = config.Modules;
            var after = next.Modules;
            bool modeChanged = (before == null) != (after == null);
            bool gaitChanged = modeChanged || before?.Gait != after?.Gait || before?.StartTurn != after?.StartTurn ||
                before?.Stop != after?.Stop;
            bool groundChanged = gaitChanged || before?.Match != after?.Match;
            var replaced = ChangedKeys(config.Actions, next.Actions);
            bool mappingsChanged = !SameOrder(config.BehaviorAnimations, next.BehaviorAnimations);
            foreach (string key in replaced) Composer.WithdrawKey(key);
            if (groundChanged)
            {
                BindGround(next);
                Player.ConfigurePaces(next);
                Resources.RefreshBindings();
                Poses.BindClips();
                Composer.RebindRules(Resources);
            }
            else if (replaced.Count != 0 || mappingsChanged)
                Resources.ReplaceActions(next, owner);

            if (groundChanged)
            {
                Intent.Configure(next);
                if (modeChanged || before?.Gait != after?.Gait) Intent.Reset(seed.Behavior);
            }
            Bindings.Reconfigure(next.BehaviorAnimations, Resources, seed, replaced);
            if (config.PoseInertia != next.PoseInertia || config.SamplesFeet != next.SamplesFeet ||
                !SameChannels(config.IkChannels, next.IkChannels))
            {
                var previous = Ik;
                Ik = CreateIk(next);
                Ik.TakeOver(previous);
            }
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
