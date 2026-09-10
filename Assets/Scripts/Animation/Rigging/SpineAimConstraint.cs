using System;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Animations.Rigging;

namespace HagenDa.Animation.Rigging
{
    /// <summary>
    /// 脊柱瞄准约束(无状态):上半身补齐下半身没跟上的瞄准差。
    ///   lag = aimYaw - hipsYaw(hips 由 HipsPoseConstraint 先应用 damp,执行顺序
    ///   LowerBody rig 在 UpperBody rig 之前保证读到的是本帧 damp 后值)
    ///   chest ← clamp(lag, ±maxTwist) 的水平反扭 + pitch*chestPitchShare
    ///   head  ← 剩余反扭(lag - chestTwist) + pitch*headPitchShare
    /// 全部绕实体根的水平 up/right 轴应用,按约束权重缩放。pitch 正 = 抬头。
    /// </summary>
    [Serializable]
    public struct SpineAimConstraintData : IAnimationJobData
    {
        public Transform root;        // 实体根(水平 right 轴参考)
        public Transform hips;        // 滞后后的髋部(读世界 yaw)
        public Transform chest;       // 必填
        public Transform head;        // 可空
        public Transform aimSource;   // 世界 rotation = 瞄准方向(pitch, yaw)
        public float maxTwist;        // 脊柱最大反扭(度),默认 60
        [Range(0f, 1f)] public float chestPitchShare;  // 胸部 pitch 分担,默认 0.3
        [Range(0f, 1f)] public float headPitchShare;   // 头部 pitch 分担,默认 0.6

        public bool IsValid()
        {
            return root != null && hips != null && chest != null && aimSource != null;
        }

        public void SetDefaultValues()
        {
            root = null;
            hips = null;
            chest = null;
            head = null;
            aimSource = null;
            maxTwist = 60f;
            chestPitchShare = 0.3f;
            headPitchShare = 0.6f;
        }
    }

    public struct SpineAimConstraintJob : IWeightedAnimationJob
    {
        public ReadWriteTransformHandle chest;
        public ReadWriteTransformHandle head;
        public bool hasHead;
        public ReadOnlyTransformHandle root;
        public ReadOnlyTransformHandle hips;
        public ReadOnlyTransformHandle aimSource;

        public float maxTwist;
        public float chestPitchShare;
        public float headPitchShare;

        public FloatProperty jobWeight { get; set; }

        public void ProcessRootMotion(AnimationStream stream) { }

        public void ProcessAnimation(AnimationStream stream)
        {
            float w = Mathf.Clamp01(jobWeight.Get(stream));
            if (w <= 0.0001f) return;

            Quaternion aimRot = aimSource.GetRotation(stream);
            Vector3 aimEuler = aimRot.eulerAngles;
            float aimYaw = aimEuler.y;
            float aimPitch = aimEuler.x > 180f ? aimEuler.x - 360f : aimEuler.x;

            float hipsYaw = hips.GetRotation(stream).eulerAngles.y;
            float lag = Mathf.DeltaAngle(hipsYaw, aimYaw);

            float chestTwist = Mathf.Clamp(lag, -maxTwist, maxTwist);
            float headTwist = lag - chestTwist;
            float chestPitchDeg = -aimPitch * chestPitchShare;   // 负号:绕 right 正角=低头
            float headPitchDeg = -aimPitch * headPitchShare;

            Quaternion rootRot = root.GetRotation(stream);
            Vector3 right = rootRot * Vector3.right;
            right.y = 0f;
            if (right.sqrMagnitude < 1e-6f) right = Vector3.right;
            right.Normalize();

            Quaternion chestRot = chest.GetRotation(stream);
            chestRot = Quaternion.AngleAxis(chestTwist * w, Vector3.up)
                     * Quaternion.AngleAxis(chestPitchDeg * w, right)
                     * chestRot;
            chest.SetRotation(stream, chestRot);

            if (hasHead)
            {
                Quaternion headRot = head.GetRotation(stream);
                headRot = Quaternion.AngleAxis(headTwist * w, Vector3.up)
                        * Quaternion.AngleAxis(headPitchDeg * w, right)
                        * headRot;
                head.SetRotation(stream, headRot);
            }
        }
    }

    public class SpineAimConstraintJobBinder : AnimationJobBinder<SpineAimConstraintJob, SpineAimConstraintData>
    {
        public override SpineAimConstraintJob Create(Animator animator, ref SpineAimConstraintData data, Component component)
        {
            return new SpineAimConstraintJob
            {
                chest = ReadWriteTransformHandle.Bind(animator, data.chest),
                head = data.head != null ? ReadWriteTransformHandle.Bind(animator, data.head) : default,
                hasHead = data.head != null,
                root = ReadOnlyTransformHandle.Bind(animator, data.root),
                hips = ReadOnlyTransformHandle.Bind(animator, data.hips),
                aimSource = ReadOnlyTransformHandle.Bind(animator, data.aimSource),
                maxTwist = data.maxTwist,
                chestPitchShare = data.chestPitchShare,
                headPitchShare = data.headPitchShare,
                jobWeight = FloatProperty.Bind(animator, component, "m_Weight")
            };
        }

        public override void Destroy(SpineAimConstraintJob job) { }
    }

    [DisallowMultipleComponent]
    [AddComponentMenu("Animation Rigging/HagenDa/Spine Aim Constraint")]
    public class SpineAimConstraint : RigConstraint<SpineAimConstraintJob, SpineAimConstraintData, SpineAimConstraintJobBinder>
    {
    }
}