using Unity.Collections;
using UnityEngine;
using UnityEngine.Animations;

namespace ThreeC.CharacterBehavior
{
    // 区分胸部和双臂使用的惯性响应区域。
    internal enum CharacterPoseRegion { Chest, LeftArm, RightArm }

    // 保存惯性作业使用的骨骼句柄、部位和区域权重。
    internal struct CharacterPoseBoneBinding
    {
        public TransformStreamHandle Handle;
        public CharacterPoseRegion Region;
        public float Share;
        public byte SharedTransition; // 同一骨骼的片段交接由通用切换模块执行。
    }

    internal struct CharacterPoseInertiaFrame
    {
        public int Revision;
        public float Strength, ChestSeconds, ArmSeconds, ChestLimit, ArmLimit;
        public Vector3 Allowance;
        public Vector3 BrakeRotation; // 世界旋转向量（度），各骨骼在父空间求解。
        public long StopInstance;
        public float StopLoad;
        public Vector3 StopLeftArm, StopRightArm;
    }

    // 保存每根惯性骨骼的上一帧姿态、角速度和衰减状态。
    internal struct CharacterPoseBoneHistory
    {
        public byte Initialized, AwaitIncomingVelocity, RebaseVelocity;
        public int Revision;
        public Quaternion LastTarget, LastOutput;
        public Quaternion LastBase;
        public Vector3 BaseVelocity, HandoffVelocity;
        public long StopInstance;
        public float PeakLoad, LastStopAngle, ExitAllowance;
        public float LastInfluence;
        public Vector3 Velocity, Offset, OffsetVelocity, Lean, LeanVelocity;
        public CharacterStopSwingCarry StopSwing;
    }

    // 保存程序性收步摆臂相对当帧基础输出的余量，只在收步中断时单独衰减。
    internal struct CharacterStopSwingCarry
    {
        private Vector3 previousSwing, swingVelocity, carry, carryVelocity;
        private float previousInfluence;

        // 限制骤降的握持自由度，仅交接上一帧显示的摆臂差及其角速度。
        internal Quaternion Apply(Quaternion baseOutput, Quaternion current, bool interrupted,
            bool rebase, float influence, float seconds, float dt)
        {
            float ratio = previousInfluence > 0f ? Mathf.Clamp01(influence / previousInfluence) : 0f;
            previousSwing *= ratio;
            swingVelocity *= ratio;
            carry *= ratio;
            carryVelocity *= ratio;
            if (rebase) swingVelocity = carryVelocity = Vector3.zero;

            if (interrupted)
            {
                Quaternion outgoing = CharacterPoseInertia.Rotation(previousSwing) * baseOutput;
                carry = CharacterPoseInertia.Difference(outgoing, current);
                carryVelocity = swingVelocity;
            }
            else CharacterPoseInertia.Spring(ref carry, ref carryVelocity, Vector3.zero, seconds, dt);

            Quaternion output = CharacterPoseInertia.Rotation(carry) * current;
            Vector3 shown = CharacterPoseInertia.Difference(output, baseOutput);
            if (!interrupted && !rebase)
                swingVelocity = Vector3.ClampMagnitude((shown - previousSwing) / dt, 360f * influence);
            previousSwing = shown;
            previousInfluence = influence;
            return output;
        }
    }

