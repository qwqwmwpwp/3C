namespace ThreeC.CharacterBehavior
{
    // 冻结动画采集时的运动、状态、骨姿与发布轨迹，供试选及同帧重选共用。
    internal readonly struct CharacterPresentationInput
    {
        internal readonly CharacterAnimationMovement Frame;
        internal readonly CharacterAnimationContext Context;
        internal readonly CharacterAnimationMotion Motion;
        internal readonly CharacterPlaybackObservation Observation;
        internal readonly CharacterFootObservation Feet;
        internal readonly double Time;
        internal readonly float DeltaTime;
        internal readonly CharacterBoneObservations Bones;
        internal readonly CharacterTrajectoryObservations Trajectory;
        internal readonly CharacterGroundTrajectory OriginalTrajectory;
        private readonly AnimationTrajectoryPublication publication;
        private readonly CharacterBasePoseSetup setup;
        internal IAnimationTrajectorySnapshot Forecast => publication.Snapshot;
        internal bool ForecastEnabled => publication.Enabled;

        // 以渲染时间采集当前策略的轨迹样本，保留直接观测供该策略按需投影。
        internal CharacterPresentationInput(CharacterAnimationMovement frame, CharacterAnimationContext context,
            CharacterAnimationMotion motion, AnimationTrajectoryPublication published, CharacterPlaybackObservation observation,
            CharacterFootObservation feet, float dt, double now, CharacterBoneObservations bones, CharacterBasePoseSetup setup)
        {
            Frame = frame;
            Context = context;
            publication = published;
            this.setup = setup;
            OriginalTrajectory = motion.Trajectory;
            Bones = bones;
            Motion = motion;
            Observation = observation;
            Feet = feet;
            DeltaTime = dt;
            Time = now;
            Trajectory = setup.CaptureTrajectory(published.Snapshot, now);
        }

        // 重新发布时使用原采集时刻和新冻结来源重新读取声明的轨迹点。
        private CharacterPresentationInput(in CharacterPresentationInput captured, AnimationTrajectoryPublication published)
        {
            this = captured;
            publication = published;
            Trajectory = setup?.CaptureTrajectory(published.Snapshot, Time) ?? default;
        }

        // 保留整个批次的快照，仅更新策略自身派生的运动观测。
        internal CharacterPresentationInput(in CharacterPresentationInput captured, in CharacterAnimationMotion motion)
        {
            this = captured;
            Motion = motion;
        }

        // 同一帧状态变更只影响候选条件，仍沿用该批次的所有运动观测。
        private CharacterPresentationInput(in CharacterPresentationInput captured, in CharacterAnimationContext context)
        {
            this = captured;
            Context = context;
        }

        // 为待提交的批次更新当前状态。
        internal CharacterPresentationInput WithContext(in CharacterAnimationContext context) => new CharacterPresentationInput(this, context);

        // 检测开关、撤回、发布及来源替换，使最终选姿使用同一来源的所有样本。
        internal CharacterPresentationInput RefreshForecast(AnimationTrajectoryPublication published, out bool changed)
        {
            changed = publication.Enabled != published.Enabled || !ReferenceEquals(Forecast, published.Snapshot);
            return changed ? new CharacterPresentationInput(this, published) : this;
        }
    }
}
