using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    // 保存被拒绝的装配请求及当时的资源版本，供角色继续播放并在修复后重试同一请求。
    internal sealed class CharacterConfigurationRejection
    {
        internal readonly string Message;
        internal readonly CharacterAnimationConfig Source;
        internal readonly CharacterModuleSelection Selection;
        private readonly GroundLibraryBindings bindings;
        private readonly CharacterBasePoseStrategy strategy;
        private readonly Avatar avatar;
        private readonly int revision, sourceRevision;

        // 冻结本次失败的输入，不改写已安装的模块集合。
        internal CharacterConfigurationRejection(string message, CharacterAnimationConfig source,
            CharacterModuleSelection selection, Animator animator)
        {
            Message = message;
            Source = source;
            Selection = selection;
            bindings = source ? source.GroundBindings : null;
            strategy = source ? source.PoseStrategy : null;
            avatar = animator ? animator.avatar : null;
            sourceRevision = source ? source.Revision : 0;
            revision = GroundSearchLibraries.Revision;
        }

        // 只有依赖变化后才重试同一无效请求，避免逐帧重建或重复报错。
        internal bool Changed(CharacterAnimationConfig source, Animator animator) =>
            !ReferenceEquals(Source, source) || revision != GroundSearchLibraries.Revision ||
            (animator ? animator.avatar : null) != avatar || source &&
            (source.Revision != sourceRevision || source.GroundBindings != bindings || source.PoseStrategy != strategy);
    }
}
