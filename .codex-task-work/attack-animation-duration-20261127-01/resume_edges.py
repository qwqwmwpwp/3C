exec((__import__('pathlib').Path(__file__).with_name('editing.py')).read_text('utf-8-sig'))
use('CharacterPulseAnimationState')
sub('''            if (!matches)
            {
                if (step.Attack.StateId != 0 || ((step.InterruptedBehaviors | step.WithdrawnBehaviors) & CharacterBehaviorId.Attack) != 0)
                    attackOrigin = default;''','''            bool revoked = ((step.InterruptedBehaviors | step.WithdrawnBehaviors) & CharacterBehaviorId.Attack) != 0;
            if (!active && revoked)
            {
                attackOrigin = default;
                resumeTimed = false;
                return;
            }
            if (!matches)
            {
                if (step.Attack.StateId != 0) attackOrigin = default;''')
sub('if (animations.TryPlayEntry(binding, origin, source, out entry) && resumedEntry)', 'if (animations.TryPlayEntry(binding, origin, source, out entry) && binding.Rule.IsAttackState && resumedEntry && entry != null)')
sub('            entryAttackInstance = 0;\n            resumedEntry = false;','            entryAttackInstance = 0;\n            resumedEntry = resumeTimed = false;')
write()

