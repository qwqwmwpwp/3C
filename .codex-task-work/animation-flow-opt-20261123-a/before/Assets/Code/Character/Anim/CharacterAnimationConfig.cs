using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    [CreateAssetMenu(menuName = "3C/角色数据/04 动画表现", fileName = "04 动画表现")]
    public sealed class CharacterAnimationConfig : ScriptableObject
    {
        [Header("行为映射与身体表现")]
        [SerializeField, InspectorName("表现参数")] internal CharacterPresentationSettings Presentation = new CharacterPresentationSettings();
        [Header("步态与动作选择")]
        [SerializeField, InspectorName("步态参数")] internal CharacterGaitSettings Gaits = new CharacterGaitSettings();
        [Header("动作资源")]
        [SerializeField, InspectorName("地面动作数据库")] internal CharacterMotionDatabase MotionDatabase;
        [SerializeField, InspectorName("播放库"), Tooltip("留空时使用地面动作数据库。")] internal CharacterMotionDatabase PlaybackLibrary;
    }
}
