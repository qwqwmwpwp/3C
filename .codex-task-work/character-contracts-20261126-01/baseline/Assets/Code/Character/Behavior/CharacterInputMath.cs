using System;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>统一处理行为输入的有限数值、方向与计时余量。</summary>
    internal static class InputMath
    {
        // 将非法数值回退，有限数值限制在非负范围。
        public static float NonNegative(float value, float fallback)
        {
            return float.IsNaN(value) || float.IsInfinity(value) ? fallback : Math.Max(0f, value);
        }

        // 推进计时并去掉步长引入的微小余量。
        public static float Elapsed(float remaining, float deltaTime)
        {
            float next = Math.Max(0f, remaining - deltaTime);
            return next <= deltaTime * 0.00001f ? 0f : next; // 按步长清除舍入残差，零步长保留余量。
        }

        // 限制方向幅度并清除无效分量。
        public static Vector2 ClampDirection(Vector2 direction)
        {
            if (float.IsNaN(direction.x) || float.IsInfinity(direction.x) ||
                float.IsNaN(direction.y) || float.IsInfinity(direction.y))
                return Vector2.zero;

            double lengthSquared = (double)direction.x * direction.x + (double)direction.y * direction.y; // 避免平方和溢出。
            if (lengthSquared <= 1d)
                return direction;

            double length = Math.Sqrt(lengthSquared);
            return new Vector2((float)(direction.x / length), (float)(direction.y / length));
        }

        // 将足够长的有限方向归一化。
        public static bool TryNormalizeDirection(Vector2 direction, out Vector2 normalized)
        {
            double lengthSquared = (double)direction.x * direction.x + (double)direction.y * direction.y;
            double minimumLengthSquared = (double)Vector2.kEpsilon * Vector2.kEpsilon; // 与方向有效性检查共用下限。
            if (double.IsNaN(lengthSquared) || double.IsInfinity(lengthSquared) ||
                lengthSquared <= minimumLengthSquared)
            {
                normalized = Vector2.zero;
                return false;
            }

            double length = Math.Sqrt(lengthSquared);
            normalized = new Vector2((float)(direction.x / length), (float)(direction.y / length));
            return true;
        }
    }
}
