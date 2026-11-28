using System.Collections.Generic;

namespace ThreeC.CharacterBehavior
{
    // 保存已验证的模块选择，候选选择只有应用成功才取代已安装选择。
    internal sealed class CharacterModuleSelection
    {
        internal readonly List<AnimationModule> Roots;
        internal readonly HashSet<AnimationModule> Excluded;
        internal readonly AnimationModuleAssembly Assembly;

        // 展开并校验一组根模块及排除项。
        internal CharacterModuleSelection(List<AnimationModule> roots, HashSet<AnimationModule> excluded)
        {
            Roots = roots;
            Excluded = excluded;
            Assembly = AnimationModuleAssembly.Resolve(roots, excluded);
            if (Assembly.Decisions) Assembly.BindDecisions();
        }
    }
}
