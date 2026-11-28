using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 保存匹配时延、当前播放与预测交还的候选快照。
    internal struct GroundMatchHistory
    {
        internal long HandledTurn, HandledReverse, BaseInstance, PendingTurn;
        internal float Hold, TurnWait, SearchDelay;
        internal GroundTrackGait LastTarget, LastAlternate;
        internal GroundMotionUse LastUse;
        internal GroundCandidateRule LastRule, PreviewRule;
        internal CharacterMotionTrack StopTrack;
        internal GroundPoseQuery EntryQuery;
        internal CharacterLocomotionPhase EntryPhase;
        internal bool EntryWithoutPast;
        internal AnimationGait EntryInput;
        internal GroundTrackSide EntrySupport;
        internal Vector3 TurnDirection;
        internal CharacterMotionTrack Playing, PreviewTrack;
        internal int PlayingHash;
        internal bool ObservedPlaying;
        internal bool InputDirected;
        internal IAnimationTrajectorySnapshot PreviewSource;
        internal long HandledInputTurn;
        internal CharacterMatchResult Preview;
        internal string PreviewGait;
        internal GroundTrackSide PreviewSupport;
        internal double PreviewTime;

        // 独立的直接改向请求使用自己的续播等待窗口。
        internal void ObserveDirectTurn(long step)
        {
            if (PendingTurn == step) return;
            PendingTurn = step;
            TurnWait = 0f;
        }

        // 结算已处理的转向请求，输入急转同时结算同一固定步的直接改向。
        internal void CompleteTurn(long inputStep, long directStep)
        {
            if (inputStep != 0)
            {
                HandledInputTurn = inputStep;
                if (directStep == inputStep) HandledTurn = directStep;
            }
            else HandledTurn = directStep;
        }
    }

}
