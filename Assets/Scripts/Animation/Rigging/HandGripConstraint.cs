using System;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Animations.Rigging;

namespace HagenDa.Animation.Rigging
{
    /// <summary>
    /// 通用型握持约束 —— 简化版「驱动骨骼 + copy rotation」。
    /// 针对 Natlan Soldier 这类 DEF-* 骨骼模型（掌根 DEF-hand + 每手三组两段指链：
    /// f_index / f_middle(中指+无名指+小指) / thumb）。
    ///
    /// 用法（替代“每把武器手动建 Handler/Hint + TwoBoneIK + bake 进预制体”）：
    ///   1. Rig 下建两个子物体（左手/右手），各挂 HandGripConstraint；
    ///   2. 每个约束只需拖一个 CapsuleCollider（武器/道具的握把胶囊）；
    ///   3. 编辑器工具在 bind 姿态分析一次（写骨骼引用/自然弯轴/满握角），并自动
    ///      在 DEF-hand 下创建 GripDriver 驱动骨骼。
    ///
    /// 运行时（RigBuilder 每帧）：
    ///   a. 握紧程度 grip ∈ 0..1（几何驱动，不再用 Rig 权重当握力）：
    ///      · gripMax  = 胶囊直径限制：理论满握半径(anchorLen*gripRadiusFrac)
    ///                   与实际胶囊半径之比，柱子越粗握得越不拢（细柱≈1）。
    ///      · gripNear = 腕到胶囊轴距离 ramp：贴握=1，离轴越远越松，超过
    ///                   (半径+掌厚+reachBase*gripReleaseFrac) 完全松开。
    ///      · grip = curl * gripMax * gripNear；Rig 权重只做整约束混合。
    ///      autoCurl=0 时用 curl 手动值。
    ///   b. 驱动骨骼：localRot = AngleAxis(grip*driverMaxAngle*sideSign, 局部Z)，
    ///      直观表示握紧程度（也可被动画/手动驱动，作为纯可视化控制）。
    ///   c. 三组指链逐关节 copy rotation：目标 = bind 姿态 ∘ 绕自然弯曲轴转
    ///      (grip*满握角*influence*sgn)，再与动画姿态按 grip 混合。弯曲轴在
    ///      bind 姿态解析求得（指节指向 × 掌心法线，掌心侧由拇指判符号），
    ///      左右手自动取到各自正确的收拢方向；拇指的轴会额外偏向手骨局部 X
    ///      （Humanoid 肌肉只为拇指在 X 方向提供屈曲自由度，绕斜向轴会被钳制
    ///      到约 1/4——掌心是一个面，绕 X 同样能把拇指收向掌面）。每块骨的
    ///      满握角/influence/sgn 均在 Inspector 可调（fingers[].flex/inf/sgn）。
    ///      不做胶囊表面求解——弯曲上限 = 满握角，结构上杜绝过度弯折嵌入。
    ///   d. 带动臂模式（可选 armIK=true）：手腕位置目标优先取 wristAnchor（如武器上
    ///      预放的 GripRoot 空物体）；目标超出臂长可达球时按距离淡出 IK、安全退回
    ///      动画姿态。
    /// </summary>

    [Serializable]
    public struct HandGripFinger
    {
        public Transform root;   // .01（指根）
        public Transform mid;    // .02（中节）
        public float len1;          // root->mid 世界长度（分析刻度，比例锚）
        public Vector3 axisA;       // .01 自然弯曲轴（.01 自身局部坐标；拇指天然反向）
        public Vector3 axisB;       // .02 自然弯曲轴（.02 自身局部坐标）
        public float flexA;         // .01 满握角（度，grip=1 时的弯曲量）
        public float flexB;         // .02 满握角（度）
        public float infA;          // .01 influence（微调，0=未设置按1处理）
        public float infB;          // .02 influence
        public float sgnA;          // .01 copy 方向（+1/-1，缠绕方向不对时翻转）
        public float sgnB;          // .02 copy 方向
        public int role;            // 0=index 1=midGroup 2=thumb
        public Quaternion q01Rel;   // bind 时 .01 的局部姿态（copy rotation 基准）
        public Quaternion q12Rel;   // bind 时 .02 的局部姿态
    }

    public enum HandGripSide
    {
        Auto = 0,
        Left = 1,
        Right = 2,
    }

