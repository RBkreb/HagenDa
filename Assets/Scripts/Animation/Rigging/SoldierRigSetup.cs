using System;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Animations.Rigging;

namespace HagenDa.Animation.Rigging
{
    /// <summary>
    /// 士兵 rig 运行时配套。与编辑器 bake 出的 RigBuilder 结构协作(结构约定:
    /// RigBuilder 挂在 Animator 节点;Rig 名 Rig_LowerBody / Rig_UpperBody / Rig_Hands;
    /// 驱动物体在 Rig_Drivers 下)。运行时职责:
    ///  - 缓存约束与驱动器(按类型/名称查找,缺失的驱动物体防御性补建)
    ///  - 每帧脚部贴地 raycast(脚 IK 权重 > 0 时),写入脚部 IK 目标
    ///  - 驱动 API(SetHipsYaw / SetBodyOffset / SetAimPose / SetWeights /
    ///    SetWeaponAnchorHeight)供 NetworkSoldierAnimator 每帧调用
    ///  - BindWeapon / ClearWeapon:绑定第三人称枪模(双手 IK 目标 + HandGrip
    ///    握把胶囊 + 枪 AimConstraint),完成后重建 rig jobs
    ///  - 总开关(跨域重载存活,须 SerializeField)
    /// 所有平滑/滞后状态在 NetworkSoldierAnimator 侧,本组件只应用。
    /// </summary>
    [DisallowMultipleComponent]
    public class SoldierRigSetup : MonoBehaviour
    {
        [Header("Foot IK (ground align)")]
        public LayerMask groundMask = ~0;
        public float footHeight = 0.08f;
        public float footRayUp = 0.6f;
        public float footRayDown = 1.4f;
        public float footHintForward = 0.35f;
        public float footHintUp = 0.4f;

        [Header("Weapon (gun aim roll)")]
        [Tooltip("枪模 AimConstraint 的 upVector(枪本地轴,对齐角色上方)。默认 (0,-1,0):该枪模顶部沿本地 -Y,用默认 +Y 会绕枪管翻 180°(拿反)。")]
        public Vector3 gunAimUpVector = new Vector3(0f, -1f, 0f);

        [Header("Master switch")]
        [SerializeField] private bool rigEnabled = true;

        public bool RigEnabled => rigEnabled;

        // ---- cached rig pieces (found by type/name at runtime) ----
        private RigBuilder rigBuilder;
        private Animator animator;
        private HipsPoseConstraint hipsPose;
        private MultiPositionConstraint hipsPos;   // hips 位置约束(蹲/趴 bodyposition 下移)
        private SpineAimConstraint spineAim;
        private TwoBoneIKConstraint footIKL, footIKR;
        private TwoBoneIKConstraint handIKR, handIKL;
        private HandGripConstraint gripR, gripL;
        private SkeletonPoseConstraint crouchPose, pronePose;
        private Transform footTipL, footTipR;
        private Transform yawDriver, offsetDriver, aimSource, aimTarget, weaponAnchor;
        private Transform footTargetL, footTargetR, footHintL, footHintR;

        private GameObject boundGun;
        private AimConstraint gunAim;
        private bool cacheBuilt;
        private float footWeightL, footWeightR;

        private static Transform FindDeep(Transform root, string name)
        {
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                var r = FindDeep(root.GetChild(i), name);
                if (r != null) return r;
            }
            return null;
        }

        private static Transform EnsureChild(Transform parent, string name, Vector3? createLocalPos = null)
        {
            var t = FindDeep(parent, name);
            if (t != null) return t;   // 已烘进预制体:直接复用,不覆盖用户调好的位置
            t = new GameObject(name).transform;
            t.SetParent(parent, false);
            if (createLocalPos.HasValue) t.localPosition = createLocalPos.Value;
            return t;
        }

        private void OnEnable()
        {
            EnsureCache();
        }

