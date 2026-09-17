using System;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Animations.Rigging;

namespace HagenDa.Animation.Rigging
{
    /// <summary>
    /// 骨骼姿态约束(无状态,驱动器模式之外的直读模式):把一组骨骼的局部
    /// TRS 混合到采集好的姿态(蹲/趴),权重 = 约束权重。数据由编辑器采集
    /// 流程从 SoldierPoseAsset 烘入(骨骼引用 + 局部 TRS 数组)。
    /// 用于蹲/趴姿态层(Rig_Pose):覆盖行走剪辑,之后由 LowerBody(髋部转向
    /// +脚部贴地)/UpperBody(脊柱瞄准)/Hands(手部 IK)叠加。
    /// </summary>
    [Serializable]
    public struct SkeletonPoseConstraintData : IAnimationJobData
    {
        public Transform[] bones;
        public Vector3[] localPositions;
        public Quaternion[] localRotations;

        public bool IsValid()
        {
            if (bones == null || localRotations == null) return false;
            if (bones.Length == 0 || bones.Length != localRotations.Length) return false;
            for (int i = 0; i < bones.Length; i++)
                if (bones[i] == null) return false;
            return true;
        }

        public void SetDefaultValues()
        {
            bones = Array.Empty<Transform>();
            localRotations = Array.Empty<Quaternion>();
        }
    }

    public struct SkeletonPoseConstraintJob : IWeightedAnimationJob
    {
        public NativeArray<ReadWriteTransformHandle> bones;
        public NativeArray<Quaternion> rotations;

        public FloatProperty jobWeight { get; set; }

        public void ProcessRootMotion(AnimationStream stream) { }

        public void ProcessAnimation(AnimationStream stream)
        {
            float w = Mathf.Clamp01(jobWeight.Get(stream));
            if (w <= 0.0001f) return;

            for (int i = 0; i < bones.Length; i++)
            {
                var h = bones[i];
                if (!h.IsValid(stream)) continue;
                Quaternion r = h.GetLocalRotation(stream);
                h.SetLocalRotation(stream, Quaternion.Slerp(r, rotations[i], w));
            }
        }
    }

    public class SkeletonPoseConstraintJobBinder : AnimationJobBinder<SkeletonPoseConstraintJob, SkeletonPoseConstraintData>
    {
        public override SkeletonPoseConstraintJob Create(Animator animator, ref SkeletonPoseConstraintData data, Component component)
        {
            int n = data.bones.Length;
            var job = new SkeletonPoseConstraintJob
            {
                bones = new NativeArray<ReadWriteTransformHandle>(n, Allocator.Persistent),
                rotations = new NativeArray<Quaternion>(n, Allocator.Persistent),
                jobWeight = FloatProperty.Bind(animator, component, "m_Weight")
            };
            for (int i = 0; i < n; i++)
            {
                job.bones[i] = ReadWriteTransformHandle.Bind(animator, data.bones[i]);
                job.rotations[i] = data.localRotations[i];
            }
            return job;
        }

        public override void Destroy(SkeletonPoseConstraintJob job)
        {
            if (job.bones.IsCreated) job.bones.Dispose();
            if (job.rotations.IsCreated) job.rotations.Dispose();
        }
    }

    [DisallowMultipleComponent]
    [AddComponentMenu("Animation Rigging/HagenDa/Skeleton Pose Constraint")]
    public class SkeletonPoseConstraint : RigConstraint<SkeletonPoseConstraintJob, SkeletonPoseConstraintData, SkeletonPoseConstraintJobBinder>
    {
    }
}