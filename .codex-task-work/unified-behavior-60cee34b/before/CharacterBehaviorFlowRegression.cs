using System;
using ThreeC.CharacterBehavior;
using UnityEngine;

internal static class CharacterBehaviorFlowRegression
{
    private const float Step = 0.25f;
    private const float GuardSpeed = 2f;

    public static void Main()
    {
        MoveAndIdle();
        DashExecutesThroughFinalStep();
        SingleStepDashStartsAndEndsTogether();
        RejectedDashPreservesGuardAndMovement();
        ApprovedDashInterruptsGuard();
        SameStepRequestsFollowConflictRules();
        GuardGateDeniesDash(CharacterBehaviorGateMode.Blacklist, CharacterBehaviorId.Dash);
        GuardGateDeniesDash(CharacterBehaviorGateMode.Whitelist, CharacterBehaviorId.Move);
        InterruptChecksHolderPhase();
        GuardWhitelistControlsEntry();
        GuardGateDoesNotStopAnExistingDash();
        ConcurrentLimitsAndGatesCompose();
        SameStepRequestsShareStamina();
        AdditionalDisplacementRequiresMoveInterrupt();
        ConflictingRulesRequireDistinctPriorities();
        ResetClearsActiveBehaviorsAndResources();
        SharedSettingsKeepCharacterStateIndependent();
        Console.WriteLine("Character behavior flow regression checks passed.");
    }

    private static void MoveAndIdle()
    {
        var flow = new CharacterBehaviorFlow();
        var settings = new CharacterMovementSettings();
        var idle = Tick(flow, settings, Vector2.zero);
        Expect(!idle.MoveApproved && idle.DisplacementSource == CharacterDisplacementSource.None,
            "No direction leaves the character idle.");
        ExpectDisplacement(idle, Vector2.zero, "Idle step");

        var move = Tick(flow, settings, Vector2.right * 0.5f);
        Expect(move.MoveApproved && move.MoveStarted && move.DisplacementProvider == CharacterBehaviorId.Move,
            "The first nonzero input starts ordinary movement.");
        ExpectDisplacement(move, Vector2.right * (settings.MoveSpeed * Step * 0.5f), "Half-strength movement input");

        var steady = Tick(flow, settings, Vector2.up);
        Expect(steady.MoveApproved && !steady.MoveStarted, "Changing ordinary direction does not restart movement.");
        ExpectDisplacement(steady, Vector2.up * (settings.MoveSpeed * Step), "Held movement with a new direction");

        var stopped = Tick(flow, settings, Vector2.zero);
        Expect(stopped.MoveEnded && !stopped.MoveApproved, "Releasing movement produces the legacy end edge.");
        ExpectDisplacement(stopped, Vector2.zero, "Released movement");
    }

