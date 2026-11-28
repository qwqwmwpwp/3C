using System;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 将可复用候选规则中的逻辑库和逻辑组绑定到此角色的烘焙资源。
    [CreateAssetMenu(menuName = "3C/角色数据/地面动作库绑定", fileName = "地面动作库绑定")]
    public sealed class GroundLibraryBindings : ScriptableObject
    {
        [SerializeField, InspectorName("逻辑库绑定（按默认搜索顺序）")]
        internal GroundLibraryBinding[] Libraries;

#if UNITY_EDITOR
        // 绑定键、组或数据库变化后通知角色在下一次采集前重新装配。
        private void OnValidate() => GroundSearchLibraries.InvalidateBindings();
#endif
    }

    // 声明一个逻辑库的真实数据库，以及本库有效的逻辑组映射。
    [Serializable]
    internal sealed class GroundLibraryBinding
    {
        [InspectorName("逻辑库键")] public string Key;
        [InspectorName("数据库")] public CharacterMotionDatabase Database;
        [InspectorName("逻辑组绑定")] public GroundGroupBinding[] Groups;
    }

    // 使用数据库内的稳定 ID 绑定组，源组重命名后继续指向同一组。
    [Serializable]
    internal sealed class GroundGroupBinding
    {
        [InspectorName("逻辑组键")] public string Key;
        [InspectorName("库内组 ID")] public string GroupId;
    }

    // 向宿主报告本次角色装配必须拒绝的地面绑定错误。
    internal sealed class GroundBindingException : ArgumentException
    {
        // 保留候选表、逻辑键或绑定声明的具体失败位置。
        internal GroundBindingException(string message) : base(message) { }
    }
}
