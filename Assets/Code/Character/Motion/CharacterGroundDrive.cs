using System;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>在一个物理步内按阶段时长分配减速度，再以合成后的速度求解位移。</summary>
    internal readonly struct CharacterGroundBrake
    {
        private readonly float mainDeceleration;
        private readonly float leadInDeceleration;
        public float LeadInSeconds { get; }
        public bool IsActive => mainDeceleration > 0f;

        // 记录本步开始时的两段减速度和剩余轻制动时长。
        public CharacterGroundBrake(float mainDeceleration, float leadInDeceleration, float leadInSeconds)
        {
            this.mainDeceleration = mainDeceleration;
            this.leadInDeceleration = leadInDeceleration;
            LeadInSeconds = leadInSeconds;
        }

        // 计算指定时间内进入主制动的时长。
        public float MainSeconds(float dt) => InputMath.Elapsed(dt, LeadInSeconds);

        // 按本步两阶段的总减速量推进世界速度。
        public Vector3 Apply(Vector3 velocity, float dt) =>
            Vector3.MoveTowards(velocity, Vector3.zero, SpeedReduction(dt, LeadInSeconds));

        // 按固定步提交的末速度累计世界位移，窗口落在步内时按该步速度截取。
        public Vector3 Travel(Vector3 velocity, float horizon, float dt)
        {
            float initialSpeed = velocity.magnitude;
            if (initialSpeed <= 0f || horizon <= 0f || dt <= 0f) return Vector3.zero;
            double speed = initialSpeed;
            double wholeSteps = Math.Floor((double)horizon / dt);
            double travel = IntegrateSteps(ref speed, dt, wholeSteps, out _);
            double elapsed = wholeSteps * dt;
            double remainder = horizon - elapsed;
            if (remainder > 0d && speed > 0d)
            {
                float nextLead = (float)Math.Max(0d, LeadInSeconds - elapsed);
                double nextSpeed = Math.Max(0d, speed - SpeedReduction(dt, nextLead));
                travel += nextSpeed * remainder;
            }
            return velocity * (float)(travel / initialSpeed);
        }

        // 扣除已执行的轻制动时间，受阻或静止时直接进入主制动。
        public CharacterGroundBrake AfterStep(float dt, bool keepLeadIn = true) =>
            new CharacterGroundBrake(mainDeceleration, leadInDeceleration,
                keepLeadIn ? InputMath.Elapsed(LeadInSeconds, dt) : 0f);

        // 按本步落在轻制动与主制动阶段的时长合成减速量。
        private float SpeedReduction(float dt, float leadIn)
        {
            float main = InputMath.Elapsed(dt, leadIn);
            return leadInDeceleration * (dt - main) + mainDeceleration * main;
        }

        // 侧面接触且沿行进方向至少损失一半速度时将制动判为受阻。
        public static bool IsObstructed(Vector3 expectedVelocity, Vector3 solvedVelocity, CollisionFlags contacts)
        {
            if ((contacts & CollisionFlags.Sides) == 0)
                return false;
            Vector3 forward = Vector3.ProjectOnPlane(expectedVelocity, Vector3.up);
            float expectedSpeedSquared = forward.sqrMagnitude;
            return expectedSpeedSquared > 0.000001f &&
                Vector3.Dot(solvedVelocity, forward) <= expectedSpeedSquared * 0.5f;
        }

        // 按实际固定步积分顺序推算完全停步的水平距离与时间。
        public void PredictStop(Vector3 velocity, float dt, out float distance, out float seconds)
        {
            double speed = velocity.magnitude;
            double travel = IntegrateSteps(ref speed, dt, double.PositiveInfinity, out double steps);
            float flatRatio = velocity.sqrMagnitude > 0f
                ? Vector3.ProjectOnPlane(velocity, Vector3.up).magnitude / velocity.magnitude : 0f;
            distance = (float)(travel * flatRatio);
            seconds = (float)(steps * dt);
        }

        // 累计给定数量的完整物理步或提前停稳，返回沿速度方向的距离、段末速度与步数。
        private double IntegrateSteps(ref double speed, float dt, double maximumSteps, out double steps)
        {
            double travel = 0d;
            steps = 0d;
            // 匀减速的连续整步直接按等差数列求和，中间最多只有一个跨阶段的步。
            double leadSteps = Math.Floor((double)LeadInSeconds / dt);
            double leadRemainder = LeadInSeconds - leadSteps * dt;
            AccumulateSteps(ref speed, dt, leadInDeceleration, Math.Min(leadSteps, maximumSteps), ref travel, ref steps);
            if (speed > 0d && steps < maximumSteps && leadRemainder > dt * 0.00001d)
            {
                speed = Math.Max(0d, speed - SpeedReduction(dt, (float)leadRemainder));
                travel += speed * dt;
                steps++;
            }
            AccumulateSteps(ref speed, dt, mainDeceleration, maximumSteps - steps, ref travel, ref steps);
            return travel;
        }

        // 按等差数列累加同一减速度下的完整物理步。
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

    /// <summary>记录本步已执行的地面控制量，结合求解结果提交地面观测。</summary>
    internal readonly struct CharacterGroundDrive
    {
        private readonly Vector3 normal;
        private readonly Vector3 target;
        private readonly float acceleration;
        private readonly CharacterGroundBrake brake;
        private readonly Vector3 entryVelocity;
        private readonly CharacterGroundSteering steering;
        private readonly float yawRate;

        // 保存本次驱动实际采用的接触面、目标速度、制动与转向参数。
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

        // 核对着地状态，保存本步实际速度、制动进度和对应的推算起点。
        public CharacterGroundObservation Observe(Vector3 solvedVelocity, Vector3 displacement, float dt,
            CollisionFlags contacts, in CharacterGroundObservation previous, Vector3 facing = default, float turnRate = 0f)
        {
            if (normal.y <= 0f || (contacts & CollisionFlags.Below) == 0 || dt <= 0f)
                return default;
            Vector3 current = Vector3.ProjectOnPlane(solvedVelocity, normal);
            if (!brake.IsActive)
            {
                var prediction = new CharacterGroundPrediction(current, target, acceleration, facing, turnRate, steering, yawRate, dt);
                return new CharacterGroundObservation(prediction);
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
            var stopping = new CharacterGroundPrediction(current, target, acceleration, facing, turnRate, steering, yawRate, dt, remaining);
            return new CharacterGroundObservation(stopping, true, remaining.LeadInSeconds > 0f, obstructed,
                initialDistance, initialSeconds, previous.ActualStopTravel + stepTravel, mainTravel, distance, seconds);
        }
    }
}
