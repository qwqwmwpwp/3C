exec((__import__('pathlib').Path(__file__).with_name('editing.py')).read_text('utf-8-sig'))
use('CharacterPulseAnimationState')
sub('        private float? lastRenderedAttackPhase;', '        private CharacterPlaybackOrigin attackOrigin; // 最近一次匹配本映射的招式，空行为帧不会覆盖待补的尾段。')
sub('timedActive = matches && active && step.Attack.Handoff.IsAttack && step.Attack.ElapsedSeconds < step.Attack.Handoff.AnimationDuration;', '''timedActive = matches && active && step.Attack.Handoff.IsAttack &&
                step.Attack.ElapsedSeconds < step.Attack.Handoff.Duration && step.Attack.ElapsedSeconds < step.Attack.Handoff.AnimationDuration;''')
sub('            if (!matches)\n            {\n                if (step.AttackMoveStarted)', '''            if (!matches)
            {
                if (step.Attack.StateId != 0 || ((step.InterruptedBehaviors | step.WithdrawnBehaviors) & CharacterBehaviorId.Attack) != 0)
                    attackOrigin = default;
                if (step.AttackMoveStarted)''')
sub('''            if (newInstance)
            {
                lastRenderedAttackPhase = null;
                attackEntryClipPhase = step.Attack.Handoff.IsAttack ? step.Attack.Handoff.ClipProgress(step.Attack.StartElapsed) : 0f;
            }
            attackInstance = instance;''','''            if (newInstance)
                attackEntryClipPhase = step.Attack.Handoff.IsAttack ? step.Attack.Handoff.ClipProgress(step.Attack.StartElapsed) : 0f;
            attackOrigin = binding.Origin;
            attackInstance = instance;''')
sub('''            if (!active)
            {
                resumeTimed = false;''','''            if (reconnect && (seed || !step.AttackMoveStarted || !newInstance))
            {
                resumeTimed = CanRecover();
                return;
            }
            if (!active)
            {
                resumeTimed = resumeTimed && CanRecover();''')
sub('''            else if (reconnect && !step.AttackMoveStarted)
            {
                resumeTimed = timedActive;
            }
            else if (newInstance''', '            else if (newInstance')
cut('        // 只从确实开始的同一招式','        // 为自定义交接','''        // 区分仍在窗口内的播放与同素材尚未实际求值的自然尾段，播完的素材不再申请。
        private bool CanRecover()
        {
            if (!binding.Rule.IsAttackState) return observedActive && timedActive;
            if (attackOrigin.Move == 0 || !handoff.IsAttack) return false;
            var cursor = animations.ReadAttackCursor(attackOrigin, binding.Rule.EntryAction);
            if (cursor.HasValue && cursor.Value.normalizedTime >= 1f) return false;
            return timedActive || cursor.HasValue && attackElapsed >= handoff.AnimationDuration &&
                handoff.AnimationDuration <= handoff.Duration;
        }

''')
sub('(!handoff.Valid || resumedEntry && !timedActive)', '(!handoff.Valid || resumedEntry && !CanRecover())')
sub('''            entryAttackInstance = origin.Move;
            animations.TryPlayEntry(binding, origin, source, out entry);''','''            entryAttackInstance = origin.Move;
            if (animations.TryPlayEntry(binding, origin, source, out entry) && resumedEntry)
                entry.ResumeCursor = animations.ReadAttackCursor(origin, binding.Rule.EntryAction);''')
sub('''            if (resumeTimed && observedActive) Start(binding.Origin, CharacterPlaybackStartSource.Rebind);''','''            if (resumeTimed && CanRecover())
                Start(binding.Rule.IsAttackState ? attackOrigin : binding.Origin, CharacterPlaybackStartSource.Rebind);''')
sub('Mathf.Max(lastRenderedAttackPhase.GetValueOrDefault(), firstPose.Value)', '(entry.ResumeCursor?.normalizedTime ?? firstPose.Value)')
sub('''        // 在仲裁器重绑前保存已发事件的游标，终态句柄不再续播。''','''        // 在资源重绑前为可恢复动作排入一次申请，已结束的旧句柄不再续播。''')
sub('if (observedActive && timedActive && (entry == null', 'if (CanRecover() && (entry == null')
sub('                RememberAttackCursor();\n','')
sub('''        // 暂停时撤回旧句柄，并只为尚在计时的行为保留恢复意图。''','''        // 暂停撤回句柄，恢复资格根据角色时间与独立保存的实际求值位置判断。''')
sub('            RememberAttackCursor();\n','')
sub('            resumeTimed = timedActive;', '            resumeTimed = CanRecover();')
sub('            lastRenderedAttackPhase = null;','            attackOrigin = default;')
write()

