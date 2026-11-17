using UnityEngine;

namespace ThreeC.AnimationCamera
{
    /// <summary>Small playback panel for the supplied sample scene.</summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("3C/Animation Camera Demo Controls")]
    public sealed class AnimationCameraDemoControls : MonoBehaviour
    {
        public AnimationCameraPlayer player;
        private Vector2 scrollPosition;

        private void OnGUI()
        {
            float width = Mathf.Min(360f, Mathf.Max(1f, Screen.width - 16f));
            float height = Mathf.Min(205f, Mathf.Max(1f, Screen.height - 16f));
            GUILayout.BeginArea(new Rect(8f, 8f, width, height), GUI.skin.box);
            scrollPosition = GUILayout.BeginScrollView(scrollPosition);
            GUILayout.Label("Animation Camera - 12 second demo");
            if (player == null || player.timeline == null)
            {
                GUILayout.Label("Assign a player and timeline in the Inspector.");
                GUILayout.EndScrollView();
                GUILayout.EndArea();
                return;
            }

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Play")) player.Play();
            if (GUILayout.Button("Pause")) player.Pause();
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Replay")) player.Replay();
            if (GUILayout.Button("Stop")) player.Stop();
            GUILayout.EndHorizontal();
            player.loop = GUILayout.Toggle(player.loop, "Loop");
            float duration = Mathf.Max(0f, player.timeline.duration);
            GUILayout.Label(string.Format("{0:0.00} / {1:0.00} s  [{2}]", player.CurrentTime,
                duration, player.IsPlaying ? "Playing" : "Paused / Stopped"));
            float seekTime = GUILayout.HorizontalSlider(player.CurrentTime, 0f, duration);
            if (Mathf.Abs(seekTime - player.CurrentTime) > 0.001f)
                player.Seek(seekTime);
            GUILayout.Label("0-3 Push | 3-6 Orbit | 7 Cut | 7-10 Close | 10-12 Roll");
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }
    }
}
