using System;
using System.Globalization;
using ThreeC.Player;
using UnityEngine;
namespace ThreeC.CharacterBehavior
{
    // Offline fixture supplies only the native clip properties used by the behavior layer.
    internal sealed class AnimationClip
    {
        public float length = 1f;
        public bool isLooping;
        public static implicit operator bool(AnimationClip clip) => clip != null;
    }
    internal static class AttackRegression
    {
        private static readonly CharacterMovementSettings movement = new CharacterMovementSettings();
        private static int steps, checks;
        private static readonly Vector2 dir = Vector2.right;
        private static CharacterLightAttackSettings Settings(bool hasClip = true)
        {
            var s = new CharacterLightAttackSettings();
            s.Combo.AllowWithoutClip = !hasClip;
            s.Combo.LightSeconds = s.Combo.HeavySeconds = 1f;
            s.StrikeTravel = .3f;
            s.Combo.LightTwo.StrikeStart = .16f;
            s.Combo.LightTwo.RecoveryStart = .50f;
            s.Combo.LightTwo.StrikeTravel = .12f;
            s.Combo.Heavy.StrikeStart = .32f;
            s.Combo.Heavy.RecoveryStart = .70f;
            if (hasClip) s.Clip = s.Combo.LightTwo.Clip = s.Combo.Heavy.Clip = new AnimationClip();
            return s;
        }
        static CharacterBehaviorFlow Flow() => CharacterBehaviorFlow.ForCharacter(true, Settings());
        static CharacterBehaviorFrameResult Step(CharacterBehaviorFlow f, float dt, Vector2 direction,
            AttackButton button = AttackButton.None, bool dash = false, bool guard = false, CharacterMoveGait gait = CharacterMoveGait.Run)
        {
            var presses = default(AttackButtonPresses).With(button);
            var snapshot = new CharacterInputSnapshot(direction,dash,guard,CharacterBehaviorId.None,gait,presses);
            var r = f.Tick(snapshot,Vector2.up,dt,movement);
            Console.WriteLine(Row(r));
            steps++;
            return r;
        }
        static string Row(CharacterBehaviorFrameResult r)
        {
            var a=r.Attack; var m=r.ActionMotion;
            var fields = new object[] {r.FrameNumber,(ulong)r.ActiveBehaviors,(ulong)r.StartedBehaviors,(ulong)r.ExecutedBehaviors,
                (ulong)r.EndedBehaviors,(ulong)r.InterruptedBehaviors,(ulong)r.WithdrawnBehaviors,(ulong)r.DisplacementProvider,
                (int)r.DisplacementSource,r.MoveApproved,r.MoveStarted,r.MoveEnded,r.AttackMoveStarted,r.HasTransitions,
                (int)a.Move,a.InstanceId,(int)a.Phase,a.StartProgress,a.Progress,a.Duration,a.Handoff.MoveStart,
                r.Displacement.x,r.Displacement.y,r.DashDirection.x,r.DashDirection.y,r.StaminaRemaining,r.DashCooldownRemaining,
                r.OrdinarySpeedLimit,(int)r.GetRejection(CharacterBehaviorId.Move),(int)r.GetRejection(CharacterBehaviorId.Attack),
                (int)r.GetRejection(CharacterBehaviorId.Dash),(int)r.GetRejection(CharacterBehaviorId.Guard),
                m.StartPhase,m.EndPhase,m.Duration,m.MaxTurn,m.Braking,m.ControlsFacing,m.RecoversMovement,m.Facing.x,m.Facing.y};
            return string.Join("|",Array.ConvertAll(fields,x=> x is float v ? v.ToString("R",CultureInfo.InvariantCulture) : x?.ToString() ?? "-"));
        }
        static void Check(bool v,string why) { if(!v)throw new Exception(why); checks++; }
        static bool Near(float x,float y) => Math.Abs(x-y)<1e-5f;
        static void Concurrent()
        {
            var f=Flow(); Step(f,.02f,dir,guard:true);
            var won=Step(f,.02f,dir,AttackButton.Light,true,true);
            Check(won.DashStarted && won.InterruptedBehaviors.HasFlag(CharacterBehaviorId.Guard) &&
                !won.AttackMoveStarted && Near(won.StaminaRemaining,75),"simultaneous dash victory");
            var end=Step(f,.21f,dir,guard:true);
            Check(end.DashEnded && !end.DashActive && !end.MoveApproved && !end.StartedBehaviors.HasFlag(CharacterBehaviorId.Guard),"short final dash step");
            var shield=Step(f,.01f,dir,guard:true);
            Check(shield.MoveApproved && shield.StartedBehaviors.HasFlag(CharacterBehaviorId.Guard),"held guard resumes");
            var denied=Step(f,.01f,dir,AttackButton.Light,true,true);
            Check(!denied.DashStarted && denied.AttackMoveStarted && denied.GetRejection(CharacterBehaviorId.Dash)==CharacterBehaviorRejection.Cooldown &&
                denied.InterruptedBehaviors.HasFlag(CharacterBehaviorId.Guard) && Near(denied.StaminaRemaining,75),"rejected dash leaves attack available");
        }
        static void Phases()
        {
            var f=Flow(); Step(f,.57f,dir,AttackButton.Light);
            var crossing=Step(f,.02f,dir,dash:true);
            Check(crossing.GetRejection(CharacterBehaviorId.Dash)==CharacterBehaviorRejection.InterruptPhaseDenied &&
                crossing.Attack.Phase==CharacterBehaviorPhase.Recovery && crossing.MoveApproved && crossing.Displacement.x>0,"57 to 59 percent");
            var cancel=Step(f,.02f,dir,dash:true);
            Check(cancel.DashStarted && cancel.InterruptedBehaviors.HasFlag(CharacterBehaviorId.Attack) && !cancel.AttackMoveStarted,"recovery cancel");
            f=Flow(); Step(f,.1f,dir,AttackButton.Light);
            Check(Step(f,.01f,dir,dash:true).DashStarted,"startup cancel");
            f=Flow(); var late=Step(f,.72f,Vector2.zero,AttackButton.Light);
            Check(!late.Attack.Handoff.MoveStart.HasValue,"no premature move timer");
            var released=Step(f,.04f,dir);
            Check(Near(released.Attack.Handoff.MoveStart.Value,.72f),"late recovery timer");
            var ended=Step(f,.3f,dir);
            Check(ended.EndedBehaviors.HasFlag(CharacterBehaviorId.Attack) && ended.ExecutedBehaviors.HasFlag(CharacterBehaviorId.Attack) &&
                !ended.ActiveBehaviors.HasFlag(CharacterBehaviorId.Attack) && Near(ended.Attack.Progress,1f),"final attack state");
        }
        static void Links()
        {
            foreach(float press in new[]{.399f,.4f,.58f,.85f,.851f})
            {
                var f=Flow(); Step(f,press,dir,AttackButton.Light);
                var next=Step(f,.21f,dir,AttackButton.Light);
                Check((next.Attack.Move==CharacterAttackMove.LightTwo)==(press>=.4f && press<=.85f),"window "+press);
            }
            var s=Settings(); var live=CharacterBehaviorFlow.ForCharacter(true,s);
            Step(live,.5f,dir,AttackButton.Light); Step(live,.05f,dir,AttackButton.Light);
            var copy=CharacterBehaviorFlow.ForCharacter(true,s); live.CopyStateTo(copy);
            var a=Step(live,.06f,dir); var b=Step(copy,.06f,dir);
            Check(Row(a)==Row(b) && a.AttackMoveStarted && a.Attack.InstanceId==2 && Near(a.Attack.Progress,.03f),"buffer copied, split time");
            Check(Near(a.Displacement.x,movement.MoveSpeed*.03f*(1f-.03f/(2f*.16f))),"second attack ordinary share");
            Step(live,.41f,dir); var heavy=Step(live,.15f,dir,AttackButton.Heavy);
            Check(heavy.Attack.Move==CharacterAttackMove.Heavy && heavy.Attack.InstanceId==3 && heavy.AttackMoveStarted,"light light heavy");
            var independent=Step(copy,.04f,dir);
            Check(independent.Attack.InstanceId==2 && Near(independent.Attack.Progress,.07f),"forecast state isolated");
            live=Flow(); Step(live,.5f,dir,AttackButton.Light); Step(live,.04f,dir,AttackButton.Light);
            live.ClearAttackBuffer(); Check(Step(live,.09f,dir).Attack.Move==CharacterAttackMove.LightOne,"buffer flush");
            var zero=Step(live,0f,dir,AttackButton.Heavy,true);
            Check(zero.FrameNumber==3 && !zero.AttackMoveStarted && !zero.DashStarted && Near(zero.Attack.Progress,.63f),"zero step");
            live.Reset(movement); var idle=Step(live,.01f,Vector2.zero);
            Check(idle.FrameNumber==1 && idle.Attack.Move==CharacterAttackMove.None && Near(idle.StaminaRemaining,100),"reset");
        }
        static void Mixed()
        {
            foreach(bool clips in new[]{false,true})
            {
                var random=new System.Random(1729);
                for(int run=0; run<64; run++)
                {
                    var flow=CharacterBehaviorFlow.ForCharacter(true,Settings(clips));
                    for(int i=0;i<80;i++)
                    {
                        var t=new[]{.005f,.016f,.02f,.071f,.125f,.25f,.575f}[random.Next(7)];
                        var direction = new[]{Vector2.zero,dir,Vector2.up,Vector2.left, new Vector2(.3f,-.4f)}[random.Next(5)];
                        var button = (AttackButton)Math.Min(2, Math.Max(0,random.Next(8)-5));
                        Step(flow,t,direction,button,dash:random.Next(19)==0,guard:random.Next(3)==0,gait:(CharacterMoveGait)random.Next(3));
                    }
                }
            }
        }
        public static void Main() { CultureInfo.CurrentCulture=CultureInfo.InvariantCulture; Concurrent(); Phases(); Links(); Mixed(); Console.Error.WriteLine($"STATS {steps} frames, {checks} boundary assertions"); }
    }
}
