namespace ThreeC.CharacterBehavior
{
    // 组合注册行为编号与行为自己的起步实例序号，默认值表示没有起步操作。
    internal readonly struct MotionStartToken
    {
        private readonly CharacterBehaviorId behavior;
        private readonly long instance;

        // 为活动动作的本地起步实例绑定注册身份。
        internal MotionStartToken(CharacterBehaviorId behavior, long instance)
        {
            this.behavior = behavior;
            this.instance = instance;
        }

        internal bool HasValue => behavior != CharacterBehaviorId.None;

        // 首次遇到该令牌时推进指定执行域的游标。
        internal bool TryConsume(ref MotionStartToken cursor)
        {
            if (!HasValue || (behavior == cursor.behavior && instance == cursor.instance)) return false;
            cursor = this;
            return true;
        }
    }
}
