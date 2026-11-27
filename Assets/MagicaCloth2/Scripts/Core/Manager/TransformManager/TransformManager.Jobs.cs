// Magica Cloth 2.
// Copyright (c) 2023 MagicaSoft.
// https://magicasoft.jp
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;

namespace MagicaCloth2
{
    // 调度并执行变换的启用、还原、读取和写入作业。
    public partial class TransformManager
    {
        /// <summary>
        /// トランスフォームの有効状態を切り替える
        /// </summary>
        /// <param name="c"></param>
        /// <param name="sw">true=有効, false=無効</param>
        internal void EnableTransform(DataChunk c, bool sw)
        {
            if (isValid == false)
                return;
            if (c.IsValid == false)
                return;

            var job = new EnableTransformJob()
            {
                chunk = c,
                sw = sw,
                flagList = flagArray.GetNativeArray(),
            };
            job.Run();
        }

        [BurstCompile]
        struct EnableTransformJob : IJob
        {
            public DataChunk chunk;
            public bool sw;
            public NativeArray<ExBitFlag8> flagList;

            public void Execute()
            {
                for (int i = 0; i < chunk.dataLength; i++)
                {
                    int index = chunk.startIndex + i;

                    var flag = flagList[index];
                    if (flag.Value == 0)
                        continue;

                    flag.SetFlag(Flag_Enable, sw);
                    flagList[index] = flag;
                }
            }
        }

        /// <summary>
        /// トランスフォームの有効状態を切り替える
        /// </summary>
        /// <param name="index"></param>
        /// <param name="sw">true=有効, false=無効</param>
        internal void EnableTransform(int index, bool sw)
        {
            if (isValid == false)
                return;
            if (index < 0)
                return;

            var flag = flagArray[index];
            if (flag.Value == 0)
                return;
            flag.SetFlag(Flag_Enable, sw);
            flagArray[index] = flag;
        }

        //=========================================================================================
        /// <summary>
        /// Transformを初期姿勢で復元させるジョブを発行する
        /// </summary>
        /// <param name="count"></param>
        /// <param name="jobHandle"></param>
        /// <returns></returns>
        public JobHandle RestoreTransform(JobHandle jobHandle)
        {
            if (Count > 0)
            {
                //Debug.Log("RestoreTransform");
                var job = new RestoreTransformJob()
                {
                    flagList = flagArray.GetNativeArray(),
                    localPositionArray = initLocalPositionArray.GetNativeArray(),
                    localRotationArray = initLocalRotationArray.GetNativeArray(),
                    teamIdArray = teamIdArray.GetNativeArray(),

                    teamDataArray = MagicaManager.Team.teamDataArray.GetNativeArray(),
                };
                jobHandle = job.Schedule(transformAccessArray, jobHandle);
            }

            return jobHandle;
        }

        [BurstCompile]
        struct RestoreTransformJob : IJobParallelForTransform
        {
            [Unity.Collections.ReadOnly]
            public NativeArray<ExBitFlag8> flagList;

            [Unity.Collections.ReadOnly]
            public NativeArray<float3> localPositionArray;
            [Unity.Collections.ReadOnly]
            public NativeArray<quaternion> localRotationArray;
            [Unity.Collections.ReadOnly]
            public NativeArray<short> teamIdArray;

            // team
            [Unity.Collections.ReadOnly]
            public NativeArray<TeamManager.TeamData> teamDataArray;


            public void Execute(int index, TransformAccess transform)
            {
                if (transform.isValid == false)
                    return;
                var flag = flagList[index];
                if (flag.IsSet(Flag_Enable) == false)
                    return;
                if (flag.IsSet(Flag_Restore) == false)
                    return;

                // Keepカリング時はスキップする
                int teamId = teamIdArray[index];
                var tdata = teamDataArray[teamId];
                if (tdata.IsCullingInvisible && tdata.IsCullingKeep)
                    return;

                transform.localPosition = localPositionArray[index];
                transform.localRotation = localRotationArray[index];
            }
        }

        //=========================================================================================
        /// <summary>
        /// トランスフォームを読み込むジョブを発行する
        /// </summary>
        /// <param name="jobHandle"></param>
        /// <returns></returns>
        public JobHandle ReadTransform(JobHandle jobHandle)
        {
            if (Count > 0)
            {
                // todo:未来予測などがあると色々複雑化するところ

                var job = new ReadTransformJob()
                {
                    flagList = flagArray.GetNativeArray(),
                    positionArray = positionArray.GetNativeArray(),
                    rotationArray = rotationArray.GetNativeArray(),
                    scaleList = scaleArray.GetNativeArray(),
                    localPositionArray = localPositionArray.GetNativeArray(),
                    localRotationArray = localRotationArray.GetNativeArray(),
                    inverseRotationArray = inverseRotationArray.GetNativeArray(),

                    teamIdArray = teamIdArray.GetNativeArray(),

                    teamDataArray = MagicaManager.Team.teamDataArray.GetNativeArray(),
                };
                jobHandle = job.ScheduleReadOnly(transformAccessArray, 8, jobHandle);
            }

            return jobHandle;
        }

