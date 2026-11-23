using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>混合中的各步态共用同一物理速度，各自按素材原速换算步频。</summary>
    internal readonly struct CharacterAnimationPlaybackRates
    {
        public readonly float Selected;
        public readonly float Walk;
        public readonly float Run;
        public readonly float Sprint;
        public readonly float Dash;
        public readonly float? StrideSpeed;

        public CharacterAnimationPlaybackRates(float? actualSpeed, float selected, float walk, float run,
            float sprint, float dash)
        {
            StrideSpeed = actualSpeed;
            Selected = ForClip(actualSpeed, selected);
            Walk = ForClip(actualSpeed, walk);
            Run = ForClip(actualSpeed, run);
            Sprint = ForClip(actualSpeed, sprint);
            Dash = ForClip(actualSpeed, dash);
        }

        private static float ForClip(float? actualSpeed, float nominalSpeed) =>
            actualSpeed.HasValue && nominalSpeed > 0f ? actualSpeed.Value / nominalSpeed : 1f;
    }

    /// <summary>记录本帧哪些层实际下发了播放命令；保持、未选中及不可用的层没有播放起点。</summary>
    internal readonly struct CharacterAnimationPlaybackEmission
    {
        public bool FullBody { get; }
        public bool LowerBody { get; }
        public bool UpperBody { get; }
        public bool Overlay { get; }

        public bool For(CharacterAnimationBodyRegion source) => source switch
        {
            CharacterAnimationBodyRegion.FullBody => FullBody,
            CharacterAnimationBodyRegion.UpperBody => UpperBody,
            CharacterAnimationBodyRegion.LowerBody => LowerBody,
            _ => Overlay
        };

        public CharacterAnimationPlaybackEmission(bool fullBody, bool lowerBody, bool upperBody, bool overlay)
        {
            FullBody = fullBody;
            LowerBody = lowerBody;
            UpperBody = upperBody;
            Overlay = overlay;
        }
    }

    internal interface ICharacterPlaybackBackend
    {
        bool HasState(CharacterAnimationBodyRegion source, int hash);
        bool CanPlayOnce(int hash);
        float? StateSeconds(int state);
        float LayerWeight(CharacterAnimationBodyRegion source);
        float LayerBlendDelta(float deltaTime);
        CharacterLayerObservation Observe(CharacterAnimationBodyRegion source, bool readLengths = false, bool readNext = false);
        CharacterAnimationPlaybackEmission Apply(in CharacterAnimationPlaybackPlan plan, float deltaTime);
    }

    /// <summary>按当前控制器选定播放后端，统一管理暂停和播放图生命周期。</summary>
    internal sealed class CharacterAnimationPlayer
    {
        private readonly Animator animator;
        private readonly CharacterClipPlayback clips;
        private readonly CharacterControllerPlayback controllerPlayback;
        private RuntimeAnimatorController boundController;
        private bool paused;
        private float speedBeforePause;

        public ICharacterPlaybackBackend Active { get; private set; }
        public CharacterClipPlayback Direct => Active as CharacterClipPlayback;
        public RuntimeAnimatorController Controller => clips.Controller;
        public bool NeedsRebind => Active == null || Controller != boundController || clips.ControllerReassigned || clips.OverridesChanged();

        public CharacterAnimationPlayer(Animator animator, CharacterAnimationConfiguration config)
        {
            this.animator = animator;
            clips = new CharacterClipPlayback(animator, config);
            controllerPlayback = new CharacterControllerPlayback(animator, config, clips.BakedClip);
        }

        public void SetPaused(bool value)
        {
            if (paused == value) return;
            paused = value;
            if (paused)
            {
                speedBeforePause = animator.speed;
                animator.speed = 0f;
            }
            else animator.speed = speedBeforePause;
        }

        public void BindController()
        {
            clips.Close();
            controllerPlayback.InvalidateController();
            boundController = Controller;
            clips.BindClips();
            Active = clips.ExpectsBodyLibrary || clips.Available ? (ICharacterPlaybackBackend)clips : controllerPlayback;
        }

        internal void ConfigurePaces(CharacterAnimationConfiguration config) => clips.ConfigurePaces(config);

        public void CloseGraph() => clips.Close();

        public void ResetPlayback()
        {
            clips.Close();
            controllerPlayback.ResetPlayback();
        }

        public void Release()
        {
            clips.Close();
            if (animator) SetPaused(false);
            controllerPlayback.Release();
        }
    }
}
