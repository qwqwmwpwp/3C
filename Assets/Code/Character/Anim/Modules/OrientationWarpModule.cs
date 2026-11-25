using System;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 定义循环步态的水平朝向校正和上身反向补偿。
    [Serializable]
    internal sealed class OrientationWarpTuning
    {
        [InspectorName("最大校正角（度）"), Range(0f, 45f)] public float MaxAngle = 25f;
        [InspectorName("方向死区（度）"), Range(0f, 15f)] public float DeadZone = 3f;
        [InspectorName("响应时间（秒）"), Min(0.01f)] public float ResponseSeconds = 0.12f;
        [InspectorName("开始校正速度（米/秒）"), Min(0f)] public float MinimumSpeed = 0.15f;
        [InspectorName("完全校正速度（米/秒）"), Min(0.01f)] public float FullSpeed = 1f;
        [InspectorName("上身反向补偿"), Range(0f, 1f)] public float SpineCompensation = 1f;
    }

    // 为角色安装独立的步态朝向校正。
    [CreateAssetMenu(menuName = "3C/动画模块/步态朝向校正", fileName = "步态朝向校正")]
    public sealed class OrientationWarpModule : AnimationModule
    {
        [SerializeField, InspectorName("朝向校正调校")] internal OrientationWarpTuning Tuning = new OrientationWarpTuning();

        // 向动画组合登记朝向校正能力。
        internal override void Include(AnimationModuleAssembly assembly) => assembly.Add(this);
    }
}