        private void EnsureCache()
        {
            if (cacheBuilt) return;

            animator = GetComponentInChildren<Animator>(true);
            if (animator == null)
            {
                Debug.LogWarning($"[SoldierRigSetup] {name}: no Animator in children", this);
                return;
            }

            rigBuilder = animator.GetComponent<RigBuilder>();
            if (rigBuilder == null)
            {
                Debug.LogWarning($"[SoldierRigSetup] {name}: no RigBuilder on {animator.name} — rig 未 bake?", this);
                return;
            }

            var all = animator.GetComponentsInChildren<MonoBehaviour>(true);
            foreach (var c in all)
            {
                switch (c)
                {
                    case HipsPoseConstraint h: hipsPose = h; break;
                    case MultiPositionConstraint mp: hipsPos = mp; break;
                    case SpineAimConstraint s: spineAim = s; break;
                    case TwoBoneIKConstraint ik:
                    {
                        string tip = ik.data.tip != null ? ik.data.tip.name : string.Empty;
                        bool isFoot = tip.Contains("Foot") || tip.Contains("foot");
                        bool isLeft = tip.Contains(".L") || tip.Contains("_L");
                        if (isFoot)
                        {
                            if (isLeft) { footIKL = ik; footTipL = ik.data.tip; }
                            else { footIKR = ik; footTipR = ik.data.tip; }

                            // 脚 IK 只钉位置,不强制旋转:脚骨 forward 轴非标准,
                            // UpdateFoot 里的 LookRotation(实体forward,法线) 会把脚翻成鞋底朝上。
                            // 旋转由 locomotion 剪辑保持(鞋底自然朝下)。
                            var fd = ik.data;
                            fd.targetRotationWeight = 0f;
                            ik.data = fd;
                        }
                        else
                        {
                            if (isLeft) handIKL = ik;
                            else handIKR = ik;
                        }
                        break;
                    }
                    case HandGripConstraint g:
                        if (g.data.side == HandGripSide.Left) gripL = g;
                        else gripR = g;
                        break;
                    case SkeletonPoseConstraint sp:
                        if (sp.name == "CrouchPose") crouchPose = sp;
                        else if (sp.name == "PronePose") pronePose = sp;
                        break;
                }
            }

            var driverRoot = EnsureChild(animator.transform, "Rig_Drivers");
            yawDriver = EnsureChild(driverRoot, "Driver_Yaw");
            offsetDriver = EnsureChild(driverRoot, "Driver_Offset");
            aimSource = EnsureChild(driverRoot, "Driver_AimSource");
            aimTarget = EnsureChild(driverRoot, "Driver_AimTarget");
            weaponAnchor = EnsureChild(driverRoot, "WeaponAnchor");
            footTargetL = EnsureChild(driverRoot, "Driver_FootTarget_L");
            footTargetR = EnsureChild(driverRoot, "Driver_FootTarget_R");
            footHintL = EnsureChild(driverRoot, "Driver_FootHint_L");
            footHintR = EnsureChild(driverRoot, "Driver_FootHint_R");

            // IK 目标引用回填(编辑器 bake 时已建,这里兜底)
            if (footIKL != null && footIKL.data.target == null) footIKL.data.target = footTargetL;
            if (footIKL != null && footIKL.data.hint == null) footIKL.data.hint = footHintL;
            if (footIKR != null && footIKR.data.target == null) footIKR.data.target = footTargetR;
            if (footIKR != null && footIKR.data.hint == null) footIKR.data.hint = footHintR;

            cacheBuilt = true;
            ApplyMasterSwitch();
        }

        // ---------------------------------------------------------------
        // MASTER SWITCH
        // ---------------------------------------------------------------

        public void SetRigEnabled(bool on)
        {
            rigEnabled = on;
            ApplyMasterSwitch();
        }

        private void ApplyMasterSwitch()
        {
            if (rigBuilder != null) rigBuilder.enabled = rigEnabled;
        }

        // ---------------------------------------------------------------
        // PER-FRAME: foot ground align
        // ---------------------------------------------------------------

        private void Update()
        {
            if (!rigEnabled || !cacheBuilt) return;
            if (footWeightL > 0.01f && footTipL != null) UpdateFoot(footTipL, footTargetL, footHintL);
            if (footWeightR > 0.01f && footTipR != null) UpdateFoot(footTipR, footTargetR, footHintR);
        }

        private void UpdateFoot(Transform tip, Transform target, Transform hint)
        {
            Vector3 ankle = tip.position;
            Vector3 origin = ankle + Vector3.up * footRayUp;
            if (Physics.Raycast(origin, Vector3.down, out var hit,
                                footRayUp + footRayDown, groundMask, QueryTriggerInteraction.Ignore))
            {
                target.position = hit.point + Vector3.up * footHeight;
                Vector3 fwd = transform.forward;
                fwd.y = 0f;
                fwd.Normalize();
                target.rotation = Quaternion.LookRotation(fwd, hit.normal);
            }
            else
            {
                target.position = ankle;
            }

            if (hint != null)
            {
                Vector3 fwd = transform.forward;
                fwd.y = 0f;
                fwd.Normalize();
                hint.position = target.position + fwd * footHintForward + Vector3.up * footHintUp;
            }
        }

