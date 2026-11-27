// Magica Cloth 2.
// Copyright (c) 2023 MagicaSoft.
// https://magicasoft.jp
using System.Text;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;

namespace MagicaCloth2
{
    // 管理布料使用的变换槽位和对应的原生数据。
    public partial class TransformManager : IManager, IValid
    {
        // フラグ
        internal const byte Flag_Read = 0x01;
        internal const byte Flag_WorldRotWrite = 0x02; // ワールド回転のみ書き込み
        internal const byte Flag_LocalPosRotWrite = 0x04; // ローカル座標・回転書き込み
        internal const byte Flag_Restore = 0x08; // 復元する
        internal const byte Flag_Enable = 0x10; // 有効状態
        internal ExNativeArray<ExBitFlag8> flagArray;

        /// <summary>
        /// 初期localPosition
        /// </summary>
        internal ExNativeArray<float3> initLocalPositionArray;

        /// <summary>
        /// 初期localRotation
        /// </summary>
        internal ExNativeArray<quaternion> initLocalRotationArray;

        /// <summary>
        /// ワールド座標
        /// </summary>
        internal ExNativeArray<float3> positionArray;

        /// <summary>
        /// ワールド回転
        /// </summary>
        internal ExNativeArray<quaternion> rotationArray;

        /// <summary>
        /// ワールド逆回転
        /// </summary>
        internal ExNativeArray<quaternion> inverseRotationArray;

        /// <summary>
        /// ワールドスケール
        /// Transform.lossyScaleと等価
        /// </summary>
        internal ExNativeArray<float3> scaleArray;

        /// <summary>
        /// ローカル座標
        /// </summary>
        internal ExNativeArray<float3> localPositionArray;

        /// <summary>
        /// ローカル回転
        /// </summary>
        internal ExNativeArray<quaternion> localRotationArray;

        /// <summary>
        /// 接続チームID(0=なし)
        /// </summary>
        internal ExNativeArray<short> teamIdArray;

        /// <summary>
        /// 読み込み用トランスフォームアクセス配列
        /// 已写入的槽与数值数组索引一致，未绑定的尾部槽可暂不加入作业数组。
        /// </summary>
        internal TransformAccessArray transformAccessArray;


        internal int Count => flagArray?.Count ?? 0;

        //=========================================================================================
        /// <summary>
        /// 書き込み用トランスフォームのデータ参照インデックス
        /// つまり上記配列へのインデックス
        /// </summary>
        //internal ExNativeArray<short> writeIndexArray;

        /// <summary>
        /// 書き込み用トランスフォームアクセス配列
        /// </summary>
        //internal TransformAccessArray writeTransformAccessArray;

        bool isValid;

        //=========================================================================================
        public void Dispose()
        {
            isValid = false;

            flagArray?.Dispose();
            initLocalPositionArray?.Dispose();
            initLocalRotationArray?.Dispose();
            positionArray?.Dispose();
            rotationArray?.Dispose();
            inverseRotationArray?.Dispose();
            scaleArray?.Dispose();
            localPositionArray?.Dispose();
            localRotationArray?.Dispose();
            teamIdArray?.Dispose();
            //writeIndexArray?.Dispose();

            flagArray = null;
            initLocalPositionArray = null;
            initLocalRotationArray = null;
            positionArray = null;
            rotationArray = null;
            inverseRotationArray = null;
            scaleArray = null;
            localPositionArray = null;
            localRotationArray = null;
            teamIdArray = null;
            //writeIndexArray = null;

            if (transformAccessArray.isCreated)
                transformAccessArray.Dispose();
            //if (writeTransformAccessArray.isCreated)
            //    writeTransformAccessArray.Dispose();
        }

        public void EnterdEditMode()
        {
            Dispose();
        }

        public void Initialize()
        {
            Dispose();

            const int capacity = 256;
            flagArray = new ExNativeArray<ExBitFlag8>(capacity);
            initLocalPositionArray = new ExNativeArray<float3>(capacity);
            initLocalRotationArray = new ExNativeArray<quaternion>(capacity);
            positionArray = new ExNativeArray<float3>(capacity);
            rotationArray = new ExNativeArray<quaternion>(capacity);
            inverseRotationArray = new ExNativeArray<quaternion>(capacity);
            scaleArray = new ExNativeArray<float3>(capacity);
            localPositionArray = new ExNativeArray<float3>(capacity);
            localRotationArray = new ExNativeArray<quaternion>(capacity);
            teamIdArray = new ExNativeArray<short>(capacity);

            transformAccessArray = new TransformAccessArray(capacity);

            isValid = true;
        }

        public bool IsValid()
        {
            return isValid;
        }

