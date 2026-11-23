using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ThreeC.AnimationCamera.Editor
{
    /// <summary>导入已导出的角色，并基于演出模板创建包含角色与动作的独立镜头测试场景。</summary>
    public sealed class UmaCharacterTestWindow : EditorWindow
    {
        private const string CharacterArtFolder = "Assets/Art/AnimCamera";
        private const string TemplateScene = "Assets/Scenes/动画相机测试.unity";
        private const string DefaultTimeline = "Assets/Data/AnimCamera/示例镜头时间轴.asset";
        private const string TestScenePath = "Assets/Scenes/Uma角色镜头测试.unity";

        [SerializeField] private GameObject characterPrefab;
        [SerializeField] private AnimationClip[] clips = Array.Empty<AnimationClip>();
        [SerializeField] private CameraTimelineAsset cameraTimeline;
        [SerializeField] private string lookAtPath = "";
        [SerializeField] private bool loopClip = true;
        [SerializeField] private bool inPlace;
        [SerializeField] private string motionRootPath = "";
        private SerializedObject serializedWindow;
        private Vector2 scroll;
        private bool validationDirty = true;
        private string validationError;
        private string validationSummary;
        private string result;

        [MenuItem("Tools/3C/Uma 角色测试")]
        private static void Open()
        {
            GetWindow<UmaCharacterTestWindow>("Uma 角色测试");
        }

        private void OnEnable()
        {
            minSize = new Vector2(430f, 390f);
            serializedWindow = new SerializedObject(this);
            if (cameraTimeline == null)
                cameraTimeline = AssetDatabase.LoadAssetAtPath<CameraTimelineAsset>(DefaultTimeline);
            FindImportedCharacter();
            validationDirty = true;
        }

        private void OnProjectChange()
        {
            if (characterPrefab == null) FindImportedCharacter();
            validationDirty = true;
            Repaint();
        }

        private void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.HelpBox("先在 UmaViewer 加载真实角色与身体动作，再导出角色 unitypackage。这里会复制已有演出场景，保留原场景和已打开场景的未保存修改。", MessageType.Info);
            bool busy = EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating;
            using (new EditorGUI.DisabledScope(busy))
            {
                if (GUILayout.Button("导入角色 unitypackage…"))
                {
                    string package = EditorUtility.OpenFilePanel("选择 UmaViewer 导出的角色", "", "unitypackage");
                    if (!string.IsNullOrEmpty(package)) AssetDatabase.ImportPackage(package, true);
                }

                EditorGUI.BeginChangeCheck();
                GameObject nextPrefab = (GameObject)EditorGUILayout.ObjectField(new GUIContent("Character.prefab", "选择已导入的真实角色 prefab 资产，不能使用场景实例。"), characterPrefab, typeof(GameObject), false);
                if (nextPrefab != characterPrefab)
                {
                    characterPrefab = nextPrefab;
                    clips = CollectClips(characterPrefab);
                    lookAtPath = "";
                    motionRootPath = "";
                }

                using (new EditorGUI.DisabledScope(characterPrefab == null))
                    if (GUILayout.Button("从角色同目录的 Animations 收集动作"))
                    {
                        clips = CollectClips(characterPrefab);
                        validationDirty = true;
                    }

                serializedWindow.Update();
                EditorGUILayout.PropertyField(serializedWindow.FindProperty("clips"), new GUIContent("身体动作 Clips", "首个动作作为初始动作；生成场景后可在播放组件中切换。"), true);
                if (serializedWindow.ApplyModifiedProperties()) validationDirty = true;
                cameraTimeline = (CameraTimelineAsset)EditorGUILayout.ObjectField("镜头时间轴", cameraTimeline, typeof(CameraTimelineAsset), false);
                lookAtPath = EditorGUILayout.TextField(new GUIContent("注视骨骼路径", "相对 Character 根的路径；留空按 Chest/Spine2/Head 名称查找，找不到时使用网格包围盒中心参考点。"), lookAtPath);
                loopClip = EditorGUILayout.Toggle("循环身体动作", loopClip);
                inPlace = EditorGUILayout.Toggle(new GUIContent("原地动作", "默认关闭，保留源动作完整位移。开启后仅锁水平位移，保留高度和旋转。"), inPlace);
                if (inPlace)
                    motionRootPath = EditorGUILayout.TextField(new GUIContent("运动根节点路径", "可选 Hip/运动骨骼的相对路径。留空仅锁角色根节点的水平位移。"), motionRootPath);
                if (EditorGUI.EndChangeCheck())
                {
                    validationDirty = true;
                    result = null;
                }

                if (validationDirty)
                {
                    validationError = ValidateInputs(out validationSummary);
                    validationDirty = false;
                }
                EditorGUILayout.HelpBox(validationError ?? validationSummary, validationError != null ? MessageType.Warning : MessageType.Info);
                using (new EditorGUI.DisabledScope(validationError != null))
                    if (GUILayout.Button("创建测试场景")) CreateTestScene();
            }

            if (busy) EditorGUILayout.HelpBox("请在编辑模式且脚本编译、资源导入完成后操作。", MessageType.Info);
            if (!string.IsNullOrEmpty(result)) EditorGUILayout.HelpBox(result, MessageType.Info);
            EditorGUILayout.EndScrollView();
        }

        private void FindImportedCharacter()
        {
            if (!AssetDatabase.IsValidFolder(CharacterArtFolder)) return;
            string[] candidates = AssetDatabase.FindAssets("Character t:Prefab", new[] { CharacterArtFolder });
            GameObject found = null;
            for (int i = 0; i < candidates.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(candidates[i]);
                if (!string.Equals(Path.GetFileName(path), "Character.prefab", StringComparison.OrdinalIgnoreCase)) continue;
                if (found != null) return; // 存在多个角色时，由用户明确选择目标。
                found = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            }
            if (found == null) return;
            characterPrefab = found;
            clips = CollectClips(found);
        }

        private static AnimationClip[] CollectClips(GameObject prefab)
        {
            if (prefab == null) return Array.Empty<AnimationClip>();
            string directory = Path.GetDirectoryName(AssetDatabase.GetAssetPath(prefab))?.Replace('\\', '/');
            if (string.IsNullOrEmpty(directory)) return Array.Empty<AnimationClip>();
            string animationFolder = directory + "/Animations";
            if (!AssetDatabase.IsValidFolder(animationFolder)) return Array.Empty<AnimationClip>();
            string[] guids = AssetDatabase.FindAssets("t:AnimationClip", new[] { animationFolder });
            List<AnimationClip> found = new List<AnimationClip>();
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (!path.EndsWith(".anim", StringComparison.OrdinalIgnoreCase)) continue;
                AnimationClip clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
                if (clip != null && !found.Contains(clip)) found.Add(clip);
            }
            found.Sort((left, right) => string.CompareOrdinal(AssetDatabase.GetAssetPath(left), AssetDatabase.GetAssetPath(right)));
            return found.ToArray();
        }

        private string ValidateInputs(out string summary)
        {
            summary = null;
            if (characterPrefab == null)
                return "尚未选择真实角色资源。请导入源项目导出的角色 unitypackage，再选择其中的 Character.prefab。";
            string assetPath = AssetDatabase.GetAssetPath(characterPrefab);
            if (!assetPath.StartsWith("Assets/", StringComparison.Ordinal) || !assetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)
                || !PrefabUtility.IsPartOfPrefabAsset(characterPrefab) || characterPrefab.transform.parent != null
                || AssetDatabase.LoadAssetAtPath<GameObject>(assetPath) != characterPrefab)
                return "请选择 Character.prefab 根资产，不能选择场景实例、FBX 或 prefab 子节点。";

            Component[] components = characterPrefab.GetComponentsInChildren<Component>(true);
            for (int i = 0; i < components.Length; i++)
            {
                Component component = components[i];
                if (component == null) return "角色 prefab 存在 Missing Script，请从源导出器重新导出。";
                if (!(component is Transform) && !(component is SkinnedMeshRenderer) && !(component is MeshRenderer)
                    && !(component is MeshFilter) && !(component is Animator) && !(component is Animation))
                    return "角色 prefab 含有不支持的组件 " + component.GetType().Name + "；请使用不带源脚本的原生角色导出。";
            }

            Transform root = characterPrefab.transform;
            Transform[] nodes = characterPrefab.GetComponentsInChildren<Transform>(true);
            Dictionary<string, Transform> paths = new Dictionary<string, Transform>(StringComparer.Ordinal);
            for (int i = 0; i < nodes.Length; i++)
            {
                string path = AnimationUtility.CalculateTransformPath(nodes[i], root);
                if (paths.ContainsKey(path) || (nodes[i] != root && nodes[i].name.Contains("/")))
                    return "角色层级存在重复或含斜杠的节点名，动作路径无法唯一匹配：" + path;
                paths.Add(path, nodes[i]);
            }

            SkinnedMeshRenderer[] skins = characterPrefab.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            HashSet<Transform> bones = new HashSet<Transform>();
            int validSkins = 0;
            for (int i = 0; i < skins.Length; i++)
            {
                Mesh mesh = skins[i].sharedMesh;
                if (mesh == null || mesh.vertexCount == 0) return "角色存在空蒙皮网格，请重新导出真实模型。";
                Transform[] skinBones = skins[i].bones;
                if (skinBones == null || skinBones.Length == 0 || mesh.bindposes.Length == 0)
                    return "蒙皮网格缺少骨骼或 bindposes：" + skins[i].name;
                if (mesh.bindposes.Length != skinBones.Length) return "蒙皮网格 bindposes 与骨骼数量不一致：" + skins[i].name;
                for (int bone = 0; bone < skinBones.Length; bone++)
                {
                    Transform node = skinBones[bone];
                    if (node == null || (node != root && !node.IsChildOf(root))) return "蒙皮网格含有空骨骼或角色外部骨骼：" + skins[i].name;
                    bones.Add(node);
                }
                if (skins[i].rootBone != null && skins[i].rootBone != root && !skins[i].rootBone.IsChildOf(root))
                    return "蒙皮根骨骼引用了角色层级之外的对象。";
                validSkins++;
            }
            if (validSkins == 0) return "角色必须包含实际 SkinnedMeshRenderer、非空网格和内部骨骼；不能使用占位模型。";
            if (clips == null || clips.Length == 0) return "请至少选择一个已导入的身体动作 .anim。";
            for (int i = 0; i < clips.Length; i++)
            {
                string clipError = ValidateClip(clips[i], paths, bones);
                if (clipError != null) return "动作 " + i + "：" + clipError;
            }

            if (!string.IsNullOrEmpty(lookAtPath) && !paths.ContainsKey(lookAtPath)) return "Look-at 骨骼路径不存在于所选 prefab：" + lookAtPath;
            if (inPlace && !string.IsNullOrEmpty(motionRootPath) && !paths.ContainsKey(motionRootPath)) return "Motion Root 路径不存在于所选 prefab：" + motionRootPath;
            if (cameraTimeline == null || !EditorUtility.IsPersistent(cameraTimeline)) return "请选择已保存的镜头时间轴资产。";
            if (!cameraTimeline.TryValidate(out string timelineError)) return "镜头时间轴无效：" + timelineError;
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(TemplateScene) == null) return "未找到用于复制的原演出场景：" + TemplateScene;

            Transform automaticTarget = FindPreferredLookAt(root);
            string chosen = !string.IsNullOrEmpty(lookAtPath) ? lookAtPath : automaticTarget != null
                ? AnimationUtility.CalculateTransformPath(automaticTarget, root) : "网格包围盒中心参考点（未找到 Chest/Head）";
            summary = validSkins + " 个真实蒙皮网格，" + bones.Count + " 根骨骼，" + clips.Length + " 个已匹配路径的身体动作。\nLook-at：" + chosen
                + "\n新场景将创建到 " + TestScenePath + "（重名时自动递增）。";
            return null;
        }

        private static string ValidateClip(AnimationClip clip, Dictionary<string, Transform> paths, HashSet<Transform> bones)
        {
            if (clip == null) return "未指定动作。";
            string path = AssetDatabase.GetAssetPath(clip);
            if (!path.EndsWith(".anim", StringComparison.OrdinalIgnoreCase) || !EditorUtility.IsPersistent(clip)) return "请选择已导入的 .anim 资产。";
            if (clip.isHumanMotion) return clip.name + " 是 Humanoid；需要原始 Transform 路径的 Generic 或 Legacy 动作。";
            if (float.IsNaN(clip.length) || float.IsInfinity(clip.length) || clip.length <= 0f) return clip.name + " 长度无效。";
            if (AnimationUtility.GetObjectReferenceCurveBindings(clip).Length != 0) return clip.name + " 含对象引用曲线，需要仅含 Transform/BlendShape 的身体动作。";
            EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings(clip);
            int boneCurves = 0;
            for (int i = 0; i < bindings.Length; i++)
            {
                EditorCurveBinding binding = bindings[i];
                if (!paths.TryGetValue(binding.path, out Transform node)) return clip.name + " 找不到绑定节点：" + binding.path;
                if (binding.type == typeof(Transform) && IsTransformProperty(binding.propertyName))
                {
                    if (bones.Contains(node)) boneCurves++;
                    continue;
                }
                if (binding.type == typeof(SkinnedMeshRenderer) && binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal))
                {
                    SkinnedMeshRenderer skin = node.GetComponent<SkinnedMeshRenderer>();
                    if (skin != null && skin.sharedMesh != null && skin.sharedMesh.GetBlendShapeIndex(binding.propertyName.Substring(11)) >= 0) continue;
                    return clip.name + " 找不到 BlendShape：" + binding.path + "/" + binding.propertyName;
                }
                return clip.name + " 含不支持的绑定：" + binding.type?.Name + " / " + binding.propertyName
                    + "。仅支持 Transform TRS 与 BlendShape；Animator 原生 root-motion 不能由此采样入口播放。";
            }
            return boneCurves > 0 ? null : clip.name + " 未包含匹配到实际蒙皮骨骼的 Transform 动画。";
        }

        private static bool IsTransformProperty(string property)
        {
            if (string.IsNullOrEmpty(property) || property.Length < 3 || property[property.Length - 2] != '.') return false;
            char axis = property[property.Length - 1];
            string prefix = property.Substring(0, property.Length - 2);
            if (prefix == "m_LocalRotation") return axis == 'x' || axis == 'y' || axis == 'z' || axis == 'w';
            return (axis == 'x' || axis == 'y' || axis == 'z') && (prefix == "m_LocalPosition" || prefix == "m_LocalScale"
                || prefix == "localEulerAnglesRaw" || prefix == "localEulerAnglesBaked" || prefix == "localEulerAngles");
        }

        private void CreateTestScene()
        {
            validationError = ValidateInputs(out validationSummary);
            if (validationError != null) return;
            string destination = AssetDatabase.GenerateUniqueAssetPath(TestScenePath);
            Scene previousActive = SceneManager.GetActiveScene();
            Scene created = default;
            bool copied = false;
            try
            {
                if (!AssetDatabase.CopyAsset(TemplateScene, destination)) throw new InvalidOperationException("无法复制原演出场景。");
                copied = true;
                created = EditorSceneManager.OpenScene(destination, OpenSceneMode.Additive);
                AnimationCameraPlayer cameraPlayer = FindSingleComponent<AnimationCameraPlayer>(created);
                Transform anchor = FindNamedTransform(created, "Performance Anchor");
                Transform oldRoot = FindNamedTransform(created, "UmaViewer Demo Model");
                if (cameraPlayer == null || anchor == null || oldRoot == null || anchor.IsChildOf(oldRoot))
                    throw new InvalidOperationException("原演出场景需要唯一 Camera Player、Performance Anchor 和 UmaViewer Demo Model；请先核对模板结构。");
                UmaViewerModelSample oldSample = oldRoot.GetComponentInChildren<UmaViewerModelSample>(true);
                Transform oldModel = oldSample != null && oldSample.modelRoot != null ? oldSample.modelRoot.transform : oldRoot;

                GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(characterPrefab, created);
                if (instance == null) throw new InvalidOperationException("无法实例化所选角色 prefab。");
                instance.transform.SetParent(anchor, false); // 保留导出根节点的局部位置、旋转、缩放及层级。
                UmaCharacterPlayback playback = instance.AddComponent<UmaCharacterPlayback>();
                playback.player = cameraPlayer;
                playback.modelRoot = instance;
                playback.clips = (AnimationClip[])clips.Clone();
                playback.selectedClip = 0;
                playback.loopClip = loopClip;
                playback.inPlace = inPlace;
                playback.motionRoot = inPlace && !string.IsNullOrEmpty(motionRootPath) ? instance.transform.Find(motionRootPath) : null;
                if (!playback.SampleAt(0f)) throw new InvalidOperationException("首个动作无法在真实角色上采样，未生成测试场景。");

                Transform lookAt = !string.IsNullOrEmpty(lookAtPath) ? instance.transform.Find(lookAtPath) : FindPreferredLookAt(instance.transform);
                if (lookAt == null)
                {
                    if (!TryGetBounds(instance, out Bounds bounds)) throw new InvalidOperationException("无法从真实模型计算注视参考点。");
                    GameObject reference = new GameObject("Uma Look-at (Mesh Bounds Center)");
                    SceneManager.MoveGameObjectToScene(reference, created);
                    reference.transform.SetParent(instance.transform, false);
                    reference.transform.position = bounds.center;
                    lookAt = reference.transform;
                }

                List<string> notices = new List<string>();
                RetargetBindings(cameraPlayer, oldRoot, oldModel, instance.transform, anchor, lookAt, notices);
                cameraPlayer.anchor = anchor;
                cameraPlayer.lookAtTarget = lookAt;
                cameraPlayer.timeline = cameraTimeline;
                ValidateNamedTargets(cameraPlayer);
                foreach (GameObject sceneRoot in created.GetRootGameObjects())
                    foreach (UmaViewerModelSample obsolete in sceneRoot.GetComponentsInChildren<UmaViewerModelSample>(true))
                        DestroyImmediate(obsolete);
                DestroyImmediate(oldRoot.gameObject);

                if (!playback.SampleAt(0f) || !cameraPlayer.TryEvaluateAt(0f, out _)) // 与运行时一致，先采角色再采镜头。
                    throw new InvalidOperationException("替换角色后镜头绑定无效，请核对命名目标与镜头轨道。");
                cameraPlayer.Seek(0f);
                string scaleNotice = GetScaleNotice(instance);
                if (!string.IsNullOrEmpty(scaleNotice)) notices.Add(scaleNotice);
                playback.RestoreModel(); // 保存场景前还原初始姿态，供后续切换动作时重置。
                EditorSceneManager.MarkSceneDirty(created);
                if (!EditorSceneManager.SaveScene(created, destination)) throw new InvalidOperationException("新场景保存失败。");
                if (previousActive.IsValid() && previousActive.isLoaded) SceneManager.SetActiveScene(previousActive);
                if (!EditorSceneManager.CloseScene(created, true)) throw new InvalidOperationException("新场景已保存，但未能关闭临时加载的副本。");
                SceneAsset sceneAsset = AssetDatabase.LoadAssetAtPath<SceneAsset>(destination);
                Selection.activeObject = sceneAsset;
                EditorGUIUtility.PingObject(sceneAsset);
                result = "已创建并单独保存 " + destination + "。原场景及其他已打开场景未保存、未关闭。\n双击选中的新场景资产，单独打开后再进入 Play 模式测试。"
                    + (notices.Count > 0 ? "\n" + string.Join("\n", notices) : "");
                Debug.Log("[UmaCharacterTest] " + result, sceneAsset);
            }
            catch (Exception exception)
            {
                // 失败时只清理本次副本，保留已打开场景的未保存修改。
                if (previousActive.IsValid() && previousActive.isLoaded) SceneManager.SetActiveScene(previousActive);
                if (created.IsValid() && created.isLoaded) EditorSceneManager.CloseScene(created, true);
                if (copied) AssetDatabase.DeleteAsset(destination);
                result = "创建失败：" + exception.Message;
                Debug.LogError("[UmaCharacterTest] " + result);
            }
        }

        private static T FindSingleComponent<T>(Scene scene) where T : Component
        {
            T found = null;
            foreach (GameObject root in scene.GetRootGameObjects())
                foreach (T candidate in root.GetComponentsInChildren<T>(true))
                {
                    if (found != null) return null;
                    found = candidate;
                }
            return found;
        }

        private static Transform FindNamedTransform(Scene scene, string name)
        {
            Transform found = null;
            foreach (GameObject root in scene.GetRootGameObjects())
                foreach (Transform candidate in root.GetComponentsInChildren<Transform>(true))
                    if (candidate.name == name)
                    {
                        if (found != null) return null;
                        found = candidate;
                    }
            return found;
        }

        private static Transform FindPreferredLookAt(Transform root)
        {
            // 从实际蒙皮骨骼中选择注视目标，避免误选名为 Head 的网格对象。
            List<Transform> nodes = new List<Transform>();
            foreach (SkinnedMeshRenderer skin in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                foreach (Transform bone in skin.bones)
                    if (bone != null && !nodes.Contains(bone)) nodes.Add(bone);
            string[] preferences = { "Chest", "UpperChest", "Spine2", "Spine_02", "Spine02", "Head" };
            for (int preference = 0; preference < preferences.Length; preference++)
                for (int i = 0; i < nodes.Count; i++)
                    if (string.Equals(nodes[i].name, preferences[preference], StringComparison.OrdinalIgnoreCase)) return nodes[i];
            for (int i = 0; i < nodes.Count; i++)
                if (nodes[i].name.IndexOf("chest", StringComparison.OrdinalIgnoreCase) >= 0) return nodes[i];
            for (int i = 0; i < nodes.Count; i++)
                if (nodes[i].name.IndexOf("head", StringComparison.OrdinalIgnoreCase) >= 0) return nodes[i];
            return null;
        }

        private static void RetargetBindings(AnimationCameraPlayer player, Transform oldRoot, Transform oldModel,
            Transform newRoot, Transform anchor, Transform lookAt, List<string> notices)
        {
            if (IsWithin(player.anchor, oldRoot)) player.anchor = anchor;
            if (IsWithin(player.lookAtTarget, oldRoot)) player.lookAtTarget = lookAt;
            if (player.targetBindings == null) return;
            Transform[] newNodes = newRoot.GetComponentsInChildren<Transform>(true);
            foreach (CameraTargetBinding binding in player.targetBindings)
            {
                if (binding == null || !IsWithin(binding.target, oldRoot)) continue;
                Transform previous = binding.target;
                Transform replacement = previous == oldRoot || previous == oldModel ? newRoot : null;
                if (replacement == null && IsWithin(previous, oldModel))
                    replacement = newRoot.Find(AnimationUtility.CalculateTransformPath(previous, oldModel));
                if (replacement == null)
                {
                    for (int i = 0; i < newNodes.Length; i++)
                    {
                        if (newNodes[i].name != previous.name) continue;
                        if (replacement != null) { replacement = null; break; }
                        replacement = newNodes[i];
                    }
                }
                if (replacement == null)
                {
                    replacement = lookAt;
                    notices.Add("命名目标 '" + binding.id + "' 未找到同路径/同名骨骼，已改为所选 Look-at；请在新场景检查该绑定。");
                }
                binding.target = replacement;
            }
        }

        private static bool IsWithin(Transform target, Transform root)
        {
            return target != null && (target == root || target.IsChildOf(root));
        }

        private static void ValidateNamedTargets(AnimationCameraPlayer player)
        {
            List<CameraVectorKey>[] tracks = { player.timeline.positionKeys, player.timeline.lookAtKeys };
            foreach (List<CameraVectorKey> track in tracks)
                foreach (CameraVectorKey key in track)
                {
                    if (key.reference != CameraVectorReference.Target) continue;
                    int matches = 0;
                    if (player.targetBindings != null)
                        foreach (CameraTargetBinding binding in player.targetBindings)
                            if (binding != null && binding.id == key.targetId && binding.target != null) matches++;
                    if (matches != 1) throw new InvalidOperationException("镜头时间轴需要唯一命名目标 '" + key.targetId + "'，请先在原模板中配置该绑定或选择已有兼容时间轴。");
                }
        }

        private static bool TryGetBounds(GameObject model, out Bounds bounds)
        {
            bounds = default;
            bool found = false;
            foreach (Renderer renderer in model.GetComponentsInChildren<Renderer>(true))
            {
                if (!(renderer is SkinnedMeshRenderer) && !(renderer is MeshRenderer)) continue;
                Bounds candidate = renderer.bounds;
                if (!IsFinite(candidate.center) || !IsFinite(candidate.size) || candidate.size.sqrMagnitude <= 0f) continue;
                if (found) bounds.Encapsulate(candidate);
                else { bounds = candidate; found = true; }
            }
            return found;
        }

        private static string GetScaleNotice(GameObject model)
        {
            if (!TryGetBounds(model, out Bounds bounds)) return "模型包围盒不可用；请在新场景手动核对比例。";
            if (bounds.size.y < 0.5f || bounds.size.y > 3f)
                return "模型高度约 " + bounds.size.y.ToString("0.###") + " Unity 单位，与模板地面/镜头可能不匹配；未改源网格、比例或地面，请在新场景核对。";
            return null;
        }

        private static bool IsFinite(Vector3 value)
        {
            return !float.IsNaN(value.x) && !float.IsInfinity(value.x) && !float.IsNaN(value.y)
                && !float.IsInfinity(value.y) && !float.IsNaN(value.z) && !float.IsInfinity(value.z);
        }
    }
}
