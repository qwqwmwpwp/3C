using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 将角色的求解、行为许可和已发布预测转换为动画输入，适配的状态键仅在这里解释。
    internal sealed class ThreeCAnimationMotionSource
    {
        private static readonly AnimationStateSet stateLayout = CreateStateLayout();
        private ForecastSnapshot forecast;
        private GroundProjection groundBlock;

        // 按角色行为位登记状态键；项目扩展位使用稳定的位序键。
        private static AnimationStateSet CreateStateLayout()
        {
            var keys = new string[64];
            keys[0] = "move";
            keys[1] = "dash";
            keys[2] = "guard";
            keys[3] = "attack";
            for (int bit = 4; bit < keys.Length; bit++) keys[bit] = "behavior." + bit;
            return new AnimationStateSet(keys);
        }

        // 提取与动画选择有关的许可和运动覆盖，过滤其余游戏逻辑。
        internal static CharacterAnimationContext FromBehavior(in CharacterBehaviorFrameResult behavior, string motionOverrideKey)
        {
            return new CharacterAnimationContext
            {
                States = stateLayout.WithActiveBits((ulong)behavior.ActiveBehaviors),
                MoveApproved = behavior.MoveApproved,
                MoveEnded = behavior.MoveEnded,
                CanStartMoving = behavior.MoveApproved && behavior.OrdinarySpeedLimit > 0f,
                MotionSource = behavior.DisplacementSource switch
                {
                    CharacterDisplacementSource.BasicMovement => AnimationMotionSource.Locomotion,
                    CharacterDisplacementSource.Dash => AnimationMotionSource.Override,
                    CharacterDisplacementSource.None => AnimationMotionSource.None,
                    _ => AnimationMotionSource.Other
                },
                OverrideKey = behavior.DashActive ? motionOverrideKey : null,
                OverrideStarted = behavior.DashStarted,
                ActionActive = behavior.Attack.InstanceId != 0,
                RecoversMovement = behavior.ActionMotion.RecoversMovement
            };
        }

        // 将同一已结算固定步的位移、输入意图、地面参数和步态准入标志一起提交。
        internal CharacterAnimationMovement FromFrame(in CharacterControllerFrameResult frame, string motionOverrideKey, bool preserveEntryGait = false)
        {
            if (frame.FrameNumber <= 0) return default;
            return new CharacterAnimationMovement
            {
                FrameNumber = frame.FrameNumber,
                SimulationVersion = frame.SimulationVersion,
                DeltaTime = frame.DeltaTime,
                ActualDisplacement = frame.ActualDisplacement,
                ExpectedDisplacement = frame.ExpectedDisplacement,
                Facing = frame.FacingDirection,
                ExpectedFacing = FacingIntent(frame),
                MoveDirection = frame.Input.Direction,
                Gait = ToGait(frame.Input.Gait),
                Contacts = frame.CollisionFlags,
                Context = FromBehavior(frame.Behavior, motionOverrideKey),
                PreserveEntryGait = preserveEntryGait,
                SuppressForecastProjection = !frame.Ground.IsValid,
                GroundProjection = CaptureGround(frame)
            };
        }

        // 按马达采用的次序读取本步目标朝向：动作起手或接管优先，其余步使用已获准的移动输入。
        private static Vector3? FacingIntent(in CharacterControllerFrameResult frame)
        {
            var behavior = frame.Behavior;
            var motion = behavior.ActionMotion;
            if (motion.ControlsFacing || behavior.ActionMotionStarted && motion.SnapsFacingOnStart)
                return PlanarHeading(motion.Facing);
            return behavior.MoveApproved ? PlanarHeading(frame.Input.Direction) : null;
        }

        // 无水平朝向指令时交由动画采用本步期望平移或实际朝向。
        private static Vector3? PlanarHeading(Vector2 direction) => direction.sqrMagnitude > 0.000001f
            ? new Vector3(direction.x, 0f, direction.y) : (Vector3?)null;

        // 以连续步号保存只写一次的地面参数，批次可跨帧持有任意已经提交的槽位。
        private IAnimationGroundProjection CaptureGround(in CharacterControllerFrameResult frame)
        {
            if (!frame.Ground.IsValid) return null;
            if (groundBlock == null || !groundBlock.CanAppend(frame.FrameNumber))
                groundBlock = new GroundProjection(frame.FrameNumber);
            groundBlock.Append(frame.Ground);
            return groundBlock;
        }

        // 把控制器速度档转换为动画可独立使用的步态枚举。
        private static AnimationGait ToGait(CharacterMoveGait gait) => gait == CharacterMoveGait.Walk
            ? AnimationGait.Walk : gait == CharacterMoveGait.Sprint ? AnimationGait.Sprint : AnimationGait.Run;

        // 一个预测版本只包装一次，同帧撤回或重新发布时返回新的可见结果。
        internal AnimationTrajectoryPublication ReadForecast(CharacterMovementForecast published)
        {
            var snapshot = published?.Snapshot();
            if (snapshot == null) forecast = null;
            else if (forecast?.Source != snapshot) forecast = new ForecastSnapshot(snapshot);
            return new AnimationTrajectoryPublication(published != null, forecast);
        }

        // 在求解时冻结驱动值，并在匹配库实际请求的窗口上重算投影。
        private sealed class GroundProjection : IAnimationGroundProjection
        {
            private readonly CharacterGroundObservation[] steps = new CharacterGroundObservation[32];
            private readonly long first;
            private int written;

            // 一块只覆盖同一段连续步号，装满后由来源切换到新块。
            internal GroundProjection(long firstFrame) => first = firstFrame;

            // 主线程仅追加连续且尚未写入的槽位。
            internal bool CanAppend(long frame) => written < steps.Length && frame == first + written;

            // 发布前写入该步一次，后续采样可与其他槽位的追加并行。
            internal void Append(in CharacterGroundObservation ground) => steps[written++] = ground;

            // 同窗查询保存原始求解器的制动距离、朝向积分与时间尺度。
            public CharacterGroundTrajectory Project(long frameNumber, Vector2 windows)
            {
                long offset = frameNumber - first;
                if (offset < 0 || offset >= steps.Length) return default;
                ref readonly var ground = ref steps[(int)offset];
                if (!ground.IsValid) return default;
                var sample = ground.Prediction.Sample(CharacterGroundTrajectory.LookAheadSeconds, windows.x, windows.y);
                var match = new CharacterMatchTrajectory(sample.NearTravel, sample.FarTravel, ground.Prediction.Facing,
                    sample.NearYaw, sample.FarYaw, windows);
                return new CharacterGroundTrajectory(sample.Velocity, ground.IsBraking, ground.IsBrakeLeadIn, ground.IsBrakeObstructed,
                    ground.InitialStopDistance, ground.InitialStopSeconds, ground.ActualStopTravel, ground.MainBrakeTravel,
                    ground.RemainingStopDistance, ground.RemainingStopSeconds, match);
            }
        }

        // 只读转接一个已经完成且从此不再写入的预测版本。
        private sealed class ForecastSnapshot : IAnimationTrajectorySnapshot
        {
            internal readonly CharacterMovementForecast Source;
            public long Version => Source.Version;
            public bool Available => Source.Available;

            // 引用已冻结的采样数组，保持不同动画窗口使用同一组数据。
            internal ForecastSnapshot(CharacterMovementForecast source) => Source = source;

            // 保留原预测器的时钟、插值和动态接触可信边界。
            public bool TrySample(double now, float secondsAhead, out AnimationTrajectorySample sample)
            {
                bool found = Source.TrySample(now, secondsAhead, out var value);
                sample = found ? Convert(value) : default;
                return found;
            }

            // 将控制器预演的普通移动交还时刻暴露给动画匹配。
            public bool TryReturnToMovement(double now, out float secondsAhead, out AnimationTrajectorySample sample)
            {
                bool found = Source.TryReturnToMovement(now, out secondsAhead, out var value);
                sample = found ? Convert(value) : default;
                return found;
            }

            // 从同一预测点读取空间量与动画归属假设。
            private static AnimationTrajectorySample Convert(in CharacterForecastSample source) => new AnimationTrajectorySample
            {
                Position = source.Position,
                Velocity = source.Velocity,
                Rotation = source.Rotation,
                Gait = ToGait(source.Gait),
                ExternallyControlled = source.ExternallyControlled,
                AnticipatesMovementReturn = source.AnticipatesMovementReturn,
                IsLocomotion = source.MoveApproved && (source.Behaviors & CharacterBehaviorId.Dash) == 0
            };
        }
    }
}