    // 求解胸部和双臂骨骼的姿态惯性。
    internal static class CharacterPoseInertia
    {
        // 根据当帧姿态和历史角速度计算并写入骨骼的局部旋转。
        public static void Apply(AnimationStream stream, NativeArray<CharacterPoseBoneBinding> bindings,
            NativeArray<CharacterPoseBoneHistory> history, in CharacterPoseInertiaFrame frame,
            float leftGrip, float rightGrip, bool frozen)
        {
            for (int i = 0; i < bindings.Length; i++)
            {
                var binding = bindings[i];
                var handle = binding.Handle;
                if (!handle.IsValid(stream) || frame.Strength <= 0f)
                {
                    history[i] = default;
                    continue;
                }
                var state = history[i];
                if (frozen)
                {
                    if (state.Initialized != 0)
                        handle.SetLocalRotation(stream, state.LastOutput);
                    continue;
                }
                bool chest = binding.Region == CharacterPoseRegion.Chest;
                float freedom = chest ? frame.Allowance.x * (1f - 0.7f * Mathf.Max(leftGrip, rightGrip))
                    : binding.Region == CharacterPoseRegion.LeftArm ? frame.Allowance.y * (1f - leftGrip)
                    : frame.Allowance.z * (1f - rightGrip);
                float influence = Mathf.Clamp01(freedom * frame.Strength);
                float limit = (chest ? frame.ChestLimit : frame.ArmLimit) * binding.Share * influence;
                float seconds = chest ? frame.ChestSeconds : frame.ArmSeconds;
                var reference = handle.GetLocalRotation(stream);
                if (state.Initialized == 0)
                {
                    state.Initialized = 1;
                    state.Revision = frame.Revision;
                    state.LastBase = state.LastOutput = state.LastTarget = reference;
                }
                float dt = stream.deltaTime;
                if (dt <= 0f)
                {
                    handle.SetLocalRotation(stream, state.LastOutput);
                    history[i] = state;
                    continue;
                }

                if (influence < state.LastInfluence) // 影响权重降低时收窄已有偏移，避免恢复时重新摆动。
                {
                    float ratio = influence / state.LastInfluence;
                    state.Offset *= ratio;
                    state.OffsetVelocity *= ratio;
                    state.Lean *= ratio;
                    state.LeanVelocity *= ratio;
                    state.LastStopAngle *= ratio;
                    state.ExitAllowance *= ratio;
                }
                state.LastInfluence = influence;
                if (state.RebaseVelocity != 0)
                {
                    // 暂停期间不采样速度。保留当前偏移，并以恢复帧作为新的差分原点。
                    state.Velocity = state.OffsetVelocity = state.LeanVelocity = Vector3.zero;
                    state.BaseVelocity = state.HandoffVelocity = Vector3.zero;
                    state.AwaitIncomingVelocity = 0;
                }

                bool stopping = frame.StopInstance != 0;
                bool stopChanged = frame.StopInstance != state.StopInstance;
                Quaternion parentInverse = Quaternion.Inverse(handle.GetRotation(stream) * Quaternion.Inverse(reference));
                Vector3 stopPeak = Vector3.zero;
                if (stopping)
                {
                    state.Lean = state.LeanVelocity = Vector3.zero; // 收步时清除减速倾斜，双臂随承重进度摆动。
                    if (!chest)
                    {
                        Vector3 armPeak = binding.Region == CharacterPoseRegion.LeftArm ? frame.StopLeftArm : frame.StopRightArm;
                        stopPeak = parentInverse * armPeak * (binding.Share * influence);
                    }
                }
                else
                {
                    Vector3 leanTarget = chest ? parentInverse * Vector3.ClampMagnitude(
                        frame.BrakeRotation * (binding.Share * influence), limit) : Vector3.zero;
                    Spring(ref state.Lean, ref state.LeanVelocity, leanTarget, seconds, dt);
                }
                Quaternion target = Rotation(state.Lean) * reference;
                bool handoff = binding.SharedTransition == 0 && (frame.Revision != state.Revision || stopChanged);
                if (stopChanged && state.StopInstance != 0)
                    state.ExitAllowance = Mathf.Max(state.ExitAllowance, state.LastStopAngle);
                else
                    state.ExitAllowance *= Mathf.Exp(-4f * dt / Mathf.Max(0.01f, seconds));
                float carryLimit = limit + state.ExitAllowance;
                float load = stopPeak.sqrMagnitude > 0f ? frame.StopLoad : 0f;
                if (handoff)
                {
                    state.PeakLoad = 0f;
                    // 同次收步换上身动作时从摆臂前的姿态交接，退出收步时保留完整姿态和余势。
                    bool sameStop = stopping && !stopChanged;
                    state.HandoffVelocity = sameStop ? state.BaseVelocity : state.Velocity;
                    Quaternion outgoing = Rotation(state.HandoffVelocity * dt) * (sameStop ? state.LastBase : state.LastOutput);
                    state.Offset = Vector3.ClampMagnitude(Difference(outgoing, target), carryLimit);
                    state.OffsetVelocity = Vector3.zero;
                    state.AwaitIncomingVelocity = (byte)(influence > 0f ? 1 : 0);
                    state.Revision = frame.Revision;
                }
                else
                {
                    if (state.AwaitIncomingVelocity != 0)
                    {
                        // 切换帧跨越两段素材，后一采样才有新参考姿态的真实角速度。
                        state.OffsetVelocity = (state.HandoffVelocity - Difference(target, state.LastTarget) / dt) *
                            (influence * (1f - state.PeakLoad));
                        state.AwaitIncomingVelocity = 0;
                    }
                    Spring(ref state.Offset, ref state.OffsetVelocity, Vector3.zero, seconds, dt);
                    state.Offset = Vector3.ClampMagnitude(state.Offset, carryLimit);
                }
                if (load > state.PeakLoad) // 只在承重超过旧峰值时衰减，卸重时保留衰减结果。
                {
                    float remaining = (1f - load) / (1f - state.PeakLoad);
                    state.Offset *= remaining;
                    state.OffsetVelocity *= remaining;
                    state.PeakLoad = load;
                }
                Vector3 baseDelta = Vector3.ClampMagnitude(Difference(Rotation(state.Offset) * target, reference), carryLimit);
                Quaternion baseRotation = Rotation(baseDelta);
                Quaternion baseOutput = influence > 0f ? baseRotation * reference : reference;
                Quaternion output = baseOutput;
                if (load > 0f)
                {
                    Quaternion applied = Rotation(stopPeak * load) * baseRotation;
                    float totalLimit = Mathf.Max(carryLimit, stopPeak.magnitude);
                    Vector3 correction = Vector3.ClampMagnitude(Difference(applied, Quaternion.identity), totalLimit);
                    output = Rotation(correction) * reference;
                }
                if (binding.SharedTransition != 0 && !chest)
                    output = state.StopSwing.Apply(baseOutput, output, stopChanged && state.StopInstance != 0,
                        state.RebaseVelocity != 0, influence, seconds, dt);
                if (influence > 0f)
                    handle.SetLocalRotation(stream, output);
                if (!handoff && state.RebaseVelocity == 0) // 忽略限幅截断的姿态跳变，避免误算挥臂速度。
                {
                    state.Velocity = Difference(output, state.LastOutput) / dt;
                    state.BaseVelocity = Difference(baseOutput, state.LastBase) / dt;
                }
                state.RebaseVelocity = 0;
                state.LastBase = baseOutput;
                state.LastOutput = output;
                state.LastTarget = target;
                state.LastStopAngle = stopPeak.magnitude * load;
                state.StopInstance = frame.StopInstance;
                history[i] = state;
            }
        }

