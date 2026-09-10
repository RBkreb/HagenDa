using System;
using UnityEngine;

namespace HagenDa.Animation.Rigging
{
    /// <summary>
    /// 手动摆姿采集资产(PHASE13):记录 DEF-* 骨骼的局部 TRS。
    /// 由 SoldierPoseStudio 采集流程生成;SkeletonPoseConstraint 的数据来源。
    /// 蹲/趴姿态含骨骼"位置"变化(如 DEF-spine 下移),肌肉 clip 无法表达,
    /// 故采用原始骨骼 TRS + 约束混合。
    /// </summary>
    [CreateAssetMenu(menuName = "HagenDa/Soldier Pose Asset", fileName = "SoldierPose")]
    public class SoldierPoseAsset : ScriptableObject
    {
        [Serializable]
        public struct BonePose
        {
            public string boneName;
            public Vector3 localPosition;
            public Quaternion localRotation;
        }

        public BonePose[] bones = Array.Empty<BonePose>();
    }
}