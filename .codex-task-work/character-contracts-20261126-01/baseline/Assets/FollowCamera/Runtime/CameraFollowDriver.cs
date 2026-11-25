using ThreeC.FollowCamera.Core;
using UnityEngine;

namespace ThreeC.FollowCamera
{
    /// <summary>将渲染帧的操作方向发布与最终相机姿态提交组织为两个阶段。</summary>
    [DefaultExecutionOrder(-100)]
    [DisallowMultipleComponent]
    [AddComponentMenu("3C/跟随相机/跟随相机驱动器")]
    public sealed class CameraFollowDriver : MonoBehaviour
    {
        [Header("输入与输出")]
        [SerializeField, Tooltip("采集鼠标轨道控制及回正按键。")]
        private CameraControlInput controlInput;
        [SerializeField, Tooltip("提供角色面向、跟随焦点及连续性信息。")]
        private CameraTargetInput targetInput;
        [SerializeField, Tooltip("在 LateUpdate 阶段提供受遮挡限制的距离。")]
        private CameraObstructionInput obstructionInput;
        [SerializeField, Tooltip("供角色在同一渲染帧读取的水平操作方向。")]
        private CameraControlOutput controlOutput;
        [SerializeField, Tooltip("向玩法相机提交位置、旋转和视野角。")]
        private CameraPoseOutput poseOutput;

        [Header("自由轨道")]
        [SerializeField, Range(-89f, 89f), Tooltip("初始化时相机向下看的角度，单位为度。")]
        private float initialPitch = 15f;
        [SerializeField, Range(-89f, 89f), Tooltip("允许的最低俯角，单位为度。负值时相机向上看。")]
        private float minimumPitch = -25f;
        [SerializeField, Range(-89f, 89f), Tooltip("允许的最高俯角，单位为度。")]
        private float maximumPitch = 70f;
        [SerializeField, Min(0f), Tooltip("无环境遮挡时，机位到观察焦点的距离，单位为米。")]
        private float desiredDistance = 4f;
        [SerializeField, Min(1f), Tooltip("手动回正的最大速度，单位为度每秒。")]
        private float recenterSpeed = 360f;
        [SerializeField, Min(0.01f), Tooltip("遮挡解除后的距离恢复率，单位为每秒。")]
        private float obstructionRecoverySpeed = 10f;

        private readonly OrbitCameraSolver solver = new OrbitCameraSolver();
        private bool hasOrbit;
        private int targetVersion;
        private int cutVersion;

        private bool PortsAvailable => controlInput != null && targetInput != null
            && obstructionInput != null && obstructionInput.IsAvailable && controlOutput != null
            && controlOutput.isActiveAndEnabled && poseOutput != null && poseOutput.IsAvailable;

        private void Update()
        {
            if (!PortsAvailable || !targetInput.TryRead(false, out CameraTargetSample target))
            {
                ResetRuntime();
                return;
            }

            if (!controlInput.TryRead(out Vector2 orbitDegrees, out bool recenterPressed, out bool resumed))
            {
                SuspendInput();
                return;
            }

            if (!hasOrbit || targetVersion != target.TargetVersion)
            {
                solver.Initialize(target.Facing, initialPitch, minimumPitch, maximumPitch);
                targetVersion = target.TargetVersion;
                hasOrbit = true;
            }

            ApplyCut(target.CutVersion);
            if (resumed)
                solver.CancelRecenter();
            solver.StepOrbit(orbitDegrees, recenterPressed, target.Facing, minimumPitch, maximumPitch,
                recenterSpeed, Time.deltaTime);
            solver.GetControlBasis(out Vector3 forward, out Vector3 right);
            controlOutput.Publish(forward, right, controlInput); // 在角色读取本帧移动输入前发布相机方向。
        }

        private void LateUpdate()
        {
            if (!hasOrbit)
                return;

            if (!PortsAvailable || !targetInput.TryRead(true, out CameraTargetSample target)
                || targetVersion != target.TargetVersion) // 目标已切换时，等下一帧发布新方向。
            {
                ResetRuntime();
                return;
            }

            if (!controlOutput.HasCurrentBasis)
            {
                SuspendInput();
                return;
            }

            ApplyCut(target.CutVersion);
            solver.GetView(out Quaternion rotation, out Vector3 boomDirection);
            float availableDistance = obstructionInput.GetAvailableDistance(target.Pivot, boomDirection,
                Mathf.Max(0f, desiredDistance), targetInput);
            Vector3 position = solver.ResolvePosition(target.Pivot, boomDirection, availableDistance,
                obstructionRecoverySpeed, Time.deltaTime);
            poseOutput.Apply(position, rotation);
        }

        private void ApplyCut(int currentCutVersion)
        {
            if (cutVersion == currentCutVersion)
                return;

            solver.CutFollow();
            cutVersion = currentCutVersion;
        }

        private void OnDisable() => ResetRuntime();

        private void ResetRuntime()
        {
            hasOrbit = false;
            SuspendInput();
        }

        private void SuspendInput()
        {
            solver.CancelRecenter();
            if (controlOutput != null)
                controlOutput.Invalidate();
            if (controlInput != null)
                controlInput.DiscardNextSample();
        }
    }
}