        // ---------------------------------------------------------------
        // DRIVE API (NetworkSoldierAnimator 每帧调用)
        // ---------------------------------------------------------------

        /// <summary>下半身滞后朝向(世界 yaw,度)。</summary>
        public void SetHipsYaw(float yawDegrees)
        {
            if (yawDriver != null)
                yawDriver.rotation = Quaternion.Euler(0f, yawDegrees, 0f);
        }

        /// <summary>root 空间身体位移(蹲/趴/滑铲),平滑由调用方完成。</summary>
        public void SetBodyOffset(Vector3 rootSpaceOffset)
        {
            if (offsetDriver != null)
                offsetDriver.localPosition = rootSpaceOffset;
        }

        /// <summary>hips 位置约束权重(蹲/趴 bodyposition 下移的开启度,0..1)。</summary>
        public void SetHipsPosWeight(float w)
        {
            if (hipsPos != null)
                hipsPos.weight = Mathf.Clamp01(w);
        }

        /// <summary>瞄准源:眼睛位置 + 瞄准方向(世界)。</summary>
        public void SetAimPose(Vector3 origin, Quaternion direction)
        {
            if (aimSource != null)
            {
                aimSource.position = origin;
                aimSource.rotation = direction;
            }
        }

        /// <summary>瞄准目标点(枪 AimConstraint 的 source)。</summary>
        public void SetAimTarget(Vector3 pos)
        {
            if (aimTarget != null)
                aimTarget.position = pos;
        }

        /// <summary>枪械挂点高度(ADS 时由胸口平滑升到眼高)。</summary>
        public void SetWeaponAnchorPosition(Vector3 worldPos)
        {
            if (weaponAnchor != null)
                weaponAnchor.position = worldPos;
        }

        public Transform WeaponAnchor => weaponAnchor;
        public Transform AimTarget => aimTarget;
        public Transform YawDriver => yawDriver;      // 验证/调试用
        public Transform OffsetDriver => offsetDriver;
        public Transform FootTipL => footTipL;        // 脚 IK 链 tip(踝),供驱动侧做高度门控
        public Transform FootTipR => footTipR;

        // ---------------------------------------------------------------
        // WEIGHTS (0..1,平滑由调用方完成)
        // ---------------------------------------------------------------

        public void SetHipsWeight(float w) { if (hipsPose != null) hipsPose.weight = Mathf.Clamp01(w); }
        public void SetSpineWeight(float w) { if (spineAim != null) spineAim.weight = Mathf.Clamp01(w); }
        public void SetHandsWeight(float w)
        {
            w = Mathf.Clamp01(w);
            if (handIKR != null) handIKR.weight = w;
            if (handIKL != null) handIKL.weight = w;
            if (gripR != null) gripR.weight = w;
            if (gripL != null) gripL.weight = w;
        }

        public void SetFootIKWeight(float left, float right)
        {
            footWeightL = Mathf.Clamp01(left);
            footWeightR = Mathf.Clamp01(right);
            if (footIKL != null) footIKL.weight = footWeightL;
            if (footIKR != null) footIKR.weight = footWeightR;
        }

        /// <summary>蹲/趴 TRS 姿态权重(0..1,平滑由调用方完成)。</summary>
        public void SetPoseWeights(float crouch, float prone)
        {
            if (crouchPose != null) crouchPose.weight = Mathf.Clamp01(crouch);
            if (pronePose != null) pronePose.weight = Mathf.Clamp01(prone);
        }

        /// <summary>死亡/复活等场景:一键全零(或恢复 1)。</summary>
        public void SetAllWeights(float w)
        {
            SetHipsWeight(w);
            SetSpineWeight(w);
            SetFootIKWeight(w, w);
            SetHandsWeight(w);
            SetPoseWeights(w, w);
        }

        // ---------------------------------------------------------------
        // WEAPON BINDING
        // ---------------------------------------------------------------

