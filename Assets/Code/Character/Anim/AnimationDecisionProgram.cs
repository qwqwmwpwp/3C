using System;
using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 标识程序资产的装配失败，使热更失败时保留上一个已验证的角色配置。
    internal sealed class AnimationDecisionProgramException : ArgumentException
    {
        // 携带具体的缺失资源、节点或输出错误。
        internal AnimationDecisionProgramException(string message, Exception cause = null) : base(message, cause) { }
    }

    // 保存可复制、可直接在原生 Inspector 中编辑的命名表达式和有序输出分支。
    [CreateAssetMenu(menuName = "3C/动画模块/判定程序数据", fileName = "动画判定程序")]
    public sealed class AnimationDecisionProgram : ScriptableObject
    {
        private const string DefaultPath = "ThreeC/DefaultAnimationDecisions";
        [SerializeField, InspectorName("命名节点"), Tooltip("表达式可引用同阶段输入、其他命名节点及枚举常量。支持比较、&&、||、!、加减和 Select(条件, 真值, 假值)。")]
        private NamedExpression[] nodes = Array.Empty<NamedExpression>();
        [SerializeField, InspectorName("输出分支（按顺序）"), Tooltip("每项输出按从上到下的顺序命中首个条件。最后一行留空条件作为兜底。复制默认程序可保留完整输出。")]
        private OutputBranch[] branches = Array.Empty<OutputBranch>();
        [NonSerialized] private CompiledAnimationDecisions cached;
        [NonSerialized] private string cachedDefinition;

        // 描述可复用的有名计算，类型从表达式推导。
        [Serializable]
        internal sealed class NamedExpression
        {
            [InspectorName("节点名")] public string Name;
            [InspectorName("表达式"), TextArea(1, 3)] public string Expression;
        }

        // 关联输出、分支名、命中条件和返回值，声明位置决定同输出的优先级。
        [Serializable]
        internal sealed class OutputBranch
        {
            [InspectorName("输出")] public AnimationDecisionOutput Output;
            [InspectorName("分支名")] public string Name;
            [InspectorName("条件"), TextArea(1, 3)] public string When;
            [InspectorName("返回值"), TextArea(1, 3)] public string Value;
        }

        // 在主线程装配阶段冻结资产内容，重复角色共享相同的已校验指令。
        internal static CompiledAnimationDecisions Bind(AnimationDecisionProgram selected)
        {
            var asset = selected ? selected : Resources.Load<AnimationDecisionProgram>(DefaultPath);
            if (!asset) throw new AnimationDecisionProgramException($"找不到基础动画判定程序 Resources/{DefaultPath}。");
            string definition = JsonUtility.ToJson(asset);
            if (asset.cached != null && asset.cachedDefinition == definition) return asset.cached;
            try
            {
                var result = new CompiledAnimationDecisions(asset.nodes, asset.branches);
                asset.cachedDefinition = definition;
                return asset.cached = result;
            }
            catch (ArgumentException error)
            {
                throw new AnimationDecisionProgramException($"动画判定程序 {asset.name}：{error.Message}", error);
            }
        }
    }
}