    private static void DashExecutesThroughFinalStep()
    {
        var flow = new CharacterBehaviorFlow();
        var settings = new CharacterMovementSettings { DashSpeed = 8f, DashDuration = 0.625f, DashCooldown = 1f };
        Tick(flow, settings, Vector2.up);

        var start = Tick(flow, settings, Vector2.right, dash: true);
        Expect(start.DashApproved && start.DashStarted && start.DashActive && start.MoveEnded && !start.MoveApproved,
            "Approved dash starts immediately and takes over displacement from movement.");
        ExpectDisplacement(start, Vector2.right * 2f, "Dash start step");
        float spentStamina = settings.InitialStamina - settings.DashStaminaCost;
        ExpectNear(start.StaminaRemaining, spentStamina, "Dash start consumes stamina");
        ExpectNear(start.DashCooldownRemaining, 1f, "Dash start sets cooldown");

        settings.DashSpeed = 4f;
        var ongoing = Tick(flow, settings, Vector2.up, dash: true);
        Expect(ongoing.DashActive && !ongoing.DashStarted && !ongoing.DashEnded,
            "Repeated dash input leaves the existing dash executing.");
        ExpectNear(ongoing.StaminaRemaining, spentStamina, "An ongoing dash does not spend stamina again");
        ExpectNear(ongoing.DashCooldownRemaining, 0.75f, "Cooldown advances during dash");
        ExpectDisplacement(ongoing, Vector2.right * 2f, "Dash retains its initial direction and speed");

        var final = Tick(flow, settings, Vector2.up);
        Expect(final.DashEnded && !final.DashActive && !final.MoveApproved &&
            (final.ExecutedBehaviors & CharacterBehaviorId.Dash) != 0 &&
            (final.ActiveBehaviors & CharacterBehaviorId.Dash) == 0 &&
            final.DisplacementProvider == CharacterBehaviorId.Dash,
            "The final dash step provides displacement even though committed state is inactive.");
        ExpectDisplacement(final, Vector2.right, "Partial final dash step");

        var cooling = Tick(flow, settings, Vector2.up, dash: true);
        Expect(cooling.MoveStarted && !cooling.DashStarted &&
            cooling.GetRejection(CharacterBehaviorId.Dash) == CharacterBehaviorRejection.Cooldown,
            "Movement resumes on the next step while dash is on cooldown.");
        ExpectNear(cooling.StaminaRemaining, spentStamina, "A cooldown rejection preserves stamina");

        var restart = Tick(flow, settings, Vector2.up, dash: true);
        Expect(restart.DashStarted, "Dash can restart on the step that exhausts cooldown.");
        ExpectDisplacement(restart, Vector2.up, "Restarted dash uses the new direction and speed");
    }

    private static void SingleStepDashStartsAndEndsTogether()
    {
        var flow = new CharacterBehaviorFlow();
        var settings = new CharacterMovementSettings { DashSpeed = 8f, DashDuration = 0.125f };
        var frame = Tick(flow, settings, Vector2.zero, dash: true);
        Expect(frame.DashStarted && frame.DashEnded && !frame.DashActive &&
            (frame.StartedBehaviors & CharacterBehaviorId.Dash) != 0 &&
            (frame.EndedBehaviors & CharacterBehaviorId.Dash) != 0 &&
            (frame.ExecutedBehaviors & CharacterBehaviorId.Dash) != 0 &&
            frame.DisplacementSource == CharacterDisplacementSource.Dash,
            "A dash shorter than a fixed step starts, executes and ends on that step.");
        ExpectDisplacement(frame, Vector2.up, "Single-step dash uses facing as its fallback direction");
    }

    private static void RejectedDashPreservesGuardAndMovement()
    {
        var flow = GuardFlow(interrupt: CharacterBehaviorPhase.Any);
        var settings = new CharacterMovementSettings { InitialStamina = 4f, DashStaminaCost = 5f };
        var raised = Tick(flow, settings, Vector2.zero, guard: true);
        Expect((raised.StartedBehaviors & CharacterBehaviorId.Guard) != 0 && !raised.MoveApproved,
            "Guard can start while standing still.");
        var walking = Tick(flow, settings, Vector2.right, guard: true);
        Expect(walking.MoveStarted, "Ordinary movement can start under an existing guard.");
        ExpectDisplacement(walking, Vector2.right * (GuardSpeed * Step), "Movement while guarding");

        var rejected = Tick(flow, settings, Vector2.right, dash: true, guard: true);
        Expect(!rejected.DashStarted && rejected.GetRejection(CharacterBehaviorId.Dash) == CharacterBehaviorRejection.InsufficientStamina &&
            (rejected.ActiveBehaviors & CharacterBehaviorId.Guard) != 0 &&
            (rejected.EndedBehaviors & CharacterBehaviorId.Guard) == 0 &&
            rejected.MoveApproved && !rejected.MoveStarted && !rejected.MoveEnded,
            "A stamina rejection preserves the existing guard and movement without emitting new edges.");
        ExpectNear(rejected.StaminaRemaining, settings.InitialStamina, "A rejected dash preserves stamina");
        ExpectNear(rejected.DashCooldownRemaining, 0f, "A rejected dash does not set cooldown");
        ExpectNear(rejected.OrdinarySpeedLimit, GuardSpeed, "Guard caps ordinary movement speed");
        ExpectDisplacement(rejected, Vector2.right * (GuardSpeed * Step), "Held guard movement");

        var released = Tick(flow, settings, Vector2.right);
        Expect((released.EndedBehaviors & CharacterBehaviorId.Guard) != 0 &&
            (released.InterruptedBehaviors & CharacterBehaviorId.Guard) == 0,
            "Releasing guard ends it without interruption.");
        ExpectDisplacement(released, Vector2.right * (settings.MoveSpeed * Step), "Movement after releasing guard");
    }

