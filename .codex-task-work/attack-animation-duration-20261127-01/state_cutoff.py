exec((__import__('pathlib').Path(__file__).with_name('editing.py')).read_text('utf-8-sig'))
use('CharacterPulseAnimationState')
sub('        // 区分仍在窗口内的播放与同素材尚未实际求值的自然尾段，播完的素材不再申请。','        // 恢复窗口内的播放，或补齐实际游标到状态截止与素材终点中较早者的末段。')
sub('''            if (cursor.HasValue && cursor.Value.normalizedTime >= 1f) return false;
            return timedActive || cursor.HasValue && attackElapsed >= handoff.AnimationDuration &&
                handoff.AnimationDuration <= handoff.Duration;''','''            float cutoff = Mathf.Min(handoff.Duration, handoff.AnimationDuration);
            if (cursor.HasValue && cursor.Value.normalizedTime >= handoff.ClipProgress(cutoff)) return false;
            return timedActive || cursor.HasValue && attackElapsed >= cutoff;''')
write()

