exec((__import__('pathlib').Path(__file__).with_name('editing.py')).read_text('utf-8-sig'))
use('CharacterActionHandoffSettingsDrawer');sub('child in property)','child in property.Copy())');write()
use('CharacterRigidbodyMotor')
lines('internal readonly Vector3 Start;','Start = body.position;','internal readonly Surface Contacts;','Contacts = contacts;') if False else None
cut('        internal readonly struct StepState','        internal StepState ReadStepState()', '''        // 保存逐帧行为裁决使用的实测朝向和地面速度。
        internal readonly struct StepState
        {
            internal readonly Vector2 Facing, GroundVelocity;

            // 从同一刚体状态和支撑面取得输入方向与起手条件。
            internal StepState(Rigidbody body, Surface contacts)
            {
                Vector3 forward = body.rotation * Vector3.forward;
                Facing = new Vector2(forward.x, forward.z);
                GroundVelocity = CharacterMotorStep.GroundVelocity(body.velocity, contacts.Ground);
            }
        }

''')
write()
use('CharacterActionHandoff')
cut('        internal CharacterActionHandoff Resolve(', '    }\n\n    /// <summary>本次表现', '''        // 根据普通动作的配置和已获得移动许可的时刻生成交接权重。
        internal CharacterActionHandoff Resolve(float duration, float? moveStart = null) =>
            new CharacterActionHandoff(duration, FullFrom, ReleaseFrom, MoveBlendSeconds, moveStart);
''')
cut('        public CharacterBehaviorPhase PhaseAt(', '        public float WeightAt(')
write()
use('CharacterBehaviorAnimationMapping');sub('            if (IsAttackState) return false;\n            if (settings?.OverridePhases != true) return false;','            if (IsAttackState || settings?.OverridePhases != true) return false;');write()
use('CharacterLocomotionPoseSelector');sub('            var timing = overlay.Timing;', '''            var timing = overlay.Playback?.ExplicitAttackTiming.Valid == true && overlay.Playback == previous.Overlay.Demand.Playback
                ? previous.Overlay.Demand.Timing : overlay.Timing;''')
write()

