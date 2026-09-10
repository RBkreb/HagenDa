using System;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Animations.Rigging;

namespace HagenDa.Animation.Rigging
{
    /// <summary>
    /// 髋部朝向约束(无状态,驱动器模式):把身体朝向对齐 yawDriver 的朝向
    /// (NetworkSoldierAnimator 每帧 SmoothDampAngle 后写入的下半身滞后朝向)。
    /// 按约束权重缩放。滞后/平滑的全部状态在驱动脚本侧,约束只做应用,帧间无状态。
    ///
    /// 注意:不能用 eulerAngles.y 做反馈——骨架根(Hips=armature)常带 Blender 导入
    /// 的翻转静止旋转(如 X=270°),世界 yaw 在欧拉分解里落在 Z 分量上,eulerAngles.y
    /// 读到的是分解伪影,反馈永不收敛 → 每帧注入一次"矫正"旋转,身体持续自转。
    /// 这里改用朝向向量测差:身体朝向 = hipsRot * facingAxis(Blender 约定 -Y),
    /// 与 yawDriver 的 forward 投影到水平面求有符号角,绕世界 Y 一次性转差。
    ///
    /// 髋部"位置"(蹲/趴下移)不走本约束——Humanoid 系统里直接 SetPosition
    /// 骨骼会被 muscle/root 重计算覆盖(反系统设计)。位置由 MultiPositionConstraint
    /// 约束 hips 到 Driver_Offset 空对象实现(SoldierRigSetup bake 时挂载)。
    /// </summary>
    [Serializable]
    public struct HipsPoseConstraintData : IAnimationJobData
    {
        public Transform hips;
        public Transform yawDriver;     // 世界朝向 = damp 后目标

        [Tooltip("身体朝向在 hips 本地空间的轴(Blender armature 默认 -Y;0 向量时回落 -Y)。")]
        public Vector3 facingAxis;

        public bool IsValid()
        {
            return hips != null && yawDriver != null;
        }

        public void SetDefaultValues()
        {
            hips = null;
            yawDriver = null;
            facingAxis = new Vector3(0f, -1f, 0f);
        }
    }

    public struct HipsPoseConstraintJob : IWeightedAnimationJob
    {
        public ReadWriteTransformHandle hips;
        public ReadOnlyTransformHandle yawDriver;
        public Vector3 facingAxis;      // 已归一化

        public FloatProperty jobWeight { get; set; }

        public void ProcessRootMotion(AnimationStream stream) { }

        public void ProcessAnimation(AnimationStream stream)
        {
            float w = Mathf.Clamp01(jobWeight.Get(stream));
            if (w <= 0.0001f) return;

            // --- 朝向对齐(下半身朝向 → yawDriver 朝向),朝向向量测差,对任意
            //     骨架静止旋转都收敛(eulerAngles.y 反馈在翻转骨架上会持续自旋) ---
            Quaternion hipsRot = hips.GetRotation(stream);
            Vector3 fwd = hipsRot * facingAxis;
            fwd.y = 0f;
            Vector3 tgt = yawDriver.GetRotation(stream) * Vector3.forward;
            tgt.y = 0f;
            if (fwd.sqrMagnitude < 1e-6f || tgt.sqrMagnitude < 1e-6f) return;
            float delta = Vector3.SignedAngle(fwd, tgt, Vector3.up);
            if (Mathf.Abs(delta) > 0.001f)
                hips.SetRotation(stream, Quaternion.AngleAxis(delta * w, Vector3.up) * hipsRot);
        }
    }

    public class HipsPoseConstraintJobBinder : AnimationJobBinder<HipsPoseConstraintJob, HipsPoseConstraintData>
    {
        public override HipsPoseConstraintJob Create(Animator animator, ref HipsPoseConstraintData data, Component component)
        {
            var axis = data.facingAxis.sqrMagnitude < 1e-6f ? new Vector3(0f, -1f, 0f) : data.facingAxis;
            return new HipsPoseConstraintJob
            {
                hips = ReadWriteTransformHandle.Bind(animator, data.hips),
                yawDriver = ReadOnlyTransformHandle.Bind(animator, data.yawDriver),
                facingAxis = axis.normalized,
                jobWeight = FloatProperty.Bind(animator, component, "m_Weight")
            };
        }

        public override void Destroy(HipsPoseConstraintJob job) { }
    }

    [DisallowMultipleComponent]
    [AddComponentMenu("Animation Rigging/HagenDa/Hips Pose Constraint")]
    public class HipsPoseConstraint : RigConstraint<HipsPoseConstraintJob, HipsPoseConstraintData, HipsPoseConstraintJobBinder>
    {
    }
}