        [BurstCompile]
        struct ReadTransformJob : IJobParallelForTransform
        {
            [Unity.Collections.ReadOnly]
            public NativeArray<ExBitFlag8> flagList;

            [Unity.Collections.WriteOnly]
            public NativeArray<float3> positionArray;
            [Unity.Collections.WriteOnly]
            public NativeArray<quaternion> rotationArray;
            [Unity.Collections.WriteOnly]
            public NativeArray<float3> scaleList;
            [Unity.Collections.WriteOnly]
            public NativeArray<float3> localPositionArray;
            [Unity.Collections.WriteOnly]
            public NativeArray<quaternion> localRotationArray;
            [Unity.Collections.WriteOnly]
            public NativeArray<quaternion> inverseRotationArray;

            [Unity.Collections.ReadOnly]
            public NativeArray<short> teamIdArray;

            // team
            [Unity.Collections.ReadOnly]
            public NativeArray<TeamManager.TeamData> teamDataArray;

            public void Execute(int index, TransformAccess transform)
            {
                if (transform.isValid == false)
                    return;
                var flag = flagList[index];
                if (flag.IsSet(Flag_Enable) == false)
                    return;
                if (flag.IsSet(Flag_Read) == false)
                    return;

                // カリング時は書き込まない
                int teamId = teamIdArray[index];
                var tdata = teamDataArray[teamId];
                if (tdata.IsCullingInvisible)
                    return;

                var pos = transform.position;
                var rot = transform.rotation;
                float4x4 LtoW = transform.localToWorldMatrix;

                positionArray[index] = pos;
                rotationArray[index] = rot;
                localPositionArray[index] = transform.localPosition;
                localRotationArray[index] = transform.localRotation;

                // マトリックスから正確なスケール値を算出する（これはTransform.lossyScaleと等価）
                var irot = math.inverse(rot);
                var m2 = math.mul(new float4x4(irot, float3.zero), LtoW);
                var scl = new float3(m2.c0.x, m2.c1.y, m2.c2.z);
                scaleList[index] = scl;

                // ワールド->ローカル変換用の逆クォータニオン
                inverseRotationArray[index] = math.inverse(rot);
            }
        }

        //=========================================================================================
        /// <summary>
        /// Transformを書き込むジョブを発行する
        /// </summary>
        /// <param name="count"></param>
        /// <param name="jobHandle"></param>
        /// <returns></returns>
        public JobHandle WriteTransform(JobHandle jobHandle)
        {
            var job = new WriteTransformJob()
            {
                flagList = flagArray.GetNativeArray(),
                worldPositions = positionArray.GetNativeArray(),
                worldRotations = rotationArray.GetNativeArray(),
                localPositions = localPositionArray.GetNativeArray(),
                localRotations = localRotationArray.GetNativeArray(),

                teamIdArray = teamIdArray.GetNativeArray(),

                teamDataArray = MagicaManager.Team.teamDataArray.GetNativeArray(),
            };
            jobHandle = job.Schedule(transformAccessArray, jobHandle);

            return jobHandle;
        }

        [BurstCompile]
        struct WriteTransformJob : IJobParallelForTransform
        {
            [Unity.Collections.ReadOnly]
            public NativeArray<ExBitFlag8> flagList;

            [Unity.Collections.ReadOnly]
            public NativeArray<float3> worldPositions;
            [Unity.Collections.ReadOnly]
            public NativeArray<quaternion> worldRotations;
            [Unity.Collections.ReadOnly]
            public NativeArray<float3> localPositions;
            [Unity.Collections.ReadOnly]
            public NativeArray<quaternion> localRotations;

            [Unity.Collections.ReadOnly]
            public NativeArray<short> teamIdArray;

            // team
            [Unity.Collections.ReadOnly]
            public NativeArray<TeamManager.TeamData> teamDataArray;

            public void Execute(int index, TransformAccess transform)
            {
                if (transform.isValid == false)
                    return;
                var flag = flagList[index];
                if (flag.IsSet(Flag_Enable) == false)
                    return;

                // カリング時は書き込まない
                int teamId = teamIdArray[index];
                var tdata = teamDataArray[teamId];
                if (tdata.IsCullingInvisible)
                    return;

                // 書き込み停止中ならスキップ
                if (tdata.flag.IsSet(TeamManager.Flag_SkipWriting))
                    return;

                if (flag.IsSet(Flag_WorldRotWrite))
                {
                    // ワールド回転
                    transform.rotation = worldRotations[index];

                    // BoneSpringのみワールド座標を書き込む
                    if (tdata.IsSpring)
                        transform.position = worldPositions[index];
                }
                else if (flag.IsSet(Flag_LocalPosRotWrite))
                {
                    // ローカル座標・回転を書き込む
                    transform.localPosition = localPositions[index];
                    transform.localRotation = localRotations[index];
                }
            }
        }
    }
}
