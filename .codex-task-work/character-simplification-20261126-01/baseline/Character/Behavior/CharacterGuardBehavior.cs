namespace ThreeC.CharacterBehavior
{
    /// <summary>由纯裁决输入维持的举盾示例。共存、限速及打断规则由创建方决定。</summary>
    public sealed class CharacterGuardBehavior : CharacterBehavior
    {
        public override bool Evaluate(CharacterBehaviorContext context) => context.Input.GuardHeld;

        public override bool ShouldContinue(CharacterBehaviorContext context) => Evaluate(context);
    }
}
