from pathlib import Path
r=Path('Assets/CharacterBehavior/Runtime')
p=r/'CharacterMatchReplay.cs';s=p.read_text()
s=s.replace('    // 只在 Inspector', '    internal enum GroundReplayCase { Stop, ReverseAfterRelease, DirectLeft, DirectRight }\n\n    // 只在 Inspector')
s=s.replace('        private readonly bool reverse;', '''        private readonly GroundReplayCase scenario;
        private readonly Vector2 newDirection;
        private bool Direct => scenario == GroundReplayCase.DirectLeft || scenario == GroundReplayCase.DirectRight;
        private bool ChangesDirection => scenario != GroundReplayCase.Stop;
        private string entry, query;
        private float directionSlip;
        private int directionSamples;''')
s=s.replace('Vector3 heading, bool reverse,','Vector3 heading, GroundReplayCase scenario,')
s=s.replace('            this.reverse = reverse;', '''            this.scenario = scenario;
            var right = new Vector2(forward.y, -forward.x);
            newDirection = scenario == GroundReplayCase.DirectLeft ? -right :
                scenario == GroundReplayCase.DirectRight ? right : -forward;''')
s=s.replace('double brakeEnd = releaseTime + (reverse ? 0.12 : 0.85);', 'double brakeEnd = releaseTime + (Direct ? 0d : ChangesDirection ? 0.12 : 0.85);')
s=s.replace('                reverse &&','                ChangesDirection &&').replace('brakeEnd + (reverse ?', 'brakeEnd + (ChangesDirection ?').replace('phase == 3 ? -forward','phase == 3 ? newDirection')
s=s.replace('Vector2.Dot(frame.Input.Direction, forward) < -0.9f','Vector2.Dot(frame.Input.Direction, newDirection) > 0.9f')
s=s.replace('            var layer = pipeline.DisplayedBase;', '''            if (phase == 3)
            {
                directionSlip = slip;
                directionSamples = measurements;
            }
            var layer = pipeline.DisplayedBase;
            if (acceptedReverse && phase == 3 && query == null)
                query = pipeline.Matcher.Describe();
            bool isTurn = layer.Binding.Variant == CharacterLocomotionVariant.TurnLeft ||
                layer.Binding.Variant == CharacterLocomotionVariant.TurnRight;''')
s=s.replace('layer.Command != CharacterAnimationPlaybackCommand.Keep)', 'layer.Command != CharacterAnimationPlaybackCommand.Keep && (!Direct || isTurn))')
s=s.replace('                observedRestart = true;', '''                observedRestart = true;
                entry = $"{layer.StateName} @ {layer.StartNormalizedTime:F3}（归一化）";
                query = pipeline.Matcher.Describe();''')
a=s.index('            string response ='); b=s.index('\n        }',a)
s=s[:a]+'''            string response = !timedReverse ? "" : observedRestart ? $"，改向至播放指令 {latency * 1000f:0} ms · {entry}" : "，尚无对应改向播放指令";
            string label = scenario == GroundReplayCase.DirectLeft ? "连续左转" :
                scenario == GroundReplayCase.DirectRight ? "连续右转" : ChangesDirection ? "制动中反向" : "直线急停";
            string travel = Direct ? "" : $"松键位移 {brakeTravel:0.000} m，";
            string samples = Direct ? directionSamples == 0 ? "无连续承重样本" :
                $"{directionSlip * 100f:0.0} cm（{directionSamples} 个采样间隔）" : drift;
            return $"{modeName} · {label} · {state}，{travel}双脚累计滑移 {samples}{response}" +
                (query == null ? "" : "\\n" + query);
'''+s[b:]
p.write_text(s)
p=r/'CharacterBehaviorAnimator.cs';s=p.read_text().replace('BeginReplay(bool reverse, bool shiftPhase)','BeginReplay(GroundReplayCase scenario, bool shiftPhase)').replace('new CharacterMatchReplay(transform.forward, reverse,','new CharacterMatchReplay(transform.forward, scenario,')
p.write_text(s)
p=Path('Assets/CharacterBehavior/Editor/CharacterBehaviorAnimatorEditor.cs');s=p.read_text().replace('BeginReplay(false, shiftPhase)','BeginReplay(GroundReplayCase.Stop, shiftPhase)').replace('animator.BeginReplay(true, shiftPhase);','''animator.BeginReplay(GroundReplayCase.ReverseAfterRelease, shiftPhase);
                        if (GUILayout.Button("回放持续跑动中直接左转")) animator.BeginReplay(GroundReplayCase.DirectLeft, shiftPhase);
                        if (GUILayout.Button("回放持续跑动中直接右转")) animator.BeginReplay(GroundReplayCase.DirectRight, shiftPhase);''')
s=s.replace('错开半个跑步周期松键','错开半个跑步周期触发')
p.write_text(s)

