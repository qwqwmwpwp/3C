using System;
using UnityEngine;
using UnityEngine.Animations;

namespace ThreeC.CharacterBehavior
{
    /// <summary>骨骼来自当前 Animator 的骨架。留空的部位跳过，原有角色默认旁路。</summary>
    [Serializable]
    public sealed class CharacterPoseInertiaSettings : CharacterInertiaTuning
    {
        [InspectorName("脊柱")] public Transform Spine;
        [InspectorName("胸部")] public Transform Chest;
        [InspectorName("左上臂")] public Transform LeftUpperArm;
        [InspectorName("左前臂")] public Transform LeftForearm;
        [InspectorName("右上臂")] public Transform RightUpperArm;
        [InspectorName("右前臂")] public Transform RightForearm;

        internal CharacterInertiaBones Bones => new CharacterInertiaBones
        {
            Spine = this.Spine,
            Chest = this.Chest,
            LeftUpperArm = this.LeftUpperArm,
            LeftForearm = this.LeftForearm,
            RightUpperArm = this.RightUpperArm,
            RightForearm = this.RightForearm
        };
    }

    internal enum CharacterPoseRegion { Chest, LeftArm, RightArm }

    internal struct CharacterPoseBoneBinding
    {
        public TransformStreamHandle Handle;
        public CharacterPoseRegion Region;
        public float Share;
    }
}