    /// <summary>
    /// 约束数据。analyzed 常量由 HandGripAnalyzer 在 bind 姿态写入；
    /// 运行时 Binder 只读取序列化数据（不依赖模型运行姿态）。
    /// </summary>
    [Serializable]
    public struct HandGripConstraintData : IAnimationJobData
    {
        public HandGripSide side;
        public CapsuleCollider grip;         // 唯一必填
        public Transform rootBone;           // DEF-upper_arm.L/R
        public Transform midBone;            // DEF-forearm.L/R
        public Transform tipBone;            // DEF-hand.L/R
        public Transform armatureRoot;       // 自动解析范围（可选）
        public HandGripFinger[] fingers;     // [index, midGroup, thumb]
        public bool analyzed;

        // 分析常量
        public Vector3 hingeLocal;           // 手指弯曲轴（hand rest-local）
        public Vector3 backLocal;            // 手背法线（hand rest-local）
        public Vector3 azLocal;              // 胶囊局部坐标内：轴->手背 的径向单位向量（⊥胶囊轴）
        public Vector3 axisLocal;            // 胶囊局部坐标轴向单位向量
        public float reachBase;              // 腕->指根环平均长度（米）
        public float anchorLen;              // index.len1（米；运行时比例锚）
        public float axialOffset;            // 腕点沿胶囊轴偏移（米，带动臂无锚点时用）
        public float capPerpScale;           // 分析时胶囊垂直轴缩放（常见 1）

        // 调参
        public float reachFrac;              // 带动臂无锚点时：腕距=胶囊半径+reachBase*reachFrac
        public float curl;                   // 手动握紧程度 0..1（autoCurl=0 时生效）
        public float autoCurl;               // 1=按柱体半径+手距自动算 grip（推荐）；0=固定用 curl
        public float gripRadiusFrac;          // 理论“满握”半径 = anchorLen * 该系数（按此限制最大握紧）
        public float gripPalmFrac;            // 掌厚系数：满握腕距 = 胶囊半径 + reachBase*该系数（掌心位置）
        public float gripReleaseFrac;         // 松开带宽度 = reachBase * 该系数
        public float thumbXBias;              // 拇指弯曲轴偏向手骨局部X的程度 0..1（掌心是面，X轴同样朝掌心）
        public bool armIK;                   // false=仅握持(推荐)：不移动手臂/手腕，只收指；true=带动臂
        public float twistDeg;               // 带动臂模式：腕部绕胶囊轴附加扭转（度）
        public Transform wristAnchor;        // 可选：手腕目标锚点（如武器上预放的 GripRoot 空物体）
        public bool useAnchorRot;            // 带动臂模式：腕朝向直接采用锚点旋转
        public float armLength;              // bind 上臂根->手 距离（带动臂模式可达半径，米）
        public Transform gripDriver;         // 驱动骨骼（DEF-hand 子物体，代表握紧程度）
        public float driverMaxAngle;         // grip=1 时驱动骨骼绕局部Z的旋转角（度，默认90）

        bool IAnimationJobData.IsValid()
        {
            if (grip == null) return false;
            if (fingers == null || fingers.Length != 3) return false;
            for (int i = 0; i < 3; ++i)
                if (fingers[i].root == null || fingers[i].mid == null) return false;
            return rootBone != null && midBone != null && tipBone != null;
        }

        void IAnimationJobData.SetDefaultValues()
        {
            side = HandGripSide.Auto;
            grip = null;
            rootBone = midBone = tipBone = armatureRoot = null;
            fingers = null;
            analyzed = false;
            hingeLocal = backLocal = Vector3.zero;
            azLocal = Vector3.forward;
            axisLocal = Vector3.up;
            reachBase = anchorLen = axialOffset = 0f;
            capPerpScale = 1f;
            reachFrac = 0.35f;
            curl = 1f;
            autoCurl = 1f;
            gripRadiusFrac = 0.42f;
            gripPalmFrac = 0.5f;
            gripReleaseFrac = 1.0f;
            thumbXBias = 0.8f;
            armIK = false;
            twistDeg = 0f;
            wristAnchor = null;
            useAnchorRot = true;
            armLength = 0.4f;
            gripDriver = null;
            driverMaxAngle = 90f;
        }
    }

    [DisallowMultipleComponent]
    [AddComponentMenu("Animation Rigging/HagenDa/Hand Grip (Capsule) Constraint")]
    public class HandGripConstraint : RigConstraint<
        HandGripConstraintJob,
        HandGripConstraintData,
        HandGripConstraintJobBinder>
    {
    }

    // =========================================================================
    //  Job —— 每帧解算（驱动骨骼 + copy rotation，无表面求解）
    // =========================================================================

