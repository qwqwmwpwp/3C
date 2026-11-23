using System;
using UnityEditor;
using UnityEditor.Overlays;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ThreeC.CharacterBehavior
{
    [InitializeOnLoad]
    internal static class CharacterForecastSceneView
    {
        private static readonly CharacterForecastTrail trail = new CharacterForecastTrail();
        private static readonly CharacterForecastRehearsal rehearsal = new CharacterForecastRehearsal();
        private static CharacterBehaviorDriver driver;
        private static CharacterBehaviorAnimator binding;
        private static Animator model;
        private static CharacterForecastGhost ghost;
        private static bool dirty = true, transitioning;
        private static Matrix4x4 lastTransform;
        private static double nextPaint;
        private static ForecastScenario scenario;
        private static float scrub = -1f;

        static CharacterForecastSceneView()
        {
            SceneView.duringSceneGui += Draw;
            Selection.selectionChanged += SelectOwner;
            Undo.undoRedoPerformed += RefreshSetup;
            Undo.postprocessModifications += OnModified;
            EditorApplication.projectChanged += RefreshSetup;
            EditorApplication.hierarchyChanged += HierarchyChanged;
            EditorApplication.update += Update;
            EditorApplication.playModeStateChanged += ModeChanged;
            EditorSceneManager.sceneClosing += SceneClosing;
            AssemblyReloadEvents.beforeAssemblyReload += Release;
            EditorApplication.quitting += Release;
            EditorApplication.delayCall += SelectOwner;
        }

        internal static bool ShowRehearsal => !Application.isPlaying && driver && driver.ForecastSettings.Enabled && !transitioning;
        internal static float Horizon => driver ? Mathf.Max(0f, driver.Overrides.ForecastHorizon.Resolve(driver.ForecastSettings.Horizon)) : 0f;
        internal static ForecastScenario Scenario
        {
            get => scenario;
            set
            {
                if (value == scenario) return;
                scenario = value;
                dirty = true;
                SceneView.RepaintAll();
            }
        }
        internal static float Scrub
        {
            get => scrub < 0f ? Horizon : Mathf.Min(scrub, Horizon);
            set
            {
                scrub = value;
                trail.Reveal(EditorApplication.timeSinceStartup);
                SceneView.RepaintAll();
            }
        }

        private static void ModeChanged(PlayModeStateChange mode)
        {
            transitioning = mode == PlayModeStateChange.ExitingEditMode || mode == PlayModeStateChange.ExitingPlayMode;
            Release();
            if (!transitioning) SelectOwner();
        }

        private static void SceneClosing(Scene closing, bool removing)
        {
            if (driver && driver.gameObject.scene == closing) Release();
        }

        private static void Release()
        {
            trail.Dispose();
            ghost?.Dispose();
            ghost = null;
            model = null;
            binding = null;
            driver = null;
            scrub = -1f;
            dirty = true;
        }

        private static void SelectOwner()
        {
            if (transitioning) return;
            var selected = Selection.activeGameObject;
            var next = selected && !EditorUtility.IsPersistent(selected) ? selected.GetComponentInParent<CharacterBehaviorDriver>(true) : null;
            if (ReferenceEquals(next, driver)) return;
            Release();
            driver = next;
            if (next) binding = next.GetComponent<CharacterBehaviorAnimator>();
            SceneView.RepaintAll();
        }

        private static void HierarchyChanged()
        {
            if (!Application.isPlaying) dirty = true;
            SelectOwner();
        }

        private static UndoPropertyModification[] OnModified(UndoPropertyModification[] changes)
        {
            if (!Application.isPlaying && driver)
            {
                dirty = true; // 包括被拖动的墙、坡面及台阶。
                foreach (var change in changes)
                {
                    var target = change.currentValue?.target;
                    if (target is CharacterProfile || target is CharacterBehaviorAnimator || target is CharacterBehaviorDriver ||
                        target is Renderer || target is Animator || target is RuntimeAnimatorController) { RefreshSetup(); break; }
                }
            }
            return changes;
        }

        private static void RefreshSetup()
        {
            dirty = true;
            ghost?.Dispose();
            ghost = null;
            if (driver) SceneView.RepaintAll();
        }

        private static void Update()
        {
            if (transitioning) return;
            if (!driver)
            {
                if (!ReferenceEquals(driver, null)) Release();
                return;
            }
            double clock = EditorApplication.timeSinceStartup;
            if (SceneView.sceneViews.Count == 0)
            {
                ghost?.Dispose();
                ghost = null;
                trail.Dispose();
                return;
            }
            if (Application.isPlaying) trail.Bind(driver, TeleportDistance());
            if (!driver.ForecastSettings.Enabled || clock < nextPaint) return;
            nextPaint = clock + 1d / 30d;
            if (EditorApplication.isPaused && Application.isPlaying) return;
            foreach (SceneView view in SceneView.sceneViews)
                if (view && view.drawGizmos) view.Repaint();
        }

        private static float TeleportDistance() => driver.Profile ? driver.Profile.Presentation.ikTeleportDistance : 2f;

        private static void Draw(SceneView view)
        {
            if (Event.current.type != EventType.Repaint || !view.drawGizmos || transitioning || !driver ||
                !driver.isActiveAndEnabled || !driver.ForecastSettings.Enabled ||
                !StageUtility.GetCurrentStageHandle().Contains(driver.gameObject)) return;
            bool live = Application.isPlaying;
            double clock = live ? Time.timeAsDouble : EditorApplication.timeSinceStartup;
            double now = live ? clock : 0d;
            CharacterMovementForecast path;
            if (live)
            {
                path = driver.MovementForecast;
                trail.Bind(driver, TeleportDistance());
            }
            else
            {
                var world = driver.transform.localToWorldMatrix;
                if (dirty || world != lastTransform)
                {
                    dirty = false;
                    lastTransform = world;
                    rehearsal.Build(driver, scenario);
                    trail.Reset();
                    trail.CompareRehearsal(rehearsal.Before, rehearsal.After, now, clock);
                }
                path = rehearsal.After;
            }
            double end = path == null ? now : CharacterForecastTrail.End(path, now);
            if (!live) end = Math.Min(end, Scrub);
            trail.Draw(path, now, end, clock, live ? double.PositiveInfinity : Scrub,
                live ? driver.transform.position : (Vector3?)null);
            if (path == null || end < now || !path.TrySample(end, 0f, out var endpoint)) return;
            if (!binding) binding = driver.GetComponent<CharacterBehaviorAnimator>();
            var currentModel = binding ? binding.PreviewAnimator : null;
            if (model != currentModel) { ghost?.Dispose(); ghost = null; model = currentModel; }
            if (!model) return;
            ghost ??= new CharacterForecastGhost(driver, binding);
            ghost.Draw(endpoint, clock);
        }
    }

    [Overlay(typeof(SceneView), "移动预测预演", true)]
    internal sealed class CharacterForecastOverlay : IMGUIOverlay, ITransientOverlay
    {
        private static readonly string[] choices = { "跑步松键", "前冲后右转" };
        public bool visible => CharacterForecastSceneView.ShowRehearsal;
        public override void OnGUI()
        {
            CharacterForecastSceneView.Scenario = (ForecastScenario)EditorGUILayout.Popup(
                (int)CharacterForecastSceneView.Scenario, choices, GUILayout.MinWidth(190f));
            EditorGUI.BeginChangeCheck();
            float t = EditorGUILayout.Slider(new GUIContent("终点（秒）"), CharacterForecastSceneView.Scrub,
                0f, CharacterForecastSceneView.Horizon);
            if (EditorGUI.EndChangeCheck()) CharacterForecastSceneView.Scrub = t;
        }
    }
}