        // 临界阻尼解析解，约在响应时间内消除 91% 的静止偏差。
        internal static void Spring(ref Vector3 value, ref Vector3 velocity, Vector3 goal, float seconds, float dt)
        {
            float rate = 4f / Mathf.Max(0.01f, seconds);
            float decay = Mathf.Exp(-rate * dt);
            Vector3 error = value - goal;
            Vector3 slope = velocity + rate * error;
            value = goal + (error + dt * slope) * decay;
            velocity = (velocity - dt * rate * slope) * decay;
        }

        // 对四元数使用最短弧，避免 q 与 -q 的等价表示制造整圈的伪角速度。
        internal static Vector3 Difference(Quaternion to, Quaternion from)
        {
            Quaternion delta = (to * Quaternion.Inverse(from)).normalized;
            Vector3 axis = new Vector3(delta.x, delta.y, delta.z);
            if (delta.w < 0f) axis = -axis;
            float length = axis.magnitude;
            return length < 0.000001f ? axis * (2f * Mathf.Rad2Deg) :
                axis * (2f * Mathf.Rad2Deg * Mathf.Atan2(length, Mathf.Abs(delta.w)) / length);
        }

        // 将父空间角位移还原为最短弧旋转。
        internal static Quaternion Rotation(Vector3 degrees)
        {
            float angle = degrees.magnitude;
            return angle < 0.000001f ? Quaternion.identity : Quaternion.AngleAxis(angle, degrees / angle);
        }
    }
}
