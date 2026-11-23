using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    internal static class CharacterClipLibraryBaker
    {
        public static CharacterClipLibrary Build(AnimatorController controller, Dictionary<AnimationClip, CharacterClipCurves> clips, bool bodyParts)
        {
            var layers = controller.layers;
            var result = new CharacterClipLibrary { BodyOutputs = bodyParts, Layers = new CharacterClipLayer[layers.Length] };
            var states = new List<CharacterClipState>();
            for (int i = 0; i < layers.Length; i++)
            {
                var layer = layers[i];
                if (layer.syncedLayerIndex >= 0) throw new InvalidOperationException($"{layer.name}：同步层无法直接绑定片段。");
                var data = new CharacterClipLayer { Mask = layer.avatarMask, Weight = layer.defaultWeight,
                    Additive = layer.blendingMode == AnimatorLayerBlendingMode.Additive };
                Collect(i, layer.name, layer.stateMachine, layer.stateMachine.defaultState, data, states, clips);
                result.Layers[i] = data;
            }
            if (bodyParts && !result.HasBodyMasks())
                throw new InvalidOperationException("双层库需要互补的下身、上身遮罩。根和双腿归下身，双臂归上身，两层均使用覆盖混合。");
            result.States = states.ToArray();
            result.Clips = new List<CharacterClipCurves>(clips.Values).ToArray();
            return result;
        }

        private static void Collect(int layer, string path, AnimatorStateMachine machine, AnimatorState initial,
            CharacterClipLayer data, List<CharacterClipState> states, Dictionary<AnimationClip, CharacterClipCurves> curves)
        {
            if (machine.behaviours.Length > 0 || machine.anyStateTransitions.Length > 0 || machine.entryTransitions.Length > 0)
                throw new InvalidOperationException($"{path}：直接片段播放由 C# 决定流转，请使用直接片段绑定。");
            foreach (var row in machine.states)
            {
                var state = row.state;
                string name = path + "." + state.name;
                if (!(state.motion is AnimationClip clip) || state.behaviours.Length > 0 || state.transitions.Length > 0 ||
                    state.speedParameterActive || state.timeParameterActive || state.mirrorParameterActive || state.mirror ||
                    state.cycleOffsetParameterActive || state.cycleOffset != 0f || state.iKOnFeet)
                    throw new InvalidOperationException($"{name}：需要直接片段、固定状态速度与 C# 过渡和 IK。");
                states.Add(new CharacterClipState { Path = name, Clip = clip, Layer = layer, Speed = state.speed });
                if (state == initial) data.DefaultState = name;
                if (!curves.ContainsKey(clip)) curves.Add(clip, ReadCurves(clip));
            }
            foreach (var child in machine.stateMachines)
                Collect(layer, path + "." + child.stateMachine.name, child.stateMachine, initial, data, states, curves);
        }

        internal static CharacterClipCurves ReadCurves(AnimationClip clip)
        {
            var curves = new List<CharacterClipCurve>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            var importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(clip)) as ModelImporter;
            var source = importer == null ? null : Array.Find(importer.clipAnimations, item => item.name == clip.name);
            if (source != null)
            {
                foreach (var item in source.curves ?? Array.Empty<ClipAnimationInfoCurve>())
                    if (!string.IsNullOrWhiteSpace(item.name) && item.curve != null && names.Add(item.name))
                        curves.Add(new CharacterClipCurve { Name = item.name, Curve = item.curve });
            }
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                if (binding.path.Length != 0 || binding.type != typeof(Animator) || names.Contains(binding.propertyName)) continue;
                var native = AnimationUtility.GetEditorCurve(clip, binding);
                if (native != null && names.Add(binding.propertyName))
                    curves.Add(new CharacterClipCurve { Name = binding.propertyName, InSeconds = true, Curve = native });
            }
            return new CharacterClipCurves { Clip = clip, Curves = curves.ToArray() };
        }
    }
}
