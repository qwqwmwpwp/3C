using UnityEngine;

namespace ThreeC.CharacterBehavior
{
    /// <summary>保存同一固定步的实测地面速度、制动进度及用于后续推算的驱动状态。</summary>
    public readonly struct CharacterGroundObservation
    {
        public bool IsValid { get; }
        public Vector3 GroundVelocity => Prediction.Velocity; // 求解速度在接触面上的分量，单位米/秒。
        public bool IsBraking { get; }
        public bool IsBrakeLeadIn { get; }
        public bool IsBrakeObstructed { get; }
        public float InitialStopDistance { get; }
        public float InitialStopSeconds { get; }
        public float ActualStopTravel { get; }
        public float MainBrakeTravel { get; }
        public float RemainingStopDistance { get; }
        public float RemainingStopSeconds { get; }
        internal CharacterGroundPrediction Prediction { get; }

        // 记录实测固定步，未参与制动时各项制动累计值从零开始。
        internal CharacterGroundObservation(in CharacterGroundPrediction prediction, bool braking = false, bool leadIn = false,
            bool obstructed = false, float initialDistance = 0f, float initialSeconds = 0f, float travel = 0f,
            float mainTravel = 0f, float remainingDistance = 0f, float remainingSeconds = 0f)
        {
            IsValid = true;
            Prediction = prediction;
            IsBraking = braking;
            IsBrakeLeadIn = leadIn;
            IsBrakeObstructed = obstructed;
            InitialStopDistance = initialDistance;
            InitialStopSeconds = initialSeconds;
            ActualStopTravel = travel;
            MainBrakeTravel = mainTravel;
            RemainingStopDistance = remainingDistance;
            RemainingStopSeconds = remainingSeconds;
        }
    }
}
