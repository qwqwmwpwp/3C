from pathlib import Path
r=Path('Assets/CharacterBehavior/Runtime')
p=r/'CharacterClipMixer.cs';s=p.read_text()
s=s.replace('        public CharacterLayerObservation Observe()', '''        public float BlendedLength()
        {
            float length = 0f;
            foreach (var voice in voices) length += voice.Weight * voice.State.Clip.length;
            return length;
        }

        public CharacterLayerObservation Observe()''')
p.write_text(s)
p=r/'CharacterClipPlayback.cs';s=p.read_text().replace('using System.Collections.Generic;', 'using System.Collections.Generic;')
s=s.replace('plan.TransitionDuration * mixers[layer].Observe().DestinationLength;', 'plan.TransitionDuration * mixers[layer].BlendedLength();')
s=s.replace('            job.SetInputWeight(0, 1f);','            job.SetInputWeight(0, 1f);\n            job.SetProcessInputs(true);')
s=s.replace('            output.SetSourcePlayable(layers);\n            graph.Disconnect(job, 0);','            if (output.GetSourcePlayable().Equals((Playable)job)) output.SetSourcePlayable(layers);\n            if (job.GetInputCount() > 0) graph.Disconnect(job, 0);')
p.write_text(s)
# Keep upper/lower clock speeds tied to their individual clips even when the base switches to a one-shot.
p=r/'CharacterLocomotionPoseSelector.cs';s=p.read_text()
s=s.replace('            float? speed = motion.IsValid ? InputMath.NonNegative(motion.ActualSpeed, 0f) : last.Rates.StrideSpeed;\n            // 即使新入场片段维持原速，尚在淡出的走跑仍需自己的实际步频。','            // 新入场片段维持原速时，尚在淡出的走跑仍需自己的实际步频。\n            float? speed = motion.IsValid ? InputMath.NonNegative(motion.ActualSpeed, 0f) : last.Rates.StrideSpeed;')
p.write_text(s)

