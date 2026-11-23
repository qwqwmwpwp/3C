#if UNITY_EDITOR
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 与原接收器在同一个 Animator 对象上旁听真实事件，绝不转发武器操作。
    [AddComponentMenu("")]
    internal sealed class CharacterReplayEventProbe : MonoBehaviour
    {
        internal CharacterReplayEvents Ledger;
        public void SwitchSocket(AnimationEvent marker)
        {
            if (Ledger != null && marker != null && marker.isFiredByAnimator)
                Ledger.Received(marker.animatorClipInfo.clip, marker);
        }
    }
}
#endif
