using System;
using System.Reflection;
using ThreeC.CharacterBehavior;
using ThreeC.Player;
using UnityEngine;

// Offline runner uses the project's editor friend assembly and real fixed-step behavior flow.
internal static class ShieldComboRegression
{
    private static readonly AttackButton SkillButton = AttackButton.Skill;
    private static readonly CharacterAttackMove Light = CharacterAttackMove.LightOne;
    private static readonly CharacterAttackMove Heavy = CharacterAttackMove.Heavy;
    private static readonly CharacterAttackMove LightTwo = CharacterAttackMove.LightTwo;
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static void Expect(bool ok, string reason)
    {
        if (!ok) throw new Exception(reason);
    }

    private sealed class Fixture
    {
        internal readonly CharacterLightAttackSettings Settings = new CharacterLightAttackSettings();
        internal readonly CharacterComboRoutes Routes = new CharacterComboRoutes {
            Openers = new[] {
                new CharacterAttackLink(AttackButton.Light, Light),
                new CharacterAttackLink(AttackButton.Heavy, Heavy)
            },
            LightOneRoutes = new CharacterComboMoveRoutes {
                EarlySeconds = .15f, LinkStart = .6f, LinkEnd = .7f,
                Links = new[] {
                    new CharacterAttackLink(AttackButton.Light, LightTwo),
                    new CharacterAttackLink(SkillButton, Heavy)
                }
            }
        };
        internal readonly CharacterBehaviorFlow Flow;
        internal Fixture(float cooldown = 0f)
        {
            // Use the same detached clip metadata as the worker simulation (no Unity native calls).
            foreach (var def in new[]{Settings.Moves.LightOne, Settings.Moves.LightTwo, Settings.Moves.Heavy})
            {
                var t = def.GetType();
                t.GetField("captured",Fields).SetValue(def, true);
                t.GetField("hasClip",Fields).SetValue(def, true);
                t.GetField("clipDuration",Fields).SetValue(def, def == Settings.Moves.Heavy ? .1f : 1.5f);
            }
            Settings.Moves.Heavy.CooldownSeconds = cooldown;
            Settings.OpeningStepDistance = .25f;
            Flow = CharacterBehaviorFlow.ForCharacter(new CharacterBehaviorRegistration(false,Settings,Routes,null));
        }

        internal CharacterBehaviorFrameResult Tick(float dt, params AttackButton[] keys) =>
            TickVelocity(dt, default, keys);

        internal CharacterBehaviorFrameResult TickVelocity(float dt, Vector2 velocity, params AttackButton[] keys)
        {
            var presses = default(AttackButtonPresses);
            foreach (var key in keys) presses=presses.With(key);
            var input = new CharacterInputSnapshot(Vector2.right,false,false,0,0,presses);
            return Flow.Tick(input,Vector2.up,dt,null,velocity);
        }

        internal CharacterAttackMove Resolve(params AttackButton[] keys)
        {
            Tick(.8f,AttackButton.Light);
            return Tick(.11f,keys).Attack.Move;
        }
    }

    private static void Cooldown(float readyAt, CharacterAttackMove expected)
    {
        // Heavy starts at t=0 and ends at .10. Light then opens at 1.00 absolute.
        var f = new Fixture(readyAt);
        f.Tick(.10f,AttackButton.Heavy);
        f.Tick(.78f,AttackButton.Light);
        f.Tick(.02f,AttackButton.Light);
        var result=f.Tick(.11f,SkillButton);
        Expect(result.Attack.Move==expected, $"expected {expected}, got {result.Attack.Move}");
    }

    private static void Window(float pressTime, CharacterAttackMove expected)
    {
        var f = new Fixture();
        f.Tick(pressTime,AttackButton.Light);
        var result=f.Tick(Math.Max(.02f,.91f-pressTime),SkillButton);
        Expect(result.Attack.Move==expected, $"press at {pressTime}: got {result.Attack.Move}");
    }