        //=========================================================================================
        /// <summary>
        /// 按虚拟网格中的顺序注册变换和初始数据。
        /// </summary>
        /// <param name="tdata"></param>
        /// <returns></returns>
        internal DataChunk AddTransform(VirtualMeshContainer cmesh, int teamId)
        {
            if (isValid == false)
                return default;

            Debug.Assert(cmesh != null);
            int cnt = cmesh.GetTransformCount();

            // データコピー追加
            var c = flagArray.AddRange(cmesh.shareVirtualMesh.transformData.flagArray);
            initLocalPositionArray.AddRange(cmesh.shareVirtualMesh.transformData.initLocalPositionArray);
            initLocalRotationArray.AddRange(cmesh.shareVirtualMesh.transformData.initLocalRotationArray);

            // 領域のみ追加
            positionArray.AddRange(cnt);
            rotationArray.AddRange(cnt);
            inverseRotationArray.AddRange(cnt);
            scaleArray.AddRange(cnt);
            localPositionArray.AddRange(cnt);
            localRotationArray.AddRange(cnt);

            // チームID
            teamIdArray.AddRange(cnt, (short)teamId);

            for (int i = 0; i < cnt; i++)
                SetAccessSlot(c.startIndex + i, cmesh.GetTransformFromIndex(i));

            return c;
        }

        /// <summary>
        /// 为碰撞体预留逻辑槽，真实变换在绑定时写入。
        /// </summary>
        /// <param name="count"></param>
        /// <returns></returns>
        internal DataChunk AddTransform(int count, int teamId)
        {
            if (isValid == false)
                return default;

            // 領域のみ追加する
            var c = flagArray.AddRange(count);
            initLocalPositionArray.AddRange(count);
            initLocalRotationArray.AddRange(count);
            positionArray.AddRange(count);
            rotationArray.AddRange(count);
            inverseRotationArray.AddRange(count);
            scaleArray.AddRange(count);
            localPositionArray.AddRange(count);
            localRotationArray.AddRange(count);

            // チームID
            teamIdArray.AddRange(count, (short)teamId);

            // 复用区间时清理已有引用，新分配的尾部只需保留逻辑索引。
            for (int i = c.startIndex; i < c.startIndex + count && i < transformAccessArray.length; i++)
                SetAccessSlot(i, null);

            return c;
        }

        /// <summary>
        /// 注册单个变换和对应的初始姿态。
        /// </summary>
        /// <param name="t"></param>
        /// <param name="flag"></param>
        /// <returns></returns>
        internal DataChunk AddTransform(Transform t, ExBitFlag8 flag, int teamId)
        {
            if (isValid == false)
                return default;

            // データコピー追加
            var c = flagArray.Add(flag);
            initLocalPositionArray.Add(t.localPosition);
            initLocalRotationArray.Add(t.localRotation);
            positionArray.Add(t.position);
            rotationArray.Add(t.rotation);
            inverseRotationArray.Add(math.inverse(t.rotation));
            scaleArray.Add(t.lossyScale);
            localPositionArray.Add(t.localPosition);
            localRotationArray.Add(t.localRotation);

            // チームID
            teamIdArray.Add((short)teamId);

            SetAccessSlot(c.startIndex, t);

            return c;
        }

        /// <summary>
        /// 绑定或清除指定槽的变换，并同步姿态数据和标志。
        /// </summary>
        /// <param name="t"></param>
        /// <param name="flag"></param>
        /// <param name="index"></param>
        internal void SetTransform(Transform t, ExBitFlag8 flag, int index, int teamId)
        {
            if (isValid == false)
                return;

            if (t != null)
            {
                // データ設定
                flagArray[index] = flag;
                initLocalPositionArray[index] = t.localPosition;
                initLocalRotationArray[index] = t.localRotation;
                positionArray[index] = t.position;
                rotationArray[index] = t.rotation;
                inverseRotationArray[index] = math.inverse(t.rotation);
                scaleArray[index] = t.lossyScale;
                localPositionArray[index] = t.localPosition;
                localRotationArray[index] = t.localRotation;
                teamIdArray[index] = (short)teamId;
                SetAccessSlot(index, t);
            }
            else
            {
                // データクリア（無効化）
                flagArray[index] = default;
                SetAccessSlot(index, null);
                teamIdArray[index] = 0;
            }
        }

        /// <summary>
        /// 把源槽的姿态、变换引用和团队信息复制到目标槽。
        /// </summary>
        /// <param name="fromIndex"></param>
        /// <param name="toIndex"></param>
        internal void CopyTransform(int fromIndex, int toIndex)
        {
            if (isValid == false)
                return;

            flagArray[toIndex] = flagArray[fromIndex];
            initLocalPositionArray[toIndex] = initLocalPositionArray[fromIndex];
            initLocalRotationArray[toIndex] = initLocalRotationArray[fromIndex];
            positionArray[toIndex] = positionArray[fromIndex];
            rotationArray[toIndex] = rotationArray[fromIndex];
            inverseRotationArray[toIndex] = inverseRotationArray[fromIndex];
            scaleArray[toIndex] = scaleArray[fromIndex];
            localPositionArray[toIndex] = localPositionArray[fromIndex];
            localRotationArray[toIndex] = localRotationArray[fromIndex];
            SetAccessSlot(toIndex, GetAccessSlot(fromIndex));
            teamIdArray[toIndex] = teamIdArray[fromIndex];
        }

