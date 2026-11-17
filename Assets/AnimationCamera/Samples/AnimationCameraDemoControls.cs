using UnityEngine;

namespace ThreeC.AnimationCamera
{
    /// <summary>示例场景的播放控制面板，提供播放、暂停、重播、停止、循环和时间定位操作。</summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("3C/镜头演示控制面板")]
    public sealed class AnimationCameraDemoControls : MonoBehaviour
    {
        [Tooltip("演示面板控制的镜头播放器，须已配置时间轴资产。")]
        public AnimationCameraPlayer player;
        private Vector2 scrollPosition;

        private void OnGUI()
        {
            float width = Mathf.Min(360f, Mathf.Max(1f, Screen.width - 16f));
            float height = Mathf.Min(205f, Mathf.Max(1f, Screen.height - 16f));
            GUILayout.BeginArea(new Rect(8f, 8f, width, height), GUI.skin.box);
            scrollPosition = GUILayout.BeginScrollView(scrollPosition);
            GUILayout.Label("镜头动画 · 12 秒演示");
            if (player == null || player.timeline == null)
            {
                GUILayout.Label("请在 Inspector 中指定播放器及其时间轴。");
                GUILayout.EndScrollView();
                GUILayout.EndArea();
                return;
            }

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("播放")) player.Play();
            if (GUILayout.Button("暂停")) player.Pause();
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("重播")) player.Replay();
            if (GUILayout.Button("停止")) player.Stop();
            GUILayout.EndHorizontal();
            player.loop = GUILayout.Toggle(player.loop, "循环播放");
            float duration = Mathf.Max(0f, player.timeline.duration);
            GUILayout.Label(string.Format("{0:0.00} / {1:0.00} 秒  [{2}]", player.CurrentTime,
                duration, player.IsPlaying ? "播放中" : "已暂停或停止"));
            float seekTime = GUILayout.HorizontalSlider(player.CurrentTime, 0f, duration);
            if (Mathf.Abs(seekTime - player.CurrentTime) > 0.001f)
                player.Seek(seekTime);
            GUILayout.Label("0～3 秒推进 | 3～6 秒环绕 | 7 秒切镜 | 7～10 秒特写 | 10～12 秒翻滚");
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }
    }
}