    private static int Main()
    {
        int failures=0;
        void Test(string name,Action test)
        {
            try { test(); Console.WriteLine("PASS "+name); }
            catch(Exception e) { failures++; Console.WriteLine("FAIL "+name+" - "+e.GetBaseException().Message); }
        }
        Test("last legal key wins within one tick",()=>Expect(new Fixture().Resolve(AttackButton.Light,SkillButton)==Heavy,"first key won"));
        Test("repeated key wins back slot",()=>Expect(new Fixture().Resolve(AttackButton.Light,SkillButton,AttackButton.Light)==LightTwo,"lost repeated press"));
        Test("unrelated input leaves candidate",()=>Expect(new Fixture().Resolve(AttackButton.Light,AttackButton.Heavy)==LightTwo,"lost candidate"));
        Test("ready before link replaces",()=>Cooldown(.98f,Heavy));
        Test("ready exactly at link replaces",()=>Cooldown(1f,Heavy));
        Test("ready after link retains old candidate",()=>Cooldown(1.04f,LightTwo));
        Test("early input discarded",()=>Window(.74f,Light));
        Test("buffer start inclusive",()=>Window(.75f,Heavy));
        Test("link start inclusive",()=>Window(.90f,Heavy));
        Test("link end inclusive",()=>Window(1.05f,Heavy));
        Test("late input discarded",()=>Window(1.06f,Light));
        Test("empty slot rejects cooldown",()=>{
            var f=new Fixture(2f); f.Tick(.1f,AttackButton.Heavy); f.Tick(.80f,AttackButton.Light);
            Expect(f.Tick(.11f,SkillButton).Attack.Move==Light,"unavailable skill started");
        });
        Test("interrupted buffer is cleared",()=>{
            var f=new Fixture(); f.Tick(.8f,AttackButton.Light); f.Tick(.02f,SkillButton);
            f.Flow.ClearAttackBuffer(); Expect(f.Tick(.1f).Attack.Move==Light,"stale input executed");
        });
        Test("standing start ignores newly pressed direction",()=>{
            var f=new Fixture(); var r=f.Tick(.12f,AttackButton.Light);
            Expect(r.Displacement.sqrMagnitude<.000001f,"standing opening moved "+r.Displacement);
        });
        Test("moving entry integrates one step along actual velocity",()=>{
            var f=new Fixture(); var r=f.TickVelocity(.36f,Vector2.up*3f,AttackButton.Light);
            Expect(Math.Abs(r.Displacement.y-.25f)<.0001f && Math.Abs(r.Displacement.x)<.0001f,"incorrect opening distance/direction");
        });
        Test("chaining does not create a second movement step",()=>{
            var f=new Fixture(); f.TickVelocity(.8f,Vector2.up*3,AttackButton.Light);
            var r=f.TickVelocity(.2f,Vector2.up*3,AttackButton.Light);
            Expect(Math.Abs(r.Displacement.y)<.0001f,"repeated opening step");
        });
        Test("all branches use the source move window", () => {
            foreach (var button in new[] { AttackButton.Light, AttackButton.Heavy, SkillButton })
            {
                var f = new Fixture();
                var source = f.Routes.LightOneRoutes;
                source.Links = new[] {
                    new CharacterAttackLink(AttackButton.Light, LightTwo),
                    new CharacterAttackLink(AttackButton.Heavy, Heavy),
                    new CharacterAttackLink(SkillButton, Heavy)
                };
                source.LinkStart = .4f;
                source.LinkEnd = .5f;
                source.EarlySeconds = .1f;
                f.Tick(.49f, AttackButton.Light);
                Expect(f.Tick(.01f, button).Attack.BufferedMove == 0, "early input accepted: " + button);
                var target = button == AttackButton.Light ? LightTwo : Heavy;
                Expect(f.Tick(.05f, button).Attack.BufferedMove == target, "branch missed buffer: " + button);
                Expect(f.Tick(.06f).Attack.Move == target, "branch missed common open time: " + button);
            }
        });
        Test("second move uses its own clock and window", () => {
            var f = new Fixture();
            f.Routes.LightTwoRoutes = new CharacterComboMoveRoutes {
                EarlySeconds = 0f, LinkStart = .25f, LinkEnd = .5f,
                Links = new[] { new CharacterAttackLink(AttackButton.Heavy, Heavy) }
            };
            f.Tick(.9f, AttackButton.Light);
            Expect(f.Tick(.6f, AttackButton.Light).Attack.Move == LightTwo, "light two not entered");
            Expect(f.Tick(.01f, AttackButton.Heavy).Attack.Move == Heavy, "used light one's time");
        });
        Test("invalid or absent timing rejects followup but allows opening", () => {
            foreach (var source in new[] {
                new CharacterComboMoveRoutes(),
                new CharacterComboMoveRoutes { EarlySeconds = -.1f, LinkStart = .6f, LinkEnd = .7f },
                new CharacterComboMoveRoutes { LinkStart = .8f, LinkEnd = .7f }
            })
            {
                var f = new Fixture();
                source.Links = f.Routes.LightOneRoutes.Links;
                f.Routes.LightOneRoutes = source;
                Expect(f.Tick(.9f, AttackButton.Light).Attack.Move == Light, "invalid timing blocked opener");
                var state = f.Tick(.02f, SkillButton).Attack;
                Expect(state.Move == Light && state.BufferedMove == 0 && state.BufferResult.Contains("无效"),
                    "accepted malformed timing");
            }
        });
        Test("forecast owns a detached copy of move timings and links", () => {
            var f = new Fixture();
            f.Tick(.8f, AttackButton.Light);
            var sim = CharacterBehaviorFlow.ForCharacter(new CharacterBehaviorRegistration(false, f.Settings, f.Routes, null), true);
            f.Flow.CopyStateTo(sim);
            f.Routes.LightOneRoutes.Links[1] = new CharacterAttackLink(SkillButton, LightTwo);
            f.Routes.LightOneRoutes.LinkStart = .68f;
            var input = new CharacterInputSnapshot(default, false, false, 0, 0, default(AttackButtonPresses).With(SkillButton));
            Expect(sim.Tick(input, Vector2.up, .11f, null).Attack.Move == Heavy, "snapshot followed live edits");
            Expect(f.Tick(.11f, SkillButton).Attack.Move == Light, "live used old timing");
            Expect(f.Tick(.12f, SkillButton).Attack.Move == LightTwo, "live missed edited route");
        });
        Test("wrong opening condition preserves ordinary combo",()=>{
            var f=new Fixture(); var row=f.Routes.LightOneRoutes.Links[1];
            row.OpeningCondition=CharacterComboOpeningCondition.Moving; f.Routes.LightOneRoutes.Links[1]=row;
            Expect(f.Resolve(AttackButton.Light,SkillButton)==LightTwo,"invalid condition replaced a valid input");
        });
        Test("moving route remembers entry after stopping",()=>{
            var f=new Fixture(); var row=f.Routes.LightOneRoutes.Links[1];
            row.OpeningCondition=CharacterComboOpeningCondition.Moving; f.Routes.LightOneRoutes.Links[1]=row;
            f.TickVelocity(.8f,Vector2.up*3,AttackButton.Light);
            Expect(f.Tick(.11f,SkillButton).Attack.Move==Heavy,"route lost moving entry");
        });
        Test("forecast copies pending action without consuming live input",()=>{
            var f=new Fixture(); f.Tick(.8f,AttackButton.Light); f.Tick(.02f,SkillButton);
            var sim=CharacterBehaviorFlow.ForCharacter(new CharacterBehaviorRegistration(false,f.Settings,f.Routes,null),true);
            f.Flow.CopyStateTo(sim);
            Expect(sim.Tick(default,Vector2.up,.1f,null).Attack.Move==Heavy,"copy lost buffered input");
            Expect(f.Tick(.1f).Attack.Move==Heavy,"forecast consumed live input");
        });
        Test("forecast copies cooldown and cannot spend the live cooldown",()=>{
            var f=new Fixture(2f); f.Tick(.1f,AttackButton.Heavy); f.Tick(.8f,AttackButton.Light);
            var sim=CharacterBehaviorFlow.ForCharacter(new CharacterBehaviorRegistration(false,f.Settings,f.Routes,null),true);
            f.Flow.CopyStateTo(sim);
            var press=new CharacterInputSnapshot(default,false,false,0,0,default(AttackButtonPresses).With(SkillButton));
            Expect(sim.Tick(press,Vector2.up,.11f,null).Attack.Move==Light,"copy ignored cooldown");
            sim.Reset();
            Expect(f.Tick(.11f,SkillButton).Attack.Move==Light,"copy reset live cooldown");
        });
        Test("interrupt by dash never spends buffered cooldown",()=>{
            var f=new Fixture(3f); f.Tick(.89f,AttackButton.Light); f.Tick(.005f,SkillButton);
            var dash=f.Flow.Tick(new CharacterInputSnapshot(Vector2.right,true),Vector2.up,.02f,null);
            Expect(dash.DashStarted && dash.Attack.Move==0,"dash failed to interrupt");
            f.Tick(.5f);
            Expect(f.Tick(.02f,AttackButton.Heavy).Attack.Move==Heavy,"cancelled input consumed cooldown");
        });
        Test("inside open window no waiting for late cooldown",()=>{
            var f=new Fixture(1.08f); f.Tick(.1f,AttackButton.Heavy); f.Tick(.96f,AttackButton.Light);
            Expect(f.Tick(.01f,SkillButton).Attack.Move==Light,"premature skill");
            f.Tick(.02f);
            Expect(f.Tick(.01f,SkillButton).Attack.Move==Heavy,"new ready press lost");
        });
        Test("early skill overwritten by light spends no cooldown",()=>{
            var f=new Fixture(3f); f.Tick(.78f,AttackButton.Light); f.Tick(.02f,SkillButton);
            Expect(f.Tick(.11f,AttackButton.Light).Attack.Move==LightTwo,"light failed to override skill");
            f.Tick(1.5f);
            Expect(f.Tick(.02f,AttackButton.Heavy).Attack.Move==Heavy,"overwritten input spent cooldown");
        });
        Test("only attack one can activate the authored skill",()=>{
            var f=new Fixture();
            var skill=CharacterAttackMove.SkillTwo;
            f.Settings.Moves.Additional=new[]{new CharacterAttackMoveEntry {Id=skill, Parameters=f.Settings.Moves.Heavy}};
            f.Routes.LightOneRoutes.Links[1]=new CharacterAttackLink(SkillButton,skill);
            Expect(f.Tick(.02f,SkillButton).Attack.Move==0,"skill started from idle");
            Expect(f.Resolve(SkillButton)==skill,"additional move definition not resolved");
        });
        Test("opening keeps horizontal heading on an oblique slope",()=>{
            var plane=new Vector3(-.5f,.7071068f,-.5f);
            var actual=CharacterMotorStep.GroundTarget(Vector3.right*2f,plane,true);
            Expect(Math.Abs(actual.z)<.00001f && actual.x>0f,"opening veered sideways: "+actual.z);
            Expect(Math.Abs(Vector3.Dot(actual,plane))<.00001f && Math.Abs(actual.magnitude-2f)<.00001f,
                "incorrect distance or ground tangent");
        });
        Test("invalid opening reports invalid settings",()=>{
            var f=new Fixture(); f.Settings.Moves.LightOne.StrikeStart=0f;
            Expect(f.Tick(.1f,AttackButton.Light).GetRejection(CharacterBehaviorId.Attack)==
                CharacterBehaviorRejection.InvalidSettings,"misconfigured move silently ignored");
        });
        Test("cooldown opening reports cooldown",()=>{
            var f=new Fixture(2f); f.Tick(.1f,AttackButton.Heavy);
            Expect(f.Tick(.1f,AttackButton.Heavy).GetRejection(CharacterBehaviorId.Attack)==
                CharacterBehaviorRejection.Cooldown,"missing cooldown rejection");
        });
        Test("condition rejection explains why old candidate survived",()=>{
            var f=new Fixture(); var row=f.Routes.LightOneRoutes.Links[1];
            row.OpeningCondition=CharacterComboOpeningCondition.Moving; f.Routes.LightOneRoutes.Links[1]=row;
            f.Tick(.78f,AttackButton.Light); f.Tick(.02f,AttackButton.Light);
            var r=f.Tick(.02f,SkillButton);
            Expect(r.Attack.BufferedMove==LightTwo && r.Attack.BufferResult.Contains("条件"),"stale decision");
        });
        Test("zero step distance suppresses regular startup movement",()=>{
            var f=new Fixture(); f.Settings.OpeningStepDistance=0f;
            var result=f.TickVelocity(.12f,Vector2.up*3f,AttackButton.Light);
            Expect(result.Displacement.sqrMagnitude<.000001f,"zero step still moved "+result.Displacement);
        });
        Test("blackboard retains each physical press in order",()=>{
            var board=new CharacterInputBlackboard();
            board.PressAttack(AttackButton.Light); board.PressAttack(SkillButton); board.PressAttack(AttackButton.Light);
            var presses=board.Consume().AttackPresses;
            Expect(presses.Count==3 && presses[0]==AttackButton.Light && presses[1]==SkillButton &&
                presses[2]==AttackButton.Light && board.Consume().AttackPresses.Count==0,"lost or repeated press");
        });
        Test("live and forecast use the final route's blend",()=>{
            var f=new Fixture(); var heavy=f.Routes.LightOneRoutes.Links[1]; heavy.BlendInEnd=.25f; heavy.BlendOutStart=.8f;
            f.Routes.LightOneRoutes.Links[1]=heavy;
            f.Tick(.78f,AttackButton.Light); f.Tick(.02f,SkillButton);
            var sim=CharacterBehaviorFlow.ForCharacter(new CharacterBehaviorRegistration(false,f.Settings,f.Routes,null),true);
            f.Flow.CopyStateTo(sim);
            var light=new CharacterInputSnapshot(default,false,false,0,0,default(AttackButtonPresses).With(AttackButton.Light));
            var predicted=sim.Tick(light,Vector2.up,.11f,null).Attack;
            var live=f.Tick(.11f,AttackButton.Light).Attack;
            foreach (var state in new[]{live,predicted})
                Expect(state.Move==LightTwo && Math.Abs(state.Handoff.FullFrom-.1f)<.0001f &&
                    Math.Abs(state.Handoff.ReleaseFrom-.9f)<.0001f,"continued using superseded route blend");
        });
        Test("appended move can open, link out and be predicted from an independent snapshot", () =>
        {
            var f = new Fixture();
            var extra = (CharacterAttackMove)5;
            f.Settings.Moves.Additional = new[] {
                new CharacterAttackMoveEntry { Id = extra, Parameters = f.Settings.Moves.LightOne }
            };
            f.Routes.Openers = new[] { new CharacterAttackLink(SkillButton, extra) };
            var outgoing = new CharacterComboMoveRoutes {
                LinkStart = .4f, LinkEnd = .8f,
                Links = new[] { new CharacterAttackLink(AttackButton.Heavy, Heavy) }
            };
            f.Routes.Additional = new[] { new CharacterComboRouteEntry { Id = extra, Routes = outgoing } };
            Expect(f.Tick(.61f, SkillButton).Attack.Move == extra, "additional opener rejected");
            var registration = new CharacterBehaviorRegistration(false, f.Settings, f.Routes, null);
            var forecast = CharacterBehaviorFlow.ForCharacter(registration, snapshot: true);
            f.Flow.CopyStateTo(forecast);
            var input = new CharacterInputSnapshot(default, false, false, 0, 0, default(AttackButtonPresses).With(AttackButton.Heavy));
            var actual = f.Tick(.02f, AttackButton.Heavy).Attack;
            outgoing.Links[0] = default;
            var predicted = forecast.Tick(input, Vector2.up, .02f, null).Attack;
            Expect(actual.Move == Heavy && predicted.Move == actual.Move && predicted.InstanceId == actual.InstanceId,
                "additional route lost or aliased in prediction");
        });
        return failures;
    }
}
