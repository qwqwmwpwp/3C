exec((__import__('pathlib').Path(__file__).with_name('editing.py')).read_text('utf-8-sig'))
use('CharacterAnimationPlayer')
sub('private readonly CharacterLayerObservation[] seen = new CharacterLayerObservation[4];','''private readonly CharacterLayerObservation[] seen = new CharacterLayerObservation[4];
        private readonly float[] visibleWeights = new float[4];''')
sub('clips.PrepareSegment(plan, to, (float)(to - from))','clips.PrepareSegment(plan, to, (float)(to - from), visibleWeights)')
sub('ik.Prepare(owners, motion, plan, 0f, to);','ik.Prepare(owners, motion, plan, 0f, to, keepClocks: true);')
sub('''                for (int i = retiring.Count - 1; i >= 0; i--)
                    if (retiring[i].LastEvaluatedAt < to) composer.FinishRetired(retiring[i], false);''','''                // 新请求及撤销已记下本段真实时刻，下一轮重新计算最早截止。
'''.rstrip())
sub('for (int i = 0; i < seen.Length; i++) seen[i] = clips.Observe((CharacterAnimationSource)i);','''for (int i = 0; i < seen.Length; i++)
            {
                var source = (CharacterAnimationSource)i;
                seen[i] = clips.Observe(source);
                visibleWeights[i] = clips.LayerWeight(source);
            }''')
sub('internal void RecordEvaluated(', 'private void RecordEvaluated(')
sub('internal void EvaluatePose() => clips.Evaluate();', '''internal void EvaluatePose(CharacterAnimationPlaybackPlan plan) => clips.Evaluate(() => RecordEvaluated(plan));''')
write()
use('CharacterAnimationPipeline')
sub('modules.Player.EvaluatePose();','modules.Player.EvaluatePose(plan);')
sub('            modules.Player.RecordEvaluated(plan);\n','')
write()
use('CharacterAnimationIk')
sub('in CharacterAnimationPlaybackPlan plan, float deltaTime, double sampleTime)','in CharacterAnimationPlaybackPlan plan, float deltaTime, double sampleTime, bool keepClocks = false)')
sub('float animationDelta = clips.LayerBlendDelta(deltaTime);','float animationDelta = keepClocks ? 0f : clips.LayerBlendDelta(deltaTime);')
write()
use('CharacterBodyPlaybackGraph')
sub('            private bool Writes(','''            // 图重建时从上次实际输出的层覆盖继续后续权重过渡。
            internal void RestoreWeight(float weight) => Weight = previousWeight = weight;

            private bool Writes(''')
sub('        private static void Connect(','''        // 恢复三处可见覆盖，帧间重绑仍由原蒙版和全身混合输出。
        internal void RestoreWeights(float[] weights)
        {
            lower.RestoreWeight(weights[(int)CharacterAnimationSource.LowerBody]);
            upper.RestoreWeight(weights[(int)CharacterAnimationSource.UpperBody]);
            handoff.RestoreWeight(weights[(int)CharacterAnimationSource.FullBodyOverlay]);
        }

        private static void Connect(''')
write()
use('CharacterClipPlayback')
sub('PrepareSegment(in CharacterAnimationPlaybackPlan plan, double at, float step)\n        {\n            Open();', '''PrepareSegment(in CharacterAnimationPlaybackPlan plan, double at, float step, float[] visibleWeights)
        {
            bool rebuilding = body == null;
            Open();''')
sub('if (body == null) return false;\n            body.Apply(this, plan, 0f);','if (body == null) return false;\n            if (rebuilding) body.RestoreWeights(visibleWeights);\n            body.Apply(this, plan, 0f);')
write()
use('CharacterClipMixer')
sub('rate = System.Math.Max(0d, targetSeconds - voice.Clip.GetTime()) / step;', '''double current = voice.Clip.GetTime();
                    rate = System.Math.Max(0d, targetSeconds - current) / step;
                    // 只有合法的素材末端才向上取一位，避免除再乘使末尾事件落在下一帧。
                    if (at >= handle.Clock.NaturalEnd && current + rate * step < targetSeconds)
                        rate = System.BitConverter.Int64BitsToDouble(System.BitConverter.DoubleToInt64Bits(rate) + 1);''')
write()
use('CharacterAttackAnimationBinding')
sub('Cancel(frame.Time, interrupted ?', '''double at = !interrupted && !withdrawn && attack.Duration > 0f && attack.ElapsedSeconds >= attack.Duration
                    ? attack.StartedAt + attack.Duration : frame.Time;
                Cancel(at, interrupted ?''')
write()

