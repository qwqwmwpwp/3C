using System;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 由游戏角色维护许可、招式与表现键的关系，动画宿主只消费翻译后的请求。
    [CreateAssetMenu(menuName = "3C/角色/行为动画映射", fileName = "行为动画映射")]
    public sealed class CharacterBehaviorAnimationProfile : ScriptableObject
    {
        [SerializeField, InspectorName("突进的运动覆盖键")] private string motionOverrideKey;
        [SerializeField, InspectorName("行为表现")] private CharacterBehaviorAnimationMapping[] mappings;
        public string MotionOverrideKey => motionOverrideKey;
        internal CharacterBehaviorAnimationMapping[] Mappings => mappings ?? Array.Empty<CharacterBehaviorAnimationMapping>();

        // 预测与真实行为共用同一份招式编号到动作键的映射。
        public string FindAttackAction(int stateId)
        {
            foreach (var mapping in Mappings)
                if (mapping?.IsAttackState == true && mapping.AttackStateId == stateId) return mapping.EntryAction;
            return null;
        }
    }
}