    /// <summary>单根指链的 job 常量和句柄。</summary>
    public struct HandGripChainJob
    {
        public ReadWriteTransformHandle root;   // .01
        public ReadWriteTransformHandle mid;    // .02
        public Vector3 axisA;   // .01 自然弯曲轴（该骨自身局部坐标）
        public Vector3 axisB;   // .02 自然弯曲轴
        public float flexA;     // .01 满握角（度）
        public float flexB;     // .02 满握角（度）
        public float infA;      // .01 influence
        public float infB;      // .02 influence
        public float sgnA;      // .01 copy 方向
        public float sgnB;      // .02 copy 方向
        public int role;
        public Quaternion q01Rel;   // bind 局部姿态（copy rotation 基准）
        public Quaternion q12Rel;
    }

    public struct HandGripConstraintJob : IWeightedAnimationJob
    {
        public ReadWriteTransformHandle armRoot;
        public ReadWriteTransformHandle armMid;
        public ReadWriteTransformHandle wrist;
        public ReadOnlyTransformHandle capsule;
        public ReadWriteTransformHandle driver;
        public bool hasDriver;

        public Vector3 axisLocal;
        public float capRadius;
        public float capPerpScale;
        public float axialOffset;
        public Vector3 azLocal;

        public Vector3 hingeLocal, backLocal;
        public float reachBase;
        public float anchorLen;

        public float reachFrac, curl, autoCurl;
        public float gripRadiusFrac;   // 理论满握半径 = anchorLen*该系数；胶囊更粗则限制最大握紧
        public float gripPalmFrac;     // 掌厚系数：满握腕距 = 半径 + reachBase*该系数
        public float gripReleaseFrac;  // 松开带宽 = reachBase*该系数
        public float twistDeg;
        public bool armIK;
        public ReadOnlyTransformHandle anchor;
        public bool hasAnchor, useAnchorRot;
        public float armLength;
        public float driverMaxAngle;
        public float sideSign;      // Left=-1 Right=+1（驱动骨骼正角=收拢）

        public HandGripChainJob chain0, chain1, chain2;

        public FloatProperty jobWeight { get; set; }

        public void ProcessRootMotion(AnimationStream stream) { }

        public void ProcessAnimation(AnimationStream stream)
        {
            float w = Mathf.Clamp01(jobWeight.Get(stream));
            if (w <= 0f)
            {
                Pass(stream, chain0.root); Pass(stream, chain0.mid);
                Pass(stream, chain1.root); Pass(stream, chain1.mid);
                Pass(stream, chain2.root); Pass(stream, chain2.mid);
                if (hasDriver) Pass(stream, driver);
                if (armIK) { Pass(stream, armRoot); Pass(stream, armMid); Pass(stream, wrist); }
                return;
            }

            // ---- 胶囊世界几何 ----
            capsule.GetGlobalTR(stream, out Vector3 cPos, out Quaternion cRot);
            Vector3 axis = cRot * axisLocal; axis.Normalize();
            float scaleNow = 1f;
            if (anchorLen > 1e-4f && chain0.root.IsValid(stream) && chain0.mid.IsValid(stream))
            {
                float dl = Vector3.Distance(chain0.root.GetPosition(stream), chain0.mid.GetPosition(stream));
                if (dl > 1e-5f) scaleNow = dl / anchorLen;
            }
            float radiusW = capRadius * capPerpScale * scaleNow;

            if (armIK)
            {
                // ---------------- 带动臂模式：优先用手腕锚点，超可达范围自动淡出 ----------------
                Vector3 targetPos;
                Quaternion targetRot;
                if (hasAnchor && anchor.IsValid(stream))
                {
                    anchor.GetGlobalTR(stream, out targetPos, out var arot);
                    targetRot = useAnchorRot ? arot
                        : (wrist.IsValid(stream) ? wrist.GetRotation(stream) : Quaternion.identity);
                }
                else
                {
                    Vector3 azWorld = cRot * azLocal;
                    azWorld -= axis * Vector3.Dot(azWorld, axis);
                    if (azWorld.sqrMagnitude < 1e-6f)
                    {
                        azWorld = cRot * (-Vector3.up);
                        azWorld -= axis * Vector3.Dot(azWorld, axis);
                        if (azWorld.sqrMagnitude < 1e-6f) azWorld = cRot * Vector3.forward;
                        azWorld -= axis * Vector3.Dot(azWorld, axis);
                    }
                    azWorld.Normalize();

                    targetRot = HandGripMath.FromTwoAxes(backLocal, azWorld, hingeLocal, axis).normalized;
                    if (Mathf.Abs(twistDeg) > 0.01f)
                        targetRot = Quaternion.AngleAxis(twistDeg, axis) * targetRot;
                    targetRot = targetRot.normalized;

                    targetPos = cPos + axis * (axialOffset * scaleNow)
                              + azWorld * (radiusW + Mathf.Max(0.02f, reachBase * scaleNow * Mathf.Clamp(reachFrac, 0f, 0.8f)));
                }

                // 可达门控：目标超出臂长 -> IK 按距离淡出（保持动画姿态），目标夹取到可达球内
                float wArm = w;
                if (armLength > 0.05f)
                {
                    Vector3 shoulder = armRoot.GetPosition(stream);
                    Vector3 toT = targetPos - shoulder;
                    float dist = toT.magnitude;
                    float inner = armLength * 0.88f, outer = armLength * 1.02f;
                    float g = 1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((dist - inner) / Mathf.Max(1e-4f, outer - inner)));
                    wArm = w * g;
                    if (dist > armLength * 0.97f && dist > 1e-5f)
                        targetPos = shoulder + toT * (armLength * 0.97f / dist);
                }
                if (wArm > 0.001f)
                    SolveArmIK(stream, targetPos, targetRot, wArm);
            }