        /// <summary>
        /// 绑定第三人称枪模:右手 IK 目标 = GripRoot,左手 = ForeGrip,HandGrip
        /// 握把胶囊 = 枪内首个 CapsuleCollider,枪根加 AimConstraint(source =
        /// Driver_AimTarget)。枪的约定子物体:GripRoot / ForeGrip / AimSource。
        /// </summary>
        public void BindWeapon(GameObject gun)
        {
            EnsureCache();
            ClearWeapon();
            if (gun == null || !cacheBuilt) return;

            boundGun = gun;
            // 约定(与 HandGripRigBuilder 演示一致):枪子物体 GripCapR/GripCapL
            // (握把胶囊),各自子物体 GripRootR/GripRootL(手腕 IK 目标),AimSource
            // (枪管轴向锚)。CapsuleCollider 按手分配:右=握把,左=护木。
            var gripRootR = FindDeep(gun.transform, "GripRootR");
            var gripRootL = FindDeep(gun.transform, "GripRootL");
            var capR = FindDeep(gun.transform, "GripCapR")?.GetComponent<CapsuleCollider>();
            var capL = FindDeep(gun.transform, "GripCapL")?.GetComponent<CapsuleCollider>();

            if (handIKR != null && gripRootR != null)
            {
                handIKR.data.target = gripRootR;
                // hint 已烘进预制体时直接复用(位置以预制体为准);缺失才兜底创建+定位
                handIKR.data.hint = EnsureChild(gripRootR, "HandHint_R", new Vector3(0.06f, -0.35f, 0f));
            }
            if (handIKL != null && gripRootL != null)
            {
                handIKL.data.target = gripRootL;
                handIKL.data.hint = EnsureChild(gripRootL, "HandHint_L", new Vector3(-0.06f, -0.35f, 0f));
            }

            if (gripR != null) gripR.data.grip = capR;
            if (gripL != null) gripL.data.grip = capL;

            if (aimTarget != null)
            {
                gunAim = gun.GetComponent<AimConstraint>();
                if (gunAim == null) gunAim = gun.AddComponent<AimConstraint>();

                // sources 清空后只留瞄准目标点
                var sources = new System.Collections.Generic.List<ConstraintSource>();
                gunAim.GetSources(sources);
                sources.Clear();
                sources.Add(new ConstraintSource { sourceTransform = aimTarget, weight = 1f });
                gunAim.SetSources(sources);
                // AddComponent 添加的约束默认 constraintActive=false(编辑器 UI 才会自动勾选),
                // 不激活则枪永远不跟随瞄准目标。
                gunAim.constraintActive = true;
                gunAim.upVector = gunAimUpVector;   // 滚转基准:顶面朝上(默认 +Y 对该枪模是拿反 180°)
                gunAim.worldUpType = AimConstraint.WorldUpType.ObjectUp;
                gunAim.worldUpObject = animator.transform;
                // 瞄准轴 = 枪根 → AimSource 的本地方向(AimSource 预放在枪管轴向上)
                var aimAxisChild = FindDeep(gun.transform, "AimSource");
                if (aimAxisChild != null)
                {
                    Vector3 localDir = gun.transform.InverseTransformPoint(aimAxisChild.position);
                    if (localDir.sqrMagnitude > 1e-6f)
                        gunAim.aimVector = localDir.normalized;
                }
            }

            RebuildRig();
        }

        public void ClearWeapon()
        {
            if (!cacheBuilt) return;
            if (handIKR != null) { handIKR.data.target = null; handIKR.data.hint = null; }
            if (handIKL != null) { handIKL.data.target = null; handIKL.data.hint = null; }
            if (gripR != null) gripR.data.grip = null;
            if (gripL != null) gripL.data.grip = null;
            // AimConstraint 可能已烘进枪预制体:停用+清空 sources,不销毁(解绑/重绑周期内保留烘焙组件)
            if (gunAim != null)
            {
                gunAim.constraintActive = false;
                var srcs = new System.Collections.Generic.List<ConstraintSource>();
                gunAim.GetSources(srcs);
                srcs.Clear();
                gunAim.SetSources(srcs);
            }
            gunAim = null;
            boundGun = null;
            RebuildRig();
        }

        public GameObject BoundWeapon => boundGun;

        private void RebuildRig()
        {
            if (rigBuilder != null && Application.isPlaying && rigBuilder.enabled)
                rigBuilder.Build();
        }
    }
}