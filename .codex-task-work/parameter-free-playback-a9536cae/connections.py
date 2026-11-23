from pathlib import Path
p=Path('Assets/CharacterBehavior/Runtime/CharacterClipPlayback.cs')
s=p.read_text().replace('using System.Collections.Generic;', 'using System;\nusing System.Collections.Generic;')
s=s.replace('''            job.SetInputCount(1);
            graph.Connect(layers, 0, job, 0);''','''            job.SetInputCount(1);
            output.SetSourcePlayable(Playable.Null);
            if (!graph.Connect(layers, 0, job, 0))
            {
                output.SetSourcePlayable(layers);
                throw new InvalidOperationException("无法将分层播放输出接入最终 IK。");
            }''')
s=s.replace('''            if (output.GetSourcePlayable().Equals((Playable)job)) output.SetSourcePlayable(layers);
            if (job.GetInputCount() > 0) graph.Disconnect(job, 0);
            graph.DestroyPlayable(job);''','''            bool connected = output.GetSourcePlayable().Equals((Playable)job);
            if (connected) output.SetSourcePlayable(Playable.Null);
            if (job.GetInputCount() > 0 && job.GetInput(0).IsValid()) graph.Disconnect(job, 0);
            if (connected || !output.GetSourcePlayable().IsValid()) output.SetSourcePlayable(layers);
            graph.DestroyPlayable(job);''')
p.write_text(s)