            // ---- 握紧程度 grip ∈ 0..1：几何驱动（不再靠 Rig 权重当握力） ----
            //   gripMax   = 胶囊直径限制：太粗的柱子手指合不拢，满握量按
            //               满握半径/实际半径 缩减（细柱≈1，粗柱<1）。
            //   gripNear  = 腕到胶囊轴距离 ramp：贴在胶囊面=1，远离=0（渐进松开）。
            //   grip = curl * gripMax * gripNear，最后再乘 Rig 权重（w 仅作整约束混合，
            //   即 0=保持动画姿态、1=完全接管，不再代表握力大小）。
            float grip = Mathf.Clamp01(curl);
            if (autoCurl > 0.5f)
            {
                // 直径限制：理论满握半径（anchorLen*gripRadiusFrac）与实际胶囊半径之比
                float refRadius = Mathf.Max(0.004f, anchorLen * Mathf.Max(0.05f, gripRadiusFrac)) * scaleNow;
                float gripMax = Mathf.Clamp01(refRadius / Mathf.Max(1e-4f, radiusW));

                // 距离 ramp：满握腕距 = 胶囊半径 + 掌厚（reachBase*gripPalmFrac，
                // 掌心在指根内侧，实测掌厚≈reachBase*0.5）。贴握=1，往外
                // reachBase*gripReleaseFrac 的带宽内平滑松开到 0。
                Vector3 wristP = wrist.IsValid(stream) ? wrist.GetPosition(stream) : cPos;
                Vector3 d = wristP - cPos;
                float perp = (d - axis * Vector3.Dot(d, axis)).magnitude;
                float palm = Mathf.Max(0.005f, reachBase * Mathf.Max(0.05f, gripPalmFrac) * scaleNow);
                float near = radiusW + palm;
                float slack = Mathf.Max(0.005f, reachBase * Mathf.Max(0.05f, gripReleaseFrac) * scaleNow);
                float gripNear = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((near + slack - perp) / slack));

                grip *= gripMax * gripNear;
            }
            grip *= w;

            // ---- 驱动骨骼：握紧程度可视化控制 ----
            if (hasDriver && driver.IsValid(stream))
                driver.SetLocalRotation(stream,
                    Quaternion.AngleAxis(grip * driverMaxAngle * sideSign, Vector3.forward));

