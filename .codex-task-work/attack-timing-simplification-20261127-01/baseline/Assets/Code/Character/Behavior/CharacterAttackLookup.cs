using System;

namespace ThreeC.CharacterBehavior
{
    // 按固定状态身份读取作者表或预测快照，保留各状态自己的路线顺序。
    internal sealed class CharacterAttackLookup
    {
        private readonly CharacterAttackTable table;
        internal ReadOnlySpan<CharacterAttackLink> Openers => table.Openers;
        internal float OpeningStepDistance => table.OpeningStepDistance;

        // 绑定本流程用于解析路线的配置。
        private CharacterAttackLookup(CharacterAttackTable table) => this.table = table;

        // 真实角色直接使用作者数据，预测在主线程捕获全部路线和动作参数。
        internal static CharacterAttackLookup Create(CharacterAttackTable source, bool snapshot) =>
            new CharacterAttackLookup(snapshot ? source.Snapshot() : source);

        // 返回当前状态按作者顺序排列的后续路线。
        internal ReadOnlySpan<CharacterAttackLink> Links(int id) => table.Find(id)?.Links;

        // 由请求方检查专属准入与冷却，许可目标始终获得独立副本。
        internal CharacterAttackTarget Resolve(CharacterAttackLink link, CharacterBehaviorContext context,
            CharacterAttackBehavior requester, out CharacterBehaviorRejectReason reason)
        {
            int id = link.TargetId;
            var definition = table.Find(id);
            reason = id == 0 ? CharacterBehaviorRejectReason.NoRoute : CharacterBehaviorRejectReason.InvalidConfiguration;
            if (definition == null) return default;
            reason = requester.CheckAdmission(id, definition, context);
            return reason == default ? new CharacterAttackTarget(id, definition.Name, link.BlendRange, definition.Snapshot()) : default;
        }
    }
}