    private static void ApprovedDashInterruptsGuard()
    {
        var flow = GuardFlow(interrupt: CharacterBehaviorPhase.Active);
        var settings = new CharacterMovementSettings { DashDuration = 0.375f };
        Tick(flow, settings, Vector2.up, guard: true);

        var dash = Tick(flow, settings, Vector2.right, dash: true, guard: true);
        Expect(dash.DashStarted && (dash.InterruptedBehaviors & CharacterBehaviorId.Guard) != 0 &&
            (dash.EndedBehaviors & CharacterBehaviorId.Guard) != 0 &&
            (dash.ActiveBehaviors & CharacterBehaviorId.Guard) == 0 && !dash.MoveApproved,
            "An approved dash interrupts the active guard and suppresses ordinary displacement.");
        ExpectDisplacement(dash, Vector2.right * (settings.DashSpeed * Step), "Dash after interrupting guard");

        var final = Tick(flow, settings, Vector2.up, guard: true);
        Expect(final.DashEnded && (final.StartedBehaviors & CharacterBehaviorId.Guard) == 0,
            "The held guard waits until the dash's final executed step has passed.");

        var resumed = Tick(flow, settings, Vector2.up, guard: true);
        Expect((resumed.StartedBehaviors & CharacterBehaviorId.Guard) != 0 && resumed.MoveApproved,
            "Still-held guard can start again on the step after dash completion.");
        ExpectDisplacement(resumed, Vector2.up * (GuardSpeed * Step), "Guard movement after dash");
    }

    private static void SameStepRequestsFollowConflictRules()
    {
        var settings = new CharacterMovementSettings { DashDuration = 0.375f };
        var exclusive = Tick(GuardFlow(), settings, Vector2.right, dash: true, guard: true);
        Expect(exclusive.DashStarted && (exclusive.StartedBehaviors & CharacterBehaviorId.Guard) == 0 &&
            (exclusive.InterruptedBehaviors & CharacterBehaviorId.Guard) == 0,
            "Two fresh conflicting requests select dash without interrupting a guard that never started.");

        var shared = Tick(GuardFlow(conflicts: CharacterBehaviorId.None), settings, Vector2.right, dash: true, guard: true);
        var guardAndDash = CharacterBehaviorId.Guard | CharacterBehaviorId.Dash;
        Expect((shared.ExecutedBehaviors & guardAndDash) == guardAndDash &&
            (shared.StartedBehaviors & guardAndDash) == guardAndDash && shared.DisplacementProvider == CharacterBehaviorId.Dash,
            "A compatible rule allows dash and guard to start together with one displacement provider.");
        ExpectDisplacement(shared, Vector2.right * (settings.DashSpeed * Step), "Dash while guarding");
    }

    private static void GuardGateDeniesDash(CharacterBehaviorGateMode mode, CharacterBehaviorId mask)
    {
        var flow = GuardFlow(gate: new CharacterBehaviorGate(mode, mask), conflicts: CharacterBehaviorId.None);
        var settings = new CharacterMovementSettings();
        Tick(flow, settings, Vector2.zero, guard: true);

        var frame = Tick(flow, settings, Vector2.right, dash: true, guard: true);
        Expect(frame.GetRejection(CharacterBehaviorId.Dash) == CharacterBehaviorRejection.GateDenied &&
            !frame.DashStarted && frame.MoveApproved && (frame.ActiveBehaviors & CharacterBehaviorId.Guard) != 0,
            $"The {mode} gate blocks dash while allowing ordinary movement under guard.");
        ExpectNear(frame.StaminaRemaining, settings.InitialStamina, $"{mode} rejection preserves stamina");
    }