            // ---- 三组指链 copy rotation（绕自然弯曲轴，拇指天然反向缠绕）----
            ApplyGrip(stream, ref chain0, grip);
            ApplyGrip(stream, ref chain1, grip);
            ApplyGrip(stream, ref chain2, grip);
        }

        // copy rotation：关节目标 = bind 姿态 ∘ 绕自然弯轴转 (grip*满握角*inf*sgn)，
        // 再与动画姿态按 grip 混合（grip→1 完全接管，杜绝动画残留叠加导致过弯）。
        void ApplyGrip(AnimationStream stream, ref HandGripChainJob ch, float grip)
        {
            if (grip < 0.005f) return;

            float aA = ch.flexA * ch.infA * ch.sgnA * grip;
            if (Mathf.Abs(aA) > 0.05f && ch.axisA.sqrMagnitude > 0.25f && ch.root.IsValid(stream)
                && IsValidRel(ch.q01Rel))
            {
                Quaternion bind01 = ch.q01Rel * Quaternion.AngleAxis(aA, ch.axisA.normalized);
                Quaternion q1 = ch.root.GetLocalRotation(stream);
                ch.root.SetLocalRotation(stream, Quaternion.Slerp(q1, bind01, grip));
            }

            float aB = ch.flexB * ch.infB * ch.sgnB * grip;
            if (Mathf.Abs(aB) > 0.05f && ch.axisB.sqrMagnitude > 0.25f && ch.mid.IsValid(stream)
                && IsValidRel(ch.q12Rel))
            {
                Quaternion bind12 = ch.q12Rel * Quaternion.AngleAxis(aB, ch.axisB.normalized);
                Quaternion q2 = ch.mid.GetLocalRotation(stream);
                ch.mid.SetLocalRotation(stream, Quaternion.Slerp(q2, bind12, grip));
            }
        }

        static bool IsValidRel(Quaternion q)
        {
            return q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w > 0.5f;
        }

        // 带动臂模式的两骨骼 IK（仅在 armIK=true 时使用）
        void SolveArmIK(AnimationStream stream, Vector3 targetPos, Quaternion targetRot, float w)
        {
            Vector3 aPosition = armRoot.GetPosition(stream);
            Vector3 bPosition = armMid.GetPosition(stream);
            Vector3 cPosition = wrist.GetPosition(stream);
            Vector3 tPosition = Vector3.Lerp(cPosition, targetPos, w);
            Quaternion tRotation = Quaternion.Slerp(wrist.GetRotation(stream), targetRot, w);

            Vector3 ab = bPosition - aPosition;
            Vector3 bc = cPosition - bPosition;
            Vector3 ac = cPosition - aPosition;
            Vector3 at = tPosition - aPosition;
            float abLen = ab.magnitude;
            float bcLen = bc.magnitude;
            float atLen = at.magnitude;

            float oldAng = TriangleAngle(ac.magnitude, abLen, bcLen);
            float newAng = TriangleAngle(atLen, abLen, bcLen);

            Vector3 axis = Vector3.Cross(ab, bc);
            if (axis.sqrMagnitude < 1e-8f)
            {
                axis = Vector3.Cross(at, bc);
                if (axis.sqrMagnitude < 1e-8f) axis = Vector3.up;
            }
            axis.Normalize();

            float a = (oldAng - newAng);
            Quaternion dR = Quaternion.AngleAxis(Mathf.Rad2Deg * a, axis);
            armMid.SetRotation(stream, dR * armMid.GetRotation(stream));

            Vector3 c2 = wrist.GetPosition(stream);
            Vector3 ac2 = c2 - aPosition;
            if (ac2.sqrMagnitude < 1e-8f) ac2 = ac;
            Quaternion rootR = Quaternion.FromToRotation(ac2, at) * armRoot.GetRotation(stream);
            armRoot.SetRotation(stream, rootR);
            wrist.SetRotation(stream, tRotation);
        }

        static float TriangleAngle(float aLen, float a1, float a2)
        {
            float c = Mathf.Clamp((a1 * a1 + a2 * a2 - aLen * aLen) / (a1 * a2) / 2f, -1f, 1f);
            return Mathf.Acos(c);
        }

        static void Pass(AnimationStream s, ReadWriteTransformHandle h)
        {
            if (!h.IsValid(s)) return;
            h.GetLocalTRS(s, out var p, out var r, out var q);
            h.SetLocalTRS(s, p, r, q);
        }
    }

    /// <summary>纯数学工具（Binder/分析器/编辑器预览共用）。</summary>
    public static class HandGripMath
    {
        /// <summary>双轴约束旋转：m0->t0，再绕 t0 旋转使 m1 对齐 t1。</summary>
        public static Quaternion FromTwoAxes(Vector3 m0, Vector3 t0, Vector3 m1, Vector3 t1)
        {
            m0 = m0.normalized; t0 = t0.normalized;
            m1 = (m1 - m0 * Vector3.Dot(m1, m0)).normalized;
            t1 = (t1 - t0 * Vector3.Dot(t1, t0)).normalized;
            if (m1.sqrMagnitude < 1e-6f || t1.sqrMagnitude < 1e-6f)
                return Quaternion.FromToRotation(m0, t0);
            var q = Quaternion.FromToRotation(m0, t0);
            var h0 = q * m1;
            var h1 = t1;
            var n = Vector3.Cross(h0, h1);
            if (n.sqrMagnitude < 1e-8f) return q;
            float ang = Vector3.Angle(h0, h1);
            return Quaternion.AngleAxis(ang, n.normalized) * q;
        }
    }
}