        /// <summary>
        /// 释放分块数据并清除已写入作业数组的变换引用。
        /// </summary>
        /// <param name="c"></param>
        internal void RemoveTransform(DataChunk c)
        {
            if (isValid == false)
                return;
            if (c.IsValid == false)
                return;

            flagArray.RemoveAndFill(c);
            initLocalPositionArray.Remove(c);
            initLocalRotationArray.Remove(c);
            positionArray.Remove(c);
            rotationArray.Remove(c);
            inverseRotationArray.Remove(c);
            scaleArray.Remove(c);
            localPositionArray.Remove(c);
            localRotationArray.Remove(c);
            teamIdArray.RemoveAndFill(c, 0);

            // トランスフォーム削除
            for (int i = 0; i < c.dataLength; i++)
            {
                int index = c.startIndex + i;
                SetAccessSlot(index, null);
            }
        }

        // 已预留但尚未绑定的尾部槽视为空引用。
        Transform GetAccessSlot(int index)
        {
            return index < transformAccessArray.length ? transformAccessArray[index] : null;
        }

        // 只有实际绑定的变换才延长数组，并保留中间空槽的逻辑索引。
        void SetAccessSlot(int index, Transform target)
        {
            if (index < transformAccessArray.length)
            {
                transformAccessArray[index] = target;
                return;
            }

            if (target == null)
                return;

            while (transformAccessArray.length < index)
                transformAccessArray.Add(null);
            transformAccessArray.Add(target);
        }

        // 扩展逻辑槽并在分块迁移时同步已有变换。
        internal DataChunk Expand(DataChunk c, int newLength)
        {
            if (isValid == false)
                return default;

            // 領域
            var nc = flagArray.Expand(c, newLength);
            initLocalPositionArray.Expand(c, newLength);
            initLocalRotationArray.Expand(c, newLength);
            positionArray.Expand(c, newLength);
            rotationArray.Expand(c, newLength);
            inverseRotationArray.Expand(c, newLength);
            scaleArray.Expand(c, newLength);
            localPositionArray.Expand(c, newLength);
            localRotationArray.Expand(c, newLength);

            // チームID
            teamIdArray.Expand(c, newLength);

            // トランスフォームアクセス配列の拡張
            if (c.startIndex != nc.startIndex)
            {
                for (int i = 0; i < c.dataLength; i++)
                {
                    SetAccessSlot(nc.startIndex + i, GetAccessSlot(c.startIndex + i));
                    SetAccessSlot(c.startIndex + i, null);
                }
            }

            return nc;
        }

        //=========================================================================================
        public void InformationLog(StringBuilder allsb)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"========== Transform Manager ==========");
            if (IsValid() == false)
            {
                sb.AppendLine($"Transform Manager. Invalid.");
            }
            else
            {
                int tcnt = transformAccessArray.isCreated ? transformAccessArray.length : 0;
                sb.AppendLine($"Transform Manager. Length:{tcnt}");
                sb.AppendLine($"  -flagArray:{flagArray.ToSummary()}");
                sb.AppendLine($"  -initLocalPositionArray:{initLocalPositionArray.ToSummary()}");
                sb.AppendLine($"  -initLocalRotationArray:{initLocalRotationArray.ToSummary()}");
                sb.AppendLine($"  -positionArray:{positionArray.ToSummary()}");
                sb.AppendLine($"  -rotationArray:{rotationArray.ToSummary()}");
                sb.AppendLine($"  -inverseRotationArray:{inverseRotationArray.ToSummary()}");
                sb.AppendLine($"  -scaleArray:{scaleArray.ToSummary()}");
                sb.AppendLine($"  -localPositionArray:{localPositionArray.ToSummary()}");
                sb.AppendLine($"  -localRotationArray:{localRotationArray.ToSummary()}");
                sb.AppendLine($"  -teamIdArray:{teamIdArray.ToSummary()}");

                if (transformAccessArray.isCreated)
                {
                    for (int i = 0; i < tcnt; i++)
                    {
                        var t = transformAccessArray[i];
                        var flag = flagArray[i];
                        var teamId = teamIdArray[i];
                        sb.Append($"  [{i}] team:{teamId} (");
                        sb.Append(flag.IsSet(Flag_Enable) ? "E" : "");
                        sb.Append(flag.IsSet(Flag_Restore) ? "R" : "");
                        sb.Append(flag.IsSet(Flag_Read) ? "r" : "");
                        sb.Append(flag.IsSet(Flag_WorldRotWrite) ? "W" : "");
                        sb.Append(flag.IsSet(Flag_LocalPosRotWrite) ? "w" : "");
                        sb.AppendLine($") {t?.name ?? "(null)"}");
                    }
                }
            }
            sb.AppendLine();
            Debug.Log(sb.ToString());
            allsb.Append(sb);
        }
    }
}
