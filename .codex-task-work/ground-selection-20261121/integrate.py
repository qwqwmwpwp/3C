from pathlib import Path
R=Path('Assets/CharacterBehavior/Runtime')
def read(n):return (R/n).read_text(encoding='utf-8-sig')
def write(n,s):(R/n).write_text(s,encoding='utf8')
n='CharacterAnimationLocomotion.cs';s=read(n)
s=s.replace('        private float reverseWindow;','''        public long DirectTurnStep { get; private set; }
        public float DirectTurnAngle { get; private set; }
        public float DirectTurnSpeed { get; private set; }
        public string DirectTurnGait { get; private set; }
        private readonly float directAngle;
        private Vector3 turnTarget;
        private float reverseWindow;''')
s=s.replace('            startSeconds =','            directAngle = Mathf.Clamp(config.MatchTurnAngle, 15f, 180f);\n            startSeconds =')
s=s.replace('                ReceiveRelease(motion, behavior, input, grounded);','                DirectTurnStep = 0;\n                ReceiveRelease(motion, behavior, input, grounded);')
s=s.replace('            bool resuming =','''            if (grounded && !motion.IsBlocked && !IsReleasing && motion.HasAcceleration &&
                previousExpectedDirection != Vector3.zero && previousActualVelocity.magnitude > idleExitSpeed &&
                Vector3.Angle(previousExpectedDirection, expectedDirection) + 0.001f >= directAngle)
            {
                float angle = Vector3.SignedAngle(previousActualVelocity, expectedDirection, Vector3.up);
                if (Mathf.Abs(angle) + 0.001f >= directAngle)
                {
                    DirectTurnStep = motion.FrameNumber;
                    DirectTurnAngle = angle <= -179.999f ? 180f : angle;
                    DirectTurnSpeed = previousActualVelocity.magnitude;
                    DirectTurnGait = locomotionKey;
                    turnTarget = expectedDirection;
                }
            }
            if (!grounded || !motion.HasAcceleration || Vector3.Dot(turnTarget, expectedDirection) < 0.8f)
                DirectTurnStep = 0;
            bool resuming =''')
s=s.replace('        private void ClearDirections()\n        {','        private void ClearDirections()\n        {\n            DirectTurnStep = 0;')
write(n,s)
n='CharacterBehaviorAnimator.cs';s=read(n).replace('        private float matchReverseHold = 0.08f;', '''        private float matchReverseHold = 0.08f;
        [SerializeField, Range(15f, 180f), Tooltip("持续移动中触发选姿的输入与速度改向角（度）。")]
        private float matchTurnAngle = 55f;''').replace('MatchDatabase = motionDatabase,','MatchTurnAngle = matchTurnAngle, MatchDatabase = motionDatabase,')
write(n,s)
n='CharacterAnimationPipeline.cs';s=read(n).replace('public float MatchMargin, MatchReverseHold;', 'public float MatchMargin, MatchReverseHold, MatchTurnAngle;')
a=s.index('            var selection = composer.Preview'); b=s.index('            var emission',a)
body=s[a:b]
body=body.replace('            var selection =','            selection =').replace('            var feet = Matcher.Mode == CharacterMatchMode.Rules ? default : Ik.ReadFeet(motion);\n','').replace('            var entryVelocity =','            entryVelocity =').replace('            var plan = poseSelector.Select', '            return Select').replace('poseSelector.DecideRelease','DecideRelease').replace('Matcher.Evaluate','matcher.Evaluate')
n2='CharacterLocomotionPoseSelector.cs';s2=read(n2)
a2=s2.index('        public CharacterReleaseDecision DecideRelease')
s2=s2[:a2]+'''        public CharacterAnimationPlaybackPlan Decide(CharacterAnimationIntentConverter intent, CharacterAnimationComposer composer,
            CharacterMotionMatcher matcher, in CharacterPlaybackObservation seen, in CharacterAnimationPlaybackPlan committedPlan,
            CharacterAnimationMotion motion, CharacterFootObservation feet, float deltaTime,
            out CharacterAnimationComposition selection, out Vector3 entryVelocity)
        {
'''+body+'''        }

'''+s2[a2:]
write(n2,s2.replace('public CharacterReleaseDecision DecideRelease','private CharacterReleaseDecision DecideRelease').replace('public CharacterAnimationPlaybackPlan Select(','private CharacterAnimationPlaybackPlan Select('))
s=s[:a]+'''            var feet = Matcher.Mode == CharacterMatchMode.Rules ? default : Ik.ReadFeet(motion);
            var plan = poseSelector.Decide(intent, composer, Matcher, seen, committedPlan, motion, feet, deltaTime,
                out var selection, out var entryVelocity);
'''+s[b:]
s=s.replace('seen.LeftSupport, entryVelocity);','seen.LeftSupport, entryVelocity);').replace('            observer.InvalidateAcceleration();','            intent.InvalidateMotion();\n            observer.InvalidateAcceleration();').replace('                intent.FinishStop();','                intent.InvalidateMotion();')
write(n,s)
n='CharacterMotionDatabase.cs';s=read(n)
s=s.replace('        public int Index(float phase)','''        public bool ReadyForSearch => Use != GroundMotionUse.Legacy && IsReady && FirstEntrySeconds >= 0f &&
            LastEntrySeconds >= FirstEntrySeconds && LastEntrySeconds <= Seconds + 0.001f && ReferenceSpeed > 0f &&
            (PathSource == GroundPathSource.Straight || (PathSource == GroundPathSource.Authored ? ReferencePath : RootPath)?.Length >= 2);

        public int Index(float phase)''')
write(n,s)

