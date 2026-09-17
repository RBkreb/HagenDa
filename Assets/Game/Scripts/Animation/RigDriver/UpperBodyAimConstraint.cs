using System;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Animations.Rigging;

namespace HagenDa.Animation.RigDriver
{
    /// <summary>
    /// PHASE14 脊柱瞄准约束：上半身补齐下半身没跟上的水平转向差（脊柱反扭）。
    ///
    ///   lag    = 水平面内 SignedAngle(下半身朝向, 瞄准朝向)
    ///   chest  ← clamp(lag, ±maxTwist) 的反扭 + pitch * chestPitchShare
    ///   head   ← 剩余反扭(lag - chestTwist) + pitch * headPitchShare
    ///
    /// 旋转全部绕实体根的水平 up/right 轴应用，按约束权重缩放；pitch 正 = 抬头。
    /// 瞄准源必须是**动画骨架之外**的 Transform（Rig_Drivers/Driver_AimSource）：
    /// 动画骨架内骨骼子对象的 Transform 经 AnimationStream 有独立副本，rig 作业
    /// 读到的是动画旧值（旧分支实测），骨架外句柄读的是实时场景值。
    /// 下半身朝向用朝向向量测差（翻转骨架上 eulerAngles.y 是分解伪影）。
    /// </summary>
    [Serializable]
    public struct UpperBodyAimConstraintData : IAnimationJobData
    {
        public Transform root;        // 实体根（水平 right 轴参考）
        public Transform lowerBody;   // 滞后后的下半身（模型根，读世界朝向）
        public Transform chest;       // 必填（DEF-spine.002）
        public Transform head;        // 可空（DEF-spine.005）
        public Transform aimSource;   // 世界 rotation = 瞄准方向（Rig_Drivers/Driver_AimSource）
        public float maxTwist;        // 脊柱最大反扭（度），默认 60
        [Range(0f, 1f)] public float chestPitchShare;  // 胸部 pitch 分担，默认 0.3
        [Range(0f, 1f)] public float headPitchShare;   // 头部 pitch 分担，默认 0.6

        [Tooltip("下半身朝向在其本地空间的轴。模型根由驱动侧直接旋转，本地 +Z 即身体前向；0 向量回落 +Z。")]
        public Vector3 facingAxis;

        public bool IsValid()
        {
            return root != null && lowerBody != null && chest != null && aimSource != null;
        }

        public void SetDefaultValues()
        {
            root = null;
            lowerBody = null;
            chest = null;
            head = null;
            aimSource = null;
            maxTwist = 60f;
            chestPitchShare = 0.3f;
            headPitchShare = 0.6f;
            facingAxis = new Vector3(0f, 0f, 1f);
        }
    }

    public struct UpperBodyAimConstraintJob : IWeightedAnimationJob
    {
        public ReadWriteTransformHandle chest;
        public ReadWriteTransformHandle head;
        public bool hasHead;
        public ReadOnlyTransformHandle root;
        public ReadOnlyTransformHandle lowerBody;
        public ReadOnlyTransformHandle aimSource;

        public float maxTwist;
        public float chestPitchShare;
        public float headPitchShare;
        public Vector3 facingAxis;      // 已归一化

        public FloatProperty jobWeight { get; set; }

        public void ProcessRootMotion(AnimationStream stream) { }

        public void ProcessAnimation(AnimationStream stream)
        {
            float w = Mathf.Clamp01(jobWeight.Get(stream));
            if (w <= 0.0001f) return;

            Vector3 aimDir = aimSource.GetRotation(stream) * Vector3.forward;
            Vector3 lowerFwd = lowerBody.GetRotation(stream) * facingAxis;
            lowerFwd.y = 0f;
            Vector3 aimFwd = aimDir; aimFwd.y = 0f;
            if (lowerFwd.sqrMagnitude < 1e-6f || aimFwd.sqrMagnitude < 1e-6f) return;

            float lag = Vector3.SignedAngle(lowerFwd, aimFwd, Vector3.up);

            // pitch 由瞄准方向的垂直分量求：正值 = 低头（与 euler x 同号约定）。
            float aimPitch = -Mathf.Asin(Mathf.Clamp(aimDir.y, -1f, 1f)) * Mathf.Rad2Deg;

            float chestTwist = Mathf.Clamp(lag, -maxTwist, maxTwist);
            float headTwist = lag - chestTwist;
            float chestPitchDeg = -aimPitch * chestPitchShare;   // 负号：绕 right 正角 = 低头
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

    public class UpperBodyAimConstraintJobBinder
        : AnimationJobBinder<UpperBodyAimConstraintJob, UpperBodyAimConstraintData>
    {
        public override UpperBodyAimConstraintJob Create(Animator animator,
            ref UpperBodyAimConstraintData data, Component component)
        {
            var axis = data.facingAxis.sqrMagnitude < 1e-6f ? new Vector3(0f, 0f, 1f) : data.facingAxis;
            return new UpperBodyAimConstraintJob
            {
                chest = ReadWriteTransformHandle.Bind(animator, data.chest),
                head = data.head != null ? ReadWriteTransformHandle.Bind(animator, data.head) : default,
                hasHead = data.head != null,
                root = ReadOnlyTransformHandle.Bind(animator, data.root),
                lowerBody = ReadOnlyTransformHandle.Bind(animator, data.lowerBody),
                aimSource = ReadOnlyTransformHandle.Bind(animator, data.aimSource),
                maxTwist = data.maxTwist,
                chestPitchShare = data.chestPitchShare,
                headPitchShare = data.headPitchShare,
                facingAxis = axis.normalized,
                jobWeight = FloatProperty.Bind(animator, component, "m_Weight")
            };
        }

        public override void Destroy(UpperBodyAimConstraintJob job) { }
    }

    [DisallowMultipleComponent]
    [AddComponentMenu("Animation Rigging/HagenDa/Upper Body Aim Constraint")]
    public class UpperBodyAimConstraint : RigConstraint<
        UpperBodyAimConstraintJob,
        UpperBodyAimConstraintData,
        UpperBodyAimConstraintJobBinder>
    {
    }
}
