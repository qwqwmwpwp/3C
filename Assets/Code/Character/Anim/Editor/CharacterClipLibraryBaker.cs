using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 从源片段读取需要写入播放库的命名曲线。
    internal static class CharacterClipLibraryBaker
    {
        // 导入曲线用归一化相位，动画资源自带的根 Animator 曲线以秒为时间。
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
