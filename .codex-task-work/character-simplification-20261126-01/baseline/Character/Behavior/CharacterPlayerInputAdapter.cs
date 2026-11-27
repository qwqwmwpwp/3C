using System;
using ThreeC.Player;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>将角色当前步态和已注册能力传给输入采集。</summary>
    internal readonly struct CharacterInputCaptureSettings
    {
        internal readonly CharacterMoveGait DefaultGait;
        internal readonly bool AllowGuard;

        // 保存采集玩家输入所需的角色配置。
        internal CharacterInputCaptureSettings(CharacterMoveGait defaultGait, bool allowGuard)
        {
            DefaultGait = defaultGait;
            AllowGuard = allowGuard;
        }
    }

    /// <summary>采集角色玩家输入，并处理按下时机与预测失效之间的交接。</summary>
    internal sealed class CharacterPlayerInputAdapter
    {
        private readonly CharacterInputBlackboard input;
        private readonly Func<bool> canCapture;
        private readonly Action<ForecastRefreshReason, bool> requestRefresh;

        // 绑定输入状态及模拟提供的有效性和预测通知。
        internal CharacterPlayerInputAdapter(CharacterInputBlackboard input, Func<bool> canCapture,
            Action<ForecastRefreshReason, bool> requestRefresh)
        {
            this.input = input;
            this.canCapture = canCapture;
            this.requestRefresh = requestRefresh;
        }

        // 开始接收玩家输入中需要立即处理的按下事件。
        internal void Bind()
        {
            PlayerInputSystem.AttackPressed += RecordAttackPress;
            PlayerInputSystem.DashPressed += ObserveDashPress;
        }

        // 停止接收玩家按下事件。
        internal void Unbind()
        {
            PlayerInputSystem.AttackPressed -= RecordAttackPress;
            PlayerInputSystem.DashPressed -= ObserveDashPress;
        }

        // 清空失效输入并记录边界时的冲刺编号，防止恢复后补发旧按下。
        internal static void EndContinuity(CharacterInputBlackboard input, bool receivePlayerInput) =>
            input.ClearForPlayerInput(receivePlayerInput, PlayerInputSystem.LatestDashPressId);

        // 读取本帧玩家输入，返回本次采集是否触发了预测通知。
        internal bool Capture(bool receivePlayerInput, (Vector3 forward, Vector3 right)? basis,
            in CharacterInputCaptureSettings settings) =>
            CaptureSample(receivePlayerInput ? PlayerInputSystem.Read() : default, basis, settings);

        // 聚合原始输入；只有新采集的冲刺请求才标记预测重建。
        internal bool CaptureSample(PlayerInputSample sample, (Vector3 forward, Vector3 right)? basis,
            in CharacterInputCaptureSettings settings)
        {
            if (!input.Collect(sample, basis, settings.DefaultGait, settings.AllowGuard))
                return false;
            requestRefresh(ForecastRefreshReason.DashPressed, true);
            return true;
        }

        // 检查当前模拟和上一轮采集是否都允许接收玩家输入。
        private bool AcceptsPresses() => input.WasReceivingPlayerInput && canCapture();

        // 先记录攻击按下，再使预测立即失效。
        private void RecordAttackPress(AttackButton button, double time)
        {
            if (!AcceptsPresses()) return;
            input.AddPress(new CharacterInputPress(time, button));
            requestRefresh(ForecastRefreshReason.AttackPressed, true);
        }

        // 回调与采集共用冲刺编号，只记录每次按下的首个观测。
        private void ObserveDashPress(ulong id, double time)
        {
            if (AcceptsPresses() && input.PressDash(id, time)) requestRefresh(ForecastRefreshReason.DashPressed, true);
        }
    }
}
