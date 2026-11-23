using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>在世界坐标中计算台阶探线与抬升量，供实时物理和预测使用。</summary>
    internal readonly struct CharacterStepProbePlan
    {
        private readonly Vector3 foot;
        private readonly float skin, height;
        internal Vector3 Direction { get; }
        internal Vector3 FaceOrigin => foot + Vector3.up * (skin * 2f);
        internal float FaceDistance { get; }
        internal float TopDistance => height + skin * 2f;

        internal CharacterStepProbePlan(Vector3 foot, Vector3 displacement, float radius, float skin, float height)
        {
            this.foot = foot;
            this.skin = skin;
            this.height = height;
            Direction = displacement.normalized;
            FaceDistance = radius + Mathf.Min(radius, displacement.magnitude);
        }

        internal Vector3 TopOrigin(float faceDistance) =>
            foot + Direction * (faceDistance + skin * 2f) + Vector3.up * TopDistance;

        internal bool TryLift(float topY, out float lift)
        {
            float rise = topY - foot.y;
            lift = 0f;
            if (rise <= skin || rise > height + 0.001f) return false;
            lift = rise + skin;
            return true;
        }
    }
}