    private static void InterruptChecksHolderPhase()
    {
        var flow = GuardFlow(interrupt: CharacterBehaviorPhase.Startup);
        var settings = new CharacterMovementSettings();
        Tick(flow, settings, Vector2.zero, guard: true);

        var frame = Tick(flow, settings, Vector2.right, dash: true, guard: true);
        Expect(frame.GetRejection(CharacterBehaviorId.Dash) == CharacterBehaviorRejection.InterruptPhaseDenied &&
            !frame.DashStarted && (frame.ActiveBehaviors & CharacterBehaviorId.Guard) != 0 &&
            (frame.InterruptedBehaviors & CharacterBehaviorId.Guard) == 0,
            "An interrupt limited to startup cannot remove guard in its active phase.");
        ExpectNear(frame.StaminaRemaining, settings.InitialStamina, "The phase rejection preserves stamina");
    }

    private static void GuardWhitelistControlsEntry()
    {
        var gate = new CharacterBehaviorGate(CharacterBehaviorGateMode.Whitelist, CharacterBehaviorId.Dash);
        var flow = GuardFlow(gate: gate, conflicts: CharacterBehaviorId.None);
        var settings = new CharacterMovementSettings { DashDuration = 0.5f };
        Tick(flow, settings, Vector2.zero, guard: true);

        var deniedMove = Tick(flow, settings, Vector2.right, guard: true);
        Expect(!deniedMove.MoveApproved && deniedMove.GetRejection(CharacterBehaviorId.Move) == CharacterBehaviorRejection.GateDenied,
            "An unlisted movement request from idle cannot enter the active guard's whitelist.");

        var frame = Tick(flow, settings, Vector2.right, dash: true, guard: true);
        Expect(frame.DashStarted && (frame.ActiveBehaviors & CharacterBehaviorId.Guard) != 0 &&
            (frame.InterruptedBehaviors & CharacterBehaviorId.Guard) == 0,
            "An active guard admits a whitelisted dash and both remain active when compatible.");

        flow.Reset(settings);
        Expect(Tick(flow, settings, Vector2.right).MoveApproved, "Fixture starts moving before guard begins.");
        var continuingMove = Tick(flow, settings, Vector2.right, guard: true);
        Expect(continuingMove.MoveApproved && !continuingMove.MoveEnded &&
            (continuingMove.StartedBehaviors & CharacterBehaviorId.Guard) != 0,
            "An unlisted movement already in progress continues when the guard gate starts.");
    }

    private static void GuardGateDoesNotStopAnExistingDash()
    {
        var gate = new CharacterBehaviorGate(CharacterBehaviorGateMode.Blacklist, CharacterBehaviorId.Dash);
        var flow = GuardFlow(gate: gate, conflicts: CharacterBehaviorId.None);
        var settings = new CharacterMovementSettings { DashDuration = 1f };
        Expect(Tick(flow, settings, Vector2.right, dash: true).DashStarted, "Dash starts before the guard gate exists.");

        var raised = Tick(flow, settings, Vector2.up, guard: true);
        Expect(raised.DashActive && (raised.StartedBehaviors & CharacterBehaviorId.Guard) != 0,
            "Guard with a gate can enter while a compatible dash is already executing.");

        var next = Tick(flow, settings, Vector2.up, guard: true);
        var guardAndDash = CharacterBehaviorId.Guard | CharacterBehaviorId.Dash;
        Expect((next.ExecutedBehaviors & guardAndDash) == guardAndDash && next.DashActive && !next.DashEnded &&
            (next.InterruptedBehaviors & CharacterBehaviorId.Dash) == 0,
            "A newly active gate limits entry without stopping an existing dash.");
    }

