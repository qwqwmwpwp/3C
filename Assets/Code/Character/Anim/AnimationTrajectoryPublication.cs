using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 保存冻结轨迹上的世界空间姿态、速度和该时刻可观察的移动交还状态。
    public struct AnimationTrajectorySample
    {
        public Vector3 Position, Velocity;
        public Quaternion Rotation;
        public AnimationGait Gait;
        public bool ExternallyControlled, AnticipatesMovementReturn, IsLocomotion;
    }

    // 发布后须保持不可变并允许后台只读查询。时间为来源模拟时钟秒，超出可信范围返回 false。
    public interface IAnimationTrajectorySnapshot
    {
        long Version { get; }
        bool Available { get; }

        // 在指定时刻及非负前瞻时间读取插值样本。
        bool TrySample(double now, float secondsAhead, out AnimationTrajectorySample sample);

        // 查找已知的最早移动交还时刻；未提供交还信息的来源返回 false。
        bool TryReturnToMovement(double now, out float secondsAhead, out AnimationTrajectorySample sample);
    }

    // 冻结本次求解的运动参数，用于在关闭发布预测时按不同动作库的窗口重新投影。
    public interface IAnimationGroundProjection
    {
        // 两个窗口均以本固定步结束时刻为起点，单位为秒。
        CharacterGroundTrajectory Project(long frameNumber, Vector2 windows);
    }

    // 区分关闭预测、启用后暂时无可信样本，以及已发布的同一份不可变快照。
    public readonly struct AnimationTrajectoryPublication
    {
        public readonly bool Enabled;
        public readonly IAnimationTrajectorySnapshot Snapshot;

        // 启用后可用空快照撤回上一批结果；关闭后恢复运动步自身的地面投影。
        public AnimationTrajectoryPublication(bool enabled, IAnimationTrajectorySnapshot snapshot = null)
        {
            Enabled = enabled;
            Snapshot = enabled ? snapshot : null;
        }
    }
}
