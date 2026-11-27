using System;
using System.Collections.Generic;

namespace ThreeC.CharacterBehavior
{
    // 按原顺序读取攻击路线，真实角色回读作者表，预测通过冻结索引解析唯一目标。
    internal sealed class CharacterAttackLookup
    {
        private readonly CharacterAttackTable table;
        private readonly Dictionary<int, CharacterAttackRow> frozenRows;
        internal ReadOnlySpan<CharacterAttackLink> Openers => table.Openers;
        internal float OpeningStepDistance => table.OpeningStepDistance;

        // 只给独立冻结的行建立索引，重复编号始终无法解析。
        private CharacterAttackLookup(CharacterAttackTable table, bool frozen)
        {
            this.table = table;
            if (!frozen) return;
            frozenRows = new Dictionary<int, CharacterAttackRow>(table.States?.Length ?? 0);
            if (table.States == null) return;
            foreach (var row in table.States)
            {
                if (row == null || row.Id <= 0) continue;
                frozenRows[row.Id] = frozenRows.ContainsKey(row.Id) ? null : row;
            }
        }

        // 在主线程装配时决定直接读取作者表，或复制全部行和片段数值供预测使用。
        internal static CharacterAttackLookup Create(CharacterAttackTable source, bool snapshot) =>
            new CharacterAttackLookup(snapshot ? source.Snapshot() : source, snapshot);

        // 仅当所有状态定义均由只读查询的内置类型提供时，才允许同一调度共享冻结表。
        internal static CharacterAttackLookup CaptureIfBuiltIn(CharacterAttackTable source)
        {
            if (source == null) return null;
            if (source.States != null)
                foreach (var row in source.States)
                    if (row?.Definition != null && !(row.Definition is CharacterAttackMoveSettings)) return null;
            return Create(source, true);
        }

        // 实时数据使用作者表的歧义检查，冻结数据使用构造时封存的编号索引。
        private CharacterAttackRow Find(int id)
        {
            if (frozenRows == null) return table.Find(id);
            return frozenRows.TryGetValue(id, out var row) ? row : null;
        }

        // 暴露只读的有序路线，调用方的路线下标与作者列表保持一致。
        internal ReadOnlySpan<CharacterAttackLink> Links(int id) => Find(id)?.Links;

        // 由请求方检查专属准入与冷却，许可目标始终获得独立副本。
        internal CharacterAttackTarget Resolve(CharacterAttackLink link, CharacterBehaviorContext context,
            CharacterAttackBehavior requester, out CharacterBehaviorRejectReason reason)
        {
            var row = Find(link.To);
            reason = link.To == 0 ? CharacterBehaviorRejectReason.NoRoute : CharacterBehaviorRejectReason.InvalidConfiguration;
            if (row?.Definition == null) return default;
            reason = requester.CheckAdmission(link.To, row.Definition, context);
            return reason == default ? new CharacterAttackTarget(row.Id, row.Name, link.BlendRange, row.Definition.Snapshot()) : default;
        }
    }
}