    private static void SameStepRequestsShareStamina()
    {
        const CharacterBehaviorId lowPriority = (CharacterBehaviorId)(1UL << 3);
        const CharacterBehaviorId highPriority = (CharacterBehaviorId)(1UL << 4);
        var flow = new CharacterBehaviorFlow(new[]
        {
            new CharacterBehaviorRule(lowPriority, new RequestedBehavior(lowPriority, staminaCost: 6f), priority: 10),
            new CharacterBehaviorRule(highPriority, new RequestedBehavior(highPriority, staminaCost: 6f), priority: 20)
        });
        var settings = new CharacterMovementSettings { InitialStamina = 10f };
        var input = new CharacterInputSnapshot(Vector2.zero, false, requests: lowPriority | highPriority);

        var frame = flow.Tick(input, Vector2.up, Step, settings);
        Expect((frame.StartedBehaviors & highPriority) != 0 && (frame.StartedBehaviors & lowPriority) == 0 &&
            frame.GetRejection(lowPriority) == CharacterBehaviorRejection.InsufficientStamina,
            "Lower priority request sees the remaining stamina after its compatible peer has started.");
        ExpectNear(frame.StaminaRemaining, 4f, "Only the approved request consumes stamina");
    }

    private static void ConcurrentLimitsAndGatesCompose()
    {
        const CharacterBehaviorId slowerMovement = (CharacterBehaviorId)(1UL << 3);
        const CharacterBehaviorId admittedByBoth = (CharacterBehaviorId)(1UL << 4);
        const CharacterBehaviorId admittedBySlowerMovement = (CharacterBehaviorId)(1UL << 5);
        const CharacterBehaviorId admittedByGuard = (CharacterBehaviorId)(1UL << 6);
        const float slowerMovementSpeed = 3f;
        var sharedPermissions = CharacterBehaviorId.Move | admittedByBoth;
        var slowerMovementGate = new CharacterBehaviorGate(CharacterBehaviorGateMode.Whitelist,
            sharedPermissions | CharacterBehaviorId.Guard | admittedBySlowerMovement);
        var guardGate = new CharacterBehaviorGate(CharacterBehaviorGateMode.Whitelist,
            sharedPermissions | slowerMovement | admittedByGuard);
        var flow = new CharacterBehaviorFlow(new[]
        {
            new CharacterBehaviorRule(slowerMovement, new RequestedBehavior(slowerMovement), priority: 20,
                ordinarySpeedLimit: slowerMovementSpeed, gate: slowerMovementGate),
            new CharacterBehaviorRule(CharacterBehaviorId.Guard, new CharacterGuardBehavior(), priority: 10,
                ordinarySpeedLimit: GuardSpeed, gate: guardGate),
            new CharacterBehaviorRule(admittedByBoth, new RequestedBehavior(admittedByBoth)),
            new CharacterBehaviorRule(admittedBySlowerMovement, new RequestedBehavior(admittedBySlowerMovement)),
            new CharacterBehaviorRule(admittedByGuard, new RequestedBehavior(admittedByGuard))
        });
        var settings = new CharacterMovementSettings();
        var holdingBoth = new CharacterInputSnapshot(Vector2.right, false, guardHeld: true, requests: slowerMovement);
        var raised = flow.Tick(holdingBoth, Vector2.up, Step, settings);
        var restrictions = slowerMovement | CharacterBehaviorId.Guard;
        Expect((raised.ActiveBehaviors & restrictions) == restrictions && raised.MoveApproved,
            "Both compatible movement restrictions are active.");
        ExpectNear(raised.OrdinarySpeedLimit, GuardSpeed, "The strictest concurrent speed limit wins");
        ExpectDisplacement(raised, Vector2.right * (GuardSpeed * Step), "Movement with both restrictions");

        var requests = slowerMovement | admittedByBoth | admittedBySlowerMovement | admittedByGuard;
        var restricted = flow.Tick(new CharacterInputSnapshot(Vector2.right, false, guardHeld: true, requests: requests),
            Vector2.up, Step, settings);
        Expect((restricted.StartedBehaviors & admittedByBoth) != 0 &&
            restricted.GetRejection(admittedBySlowerMovement) == CharacterBehaviorRejection.GateDenied &&
            restricted.GetRejection(admittedByGuard) == CharacterBehaviorRejection.GateDenied,
            "A new request must be admitted by both active gates.");

        var released = flow.Tick(new CharacterInputSnapshot(Vector2.right, false, requests: requests),
            Vector2.up, Step, settings);
        Expect((released.EndedBehaviors & CharacterBehaviorId.Guard) != 0 &&
            (released.ActiveBehaviors & slowerMovement) != 0 &&
            (released.StartedBehaviors & admittedBySlowerMovement) != 0 &&
            released.GetRejection(admittedByGuard) == CharacterBehaviorRejection.GateDenied,
            "Releasing guard immediately leaves only the other state's gate in effect.");
        ExpectNear(released.OrdinarySpeedLimit, slowerMovementSpeed, "The remaining speed limit applies after release");
        ExpectDisplacement(released, Vector2.right * (slowerMovementSpeed * Step), "Movement with one restriction");
    }

