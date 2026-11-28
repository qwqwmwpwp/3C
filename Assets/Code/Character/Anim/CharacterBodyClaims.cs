namespace ThreeC.CharacterBehavior
{
    /// <summary>一次预览中的两个部位持有者。全身实例在两项检查全部通过后同时占用两处。</summary>
    internal struct CharacterBodyClaims
    {
        private CharacterAnimationDemand upper, lower;

        // 在优先级和搭档规则均允许时，一次提交需求申请的所有部位。
        public bool TryClaim(in CharacterAnimationDemand demand, in CharacterAnimationDemand baseline)
        {
            bool needsUpper = demand.Source != CharacterAnimationSource.LowerBody;
            bool needsLower = demand.Source != CharacterAnimationSource.UpperBody;
            if (needsUpper && upper.IsValid || needsLower && lower.IsValid) return false;
            if (baseline.IsValid && (demand.Priority < baseline.Priority ||
                demand.Priority == baseline.Priority && !AllowsEqualPriority(demand, baseline)))
                return false;
            var partner = needsUpper ? lower : upper;
            if (partner.IsValid && (!partner.Rule.Partners.Allows(demand.Key) || !demand.Rule.Partners.Allows(partner.Key)))
                return false;
            if (needsUpper) upper = demand;
            if (needsLower) lower = demand;
            return true;
        }

        // 相同优先级仅查询基础动作的竞争策略，回退姿势在归属确定后解析。
        private static bool AllowsEqualPriority(in CharacterAnimationDemand demand, in CharacterAnimationDemand baseline) =>
            baseline.Rule.EqualPriorityTakeover switch
            {
                CharacterEqualPriorityTakeover.Allow => true,
                CharacterEqualPriorityTakeover.Ranked => demand.Precedes(baseline),
                _ => false
            };

        internal bool HasOwner => upper.IsValid || lower.IsValid;

        // 汇总本次仲裁的基础动作和两个部位持有者。
        public CharacterAnimationComposition Compose(in CharacterAnimationDemand baseline, in CharacterAnimationDemand displayed) =>
            new CharacterAnimationComposition(displayed, upper, lower, baseline);
    }

    /// <summary>仲裁结果只保留上下身持有者。空闲区域从持续更新的默认姿势取样。</summary>
    internal readonly struct CharacterAnimationComposition
    {
        private readonly CharacterAnimationDemand baseline;
        public readonly CharacterAnimationDemand UpperOwner, LowerOwner;
        public readonly CharacterAnimationDemand RequestedBaseline;
        public CharacterAnimationDemand FullBody => baseline;
        public CharacterAnimationDemand Overlay => UpperOwner.Source == CharacterAnimationSource.FullBodyOverlay ? UpperOwner : default;
        public CharacterAnimationDemand UpperBody => UpperOwner.Source == CharacterAnimationSource.UpperBody ? UpperOwner : default;
        public CharacterAnimationDemand LowerBody => LowerOwner.Source == CharacterAnimationSource.LowerBody ? LowerOwner : default;
        // 保存基础动作和已获准占用两个部位的动作。
        public CharacterAnimationComposition(CharacterAnimationDemand baseline, CharacterAnimationDemand upperOwner,
            CharacterAnimationDemand lowerOwner, CharacterAnimationDemand requestedBaseline = default)
        {
            RequestedBaseline = requestedBaseline.IsValid ? requestedBaseline : baseline;
            this.baseline = baseline;
            UpperOwner = upperOwner;
            LowerOwner = lowerOwner;
        }

        // 保留仲裁归属，使用播放计划确定的生效权重。
        public CharacterAnimationComposition WithPlaybackWeights(in CharacterAnimationPlaybackPlan plan)
        {
            var upper = UpperOwner;
            var lower = LowerOwner;
            if (Overlay.IsValid) upper = lower = plan.Overlay.Demand;
            else
            {
                if (UpperBody.IsValid) upper = plan.UpperBody.Demand;
                if (LowerBody.IsValid) lower = plan.LowerBody.Demand;
            }
            return new CharacterAnimationComposition(baseline, upper, lower, RequestedBaseline);
        }
    }
}
