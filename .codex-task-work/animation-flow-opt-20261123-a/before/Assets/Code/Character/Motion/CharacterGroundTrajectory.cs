using System;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>由本步实际驱动参数和求解后的速度得到的地面预测，距离均取世界水平面。</summary>
    public readonly struct CharacterGroundTrajectory
    {
        public const float LookAheadSeconds = 0.06f;
        public bool IsValid { get; }
        public Vector3 FutureVelocity { get; }
        public CharacterMatchTrajectory Match { get; }
        public bool IsBraking { get; }
        /// <summary>本固定步提交后，是否仍保有轻制动时间。</summary>
        public bool IsBrakeLeadIn { get; }
        /// <summary>本固定步中发生侧面接触，并且求解后沿预计行进方向损失至少一半速度。</summary>
        public bool IsBrakeObstructed { get; }
        public float InitialStopDistance { get; }
        public float InitialStopSeconds { get; }
        public float ActualStopTravel { get; }
        /// <summary>主制动累计的实际平面位移；跨阶段的一步按主制动占用时间计入。</summary>
        public float MainBrakeTravel { get; }
        public float RemainingStopDistance { get; }
        public float RemainingStopSeconds { get; }

        internal CharacterGroundTrajectory WithForecast(Vector3 velocity, CharacterMatchTrajectory match)
            => new CharacterGroundTrajectory(velocity, IsBraking, IsBrakeLeadIn, IsBrakeObstructed, InitialStopDistance,
                InitialStopSeconds, ActualStopTravel, MainBrakeTravel, RemainingStopDistance, RemainingStopSeconds, match);

        internal CharacterGroundTrajectory(Vector3 futureVelocity, bool braking, bool leadIn, bool obstructed,
            float initialDistance, float initialSeconds, float travel, float mainTravel,
            float remainingDistance, float remainingSeconds, CharacterMatchTrajectory match = default)
        {
            IsValid = true;
            Match = match;
            FutureVelocity = Vector3.ProjectOnPlane(futureVelocity, Vector3.up);
            IsBraking = braking;
            IsBrakeLeadIn = leadIn;
            IsBrakeObstructed = obstructed;
            InitialStopDistance = initialDistance;
            InitialStopSeconds = initialSeconds;
            ActualStopTravel = travel;
            MainBrakeTravel = mainTravel;
            RemainingStopDistance = remainingDistance;
            RemainingStopSeconds = remainingSeconds;
        }
    }

    /// <summary>在一个物理步内按阶段时长分配减速度，再以合成后的速度求解位移。</summary>
    internal readonly struct CharacterGroundBrake
    {
        private readonly float mainDeceleration;
        private readonly float leadInDeceleration;
        public float LeadInSeconds { get; }
        public bool IsActive => mainDeceleration > 0f;

        public CharacterGroundBrake(float mainDeceleration, float leadInDeceleration, float leadInSeconds)
        {
            this.mainDeceleration = mainDeceleration;
            this.leadInDeceleration = leadInDeceleration;
            LeadInSeconds = leadInSeconds;
        }

        public float MainSeconds(float dt) => MainSeconds(dt, LeadInSeconds);
        private static float MainSeconds(float dt, float leadIn) => InputMath.Elapsed(dt, leadIn);

        public Vector3 Apply(Vector3 velocity, float dt) =>
            Vector3.MoveTowards(velocity, Vector3.zero, SpeedReduction(dt, LeadInSeconds));

        public Vector3 Travel(Vector3 velocity, float horizon)
        {
            float lead = Mathf.Min(LeadInSeconds, horizon);
            Vector3 first = CharacterMatchTrajectory.PredictTravel(velocity, Vector3.zero, leadInDeceleration, lead);
            return first + CharacterMatchTrajectory.PredictTravel(Apply(velocity, lead), Vector3.zero,
                mainDeceleration, horizon - lead);
        }

        public CharacterGroundBrake AfterStep(float dt, bool keepLeadIn = true) =>
            new CharacterGroundBrake(mainDeceleration, leadInDeceleration,
                keepLeadIn ? InputMath.Elapsed(LeadInSeconds, dt) : 0f);

        private float SpeedReduction(float dt, float leadIn)
        {
            float main = MainSeconds(dt, leadIn);
            return leadInDeceleration * (dt - main) + mainDeceleration * main;
        }

        public static bool IsObstructed(Vector3 expectedVelocity, Vector3 solvedVelocity, CollisionFlags contacts)
        {
            if ((contacts & CollisionFlags.Sides) == 0)
                return false;
            Vector3 forward = Vector3.ProjectOnPlane(expectedVelocity, Vector3.up);
            float expectedSpeedSquared = forward.sqrMagnitude;
            return expectedSpeedSquared > 0.000001f &&
                Vector3.Dot(solvedVelocity, forward) <= expectedSpeedSquared * 0.5f;
        }

        public void PredictStop(Vector3 velocity, float dt, out float distance, out float seconds)
        {
            double speed = velocity.magnitude;
            double travel = 0d;
            double steps = 0d;
            // 匀减速的连续整步直接按等差数列求和，中间最多只有一个跨阶段的步。
            double leadSteps = Math.Floor((double)LeadInSeconds / dt);
            double leadRemainder = LeadInSeconds - leadSteps * dt;
            AccumulateSteps(ref speed, dt, leadInDeceleration, leadSteps, ref travel, ref steps);
            if (speed > 0d && leadRemainder > dt * 0.00001d)
            {
                speed = Math.Max(0d, speed - SpeedReduction(dt, (float)leadRemainder));
                travel += speed * dt;
                steps++;
            }
            AccumulateSteps(ref speed, dt, mainDeceleration, double.PositiveInfinity, ref travel, ref steps);
            float flatRatio = velocity.sqrMagnitude > 0f
                ? Vector3.ProjectOnPlane(velocity, Vector3.up).magnitude / velocity.magnitude : 0f;
            distance = (float)(travel * flatRatio);
            seconds = (float)(steps * dt);
        }

        private static void AccumulateSteps(ref double speed, float dt, float deceleration,
            double maximumSteps, ref double travel, ref double steps)
        {
            if (speed <= 0d || maximumSteps <= 0d)
                return;
            double decrement = (double)deceleration * dt;
            double toZero = Math.Ceiling(speed / decrement);
            double count = Math.Min(maximumSteps, toZero);
            double moving = Math.Min(count, toZero - 1d);
            travel += moving * dt * (speed - decrement * (moving + 1d) * 0.5d);
            speed = Math.Max(0d, speed - decrement * count);
            steps += count;
        }
    }

    /// <summary>记录 motor 已使用的地面控制量。物理求解后再预测，碰撞修正不会落后一拍。</summary>
    internal readonly struct CharacterGroundDrive
    {
        private readonly Vector3 normal;
        private readonly Vector3 target;
        private readonly float acceleration;
        private readonly CharacterGroundBrake brake;
        private readonly Vector3 entryVelocity;
        private readonly CharacterGroundSteering steering;
        private readonly float yawRate;

        public CharacterGroundDrive(Vector3 normal, Vector3 target, float acceleration, Vector3 entryVelocity,
            CharacterGroundBrake brake, CharacterGroundSteering steering, float yawRate)
        {
            this.normal = normal;
            this.target = target;
            this.acceleration = acceleration;
            this.brake = brake;
            this.entryVelocity = entryVelocity;
            this.steering = steering;
            this.yawRate = yawRate;
        }

        public CharacterGroundTrajectory Observe(Vector3 solvedVelocity, Vector3 displacement, float dt,
            CollisionFlags contacts, CharacterGroundTrajectory previous, Vector3 facing = default, float turnRate = 0f)
        {
            if (normal.y <= 0f || (contacts & CollisionFlags.Below) == 0 || dt <= 0f)
                return default;
            Vector3 current = Vector3.ProjectOnPlane(solvedVelocity, normal);
            float lookAhead = CharacterGroundTrajectory.LookAheadSeconds;
            if (!brake.IsActive)
            {
                Vector3 future;
                CharacterMatchTrajectory forecast;
                if (steering.Active)
                    forecast = CharacterMatchTrajectory.PredictSteering(steering, current, target, facing, yawRate, dt, out future);
                else
                {
                    future = Vector3.MoveTowards(current, target, acceleration * lookAhead);
                    float angle = CharacterGroundSteering.AngleTo(facing, new Vector2(target.x, target.z));
                    forecast = new CharacterMatchTrajectory(
                        CharacterMatchTrajectory.PredictTravel(current, target, acceleration, CharacterMatchTrajectory.NearSeconds),
                        CharacterMatchTrajectory.PredictTravel(current, target, acceleration, CharacterMatchTrajectory.FarSeconds), facing,
                        Mathf.Clamp(angle, -turnRate * CharacterMatchTrajectory.NearSeconds, turnRate * CharacterMatchTrajectory.NearSeconds),
                        Mathf.Clamp(angle, -turnRate * CharacterMatchTrajectory.FarSeconds, turnRate * CharacterMatchTrajectory.FarSeconds));
                }
                return new CharacterGroundTrajectory(future, false, false, false, 0f, 0f, 0f, 0f, 0f, 0f, forecast);
            }

            float initialDistance = previous.InitialStopDistance;
            float initialSeconds = previous.InitialStopSeconds;
            if (!previous.IsBraking)
                brake.PredictStop(entryVelocity, dt, out initialDistance, out initialSeconds);
            float stepTravel = Vector3.ProjectOnPlane(displacement, Vector3.up).magnitude;
            float mainTravel = previous.MainBrakeTravel + stepTravel * (brake.MainSeconds(dt) / dt);
            bool obstructed = CharacterGroundBrake.IsObstructed(brake.Apply(entryVelocity, dt), current, contacts);
            bool keepLeadIn = !obstructed && current.sqrMagnitude > 0.000001f;
            CharacterGroundBrake remaining = brake.AfterStep(dt, keepLeadIn);
            remaining.PredictStop(current, dt, out float distance, out float seconds);
            return new CharacterGroundTrajectory(remaining.Apply(current, lookAhead), true, remaining.LeadInSeconds > 0f,
                obstructed, initialDistance, initialSeconds, previous.ActualStopTravel + stepTravel, mainTravel, distance, seconds,
                new CharacterMatchTrajectory(remaining.Travel(current, CharacterMatchTrajectory.NearSeconds),
                    remaining.Travel(current, CharacterMatchTrajectory.FarSeconds), facing, 0f, 0f));
        }
    }
}