    private static void AdditionalDisplacementRequiresMoveInterrupt()
    {
        const CharacterBehaviorId action = (CharacterBehaviorId)(1UL << 3);
        var denied = RequestAdditionalDisplacement(action, null);
        Expect(denied.MoveApproved && !denied.MoveEnded && denied.DisplacementProvider == CharacterBehaviorId.Move &&
            denied.GetRejection(action) == CharacterBehaviorRejection.Conflict,
            "A conflicting displacement request without permission leaves the already approved movement in place.");

        var permit = new[] { new CharacterBehaviorInterrupt(action, CharacterBehaviorId.Move) };
        var approved = RequestAdditionalDisplacement(action, permit);
        Expect(approved.DisplacementProvider == action &&
            approved.DisplacementSource == CharacterDisplacementSource.AdditionalBehavior && approved.MoveEnded &&
            (approved.StartedBehaviors & action) != 0 &&
            (approved.InterruptedBehaviors & CharacterBehaviorId.Move) != 0,
            "An explicitly permitted request interrupts movement and supplies the sole displacement.");
        ExpectDisplacement(approved, Vector2.up, "Additional displacement");
    }

    private static CharacterBehaviorFrameResult RequestAdditionalDisplacement(CharacterBehaviorId action,
        CharacterBehaviorInterrupt[] interrupts)
    {
        var rule = new CharacterBehaviorRule(action, new RequestedBehavior(action, displacement: Vector2.up),
            priority: 10, providesDisplacement: true);
        var flow = new CharacterBehaviorFlow(new[] { rule }, interrupts);
        var settings = new CharacterMovementSettings();
        Expect(Tick(flow, settings, Vector2.right).MoveApproved, "Fixture begins in ordinary movement.");

        var input = new CharacterInputSnapshot(Vector2.right, false, requests: action);
        return flow.Tick(input, Vector2.up, Step, settings);
    }

    private static void ConflictingRulesRequireDistinctPriorities()
    {
        const CharacterBehaviorId first = (CharacterBehaviorId)(1UL << 3);
        const CharacterBehaviorId second = (CharacterBehaviorId)(1UL << 4);
        var rules = new[]
        {
            new CharacterBehaviorRule(first, new RequestedBehavior(first), conflicts: second),
            new CharacterBehaviorRule(second, new RequestedBehavior(second))
        };
        try
        {
            _ = new CharacterBehaviorFlow(rules);
        }
        catch (ArgumentException)
        {
            return;
        }
        throw new InvalidOperationException("Conflicting rules with equal priority must be rejected during configuration.");
    }

    private static void ResetClearsActiveBehaviorsAndResources()
    {
        var flow = GuardFlow(conflicts: CharacterBehaviorId.None);
        var settings = new CharacterMovementSettings { DashDuration = 0.5f };
        var active = Tick(flow, settings, Vector2.right, dash: true, guard: true);
        Expect(active.DashActive && (active.ActiveBehaviors & CharacterBehaviorId.Guard) != 0,
            "Reset fixture starts both compatible behaviors.");

        flow.Reset(settings);
        var idle = Tick(flow, settings, Vector2.zero);
        Expect(idle.FrameNumber == 1 && !idle.DashActive && !idle.DashEnded &&
            (idle.ActiveBehaviors & CharacterBehaviorId.Guard) == 0 &&
            (idle.EndedBehaviors & CharacterBehaviorId.Guard) == 0,
            "Reset removes prior guard and dash state and restarts the frame counter.");
        ExpectNear(idle.StaminaRemaining, settings.InitialStamina, "Reset restores initial stamina");
        ExpectNear(idle.DashCooldownRemaining, 0f, "Reset clears cooldown");
        Expect(Tick(flow, settings, Vector2.up, dash: true, guard: true).DashStarted,
            "After reset, rule configuration still allows new dash requests.");
    }

    private static void SharedSettingsKeepCharacterStateIndependent()
    {
        var shared = new CharacterMovementSettings { InitialStamina = 80f, DashStaminaCost = 20f, DashCooldown = 1f };
        var first = new CharacterBehaviorFlow();
        var second = new CharacterBehaviorFlow();
        var dash = Tick(first, shared, Vector2.right, dash: true);
        var idle = Tick(second, shared, Vector2.zero);
        Expect(dash.DashStarted && !idle.DashStarted, "Only the requesting character dashes.");
        ExpectNear(dash.StaminaRemaining, 60f, "Dash consumes the first character's stamina");
        ExpectNear(idle.StaminaRemaining, 80f, "Another character starts with full stamina");
        ExpectNear(idle.DashCooldownRemaining, 0f, "Another character has no cooldown");
        second.Reset(shared);
        ExpectNear(first.StaminaRemaining, 60f, "Resetting another character preserves spent stamina");
        Expect(first.DashCooldownRemaining > 0f, "Resetting another character preserves the active cooldown.");
        ExpectNear(shared.InitialStamina, 80f, "Gameplay never writes back the starting stamina");
    }

    private static CharacterBehaviorFlow GuardFlow(CharacterBehaviorPhase? interrupt = null,
        CharacterBehaviorGate gate = default, CharacterBehaviorId conflicts = CharacterBehaviorId.Dash)
    {
        var rules = new[] { new CharacterBehaviorRule(CharacterBehaviorId.Guard, new CharacterGuardBehavior(),
            conflicts: conflicts, ordinarySpeedLimit: GuardSpeed, gate: gate) };
        if (!interrupt.HasValue)
            return new CharacterBehaviorFlow(rules);
        var interrupts = new[] { new CharacterBehaviorInterrupt(CharacterBehaviorId.Dash, CharacterBehaviorId.Guard,
            allowedPhases: interrupt.Value) };
        return new CharacterBehaviorFlow(rules, interrupts);
    }

    private static CharacterBehaviorFrameResult Tick(CharacterBehaviorFlow flow, CharacterMovementSettings settings,
        Vector2 direction, bool dash = false, bool guard = false)
    {
        return flow.Tick(new CharacterInputSnapshot(direction, dash, guardHeld: guard), Vector2.up, Step, settings);
    }

    private static void ExpectDisplacement(CharacterBehaviorFrameResult frame, Vector2 expected, string message)
    {
        ExpectNear(frame.Displacement.x, expected.x, $"{message}, X");
        ExpectNear(frame.Displacement.y, expected.y, $"{message}, Z");
    }

    private static void ExpectNear(float actual, float expected, string message)
    {
        Expect(Math.Abs(actual - expected) <= 0.00001f, $"{message}: expected {expected}, got {actual}.");
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class RequestedBehavior : CharacterBehavior
    {
        private readonly CharacterBehaviorId id;
        private readonly float staminaCost;
        private readonly Vector2 displacement;

        public RequestedBehavior(CharacterBehaviorId id, float staminaCost = 0f, Vector2 displacement = default)
        {
            this.id = id;
            this.staminaCost = staminaCost;
            this.displacement = displacement;
        }

        public override bool Evaluate(CharacterBehaviorContext context) => (context.Input.Requests & id) != 0;
        public override float GetStaminaCost(CharacterBehaviorContext context) => staminaCost;
        public override void Start(CharacterBehaviorContext context) => IsActive = true;
        public override void End() => IsActive = false;
        public override Vector2 Update(CharacterBehaviorContext context) => displacement;
    }
}
