using System;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Animations.Rigging;
using HagenDa.Animation.Rigging;

namespace HagenDa.Animation.RigGraph
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
        [Tooltip("枪模 AimConstraint 的 upVector(枪本地轴,对齐世界上方)。默认 (0,1,0):该枪模顶面沿本地 +Y" +
                 "(照门/枪机在 +Y 侧)。BindWeapon 会按照门位置自动求此轴,一般无需手改。")]
        public Vector3 gunAimUpVector = new Vector3(0f, 1f, 0f);

        [Header("Weapon presentation (PHASE14 枪械专项)")]
        [Tooltip("左手握点:true=弹匣(MagGrip),false=枪管/护木(BarrelGrip)。默认枪管。")]
        public bool leftHandUsesMagGrip = false;
        [Tooltip("瞄准(ADS)时照门到眼睛的距离(m) —— 可调瞳距。")]
        public float adsEyeRelief = 0.13f;
        [Tooltip("冲刺时枪相对基准下压的后撤量(m)。")]
        public float sprintDropback = 0.06f;
        [Tooltip("冲刺摆枪(局部X,度)振幅。")]
        public float sprintSwayAmplitude = 5f;
        [Tooltip("冲刺摆枪频率(Hz)。")]
        public float sprintSwayFrequency = 2.2f;
        [Tooltip("后座抬枪(度/单位recoil)。")]
        public float recoilPitchPerUnit = 1.2f;
        [Tooltip("后座后坐下沉(度/单位recoil)。")]
        public float recoilYawPerUnit = 0.35f;
        [Tooltip("后座位移后撤(m/单位recoil)。")]
        public float recoilKickPerUnit = 0.004f;
        [Tooltip("枪机(Bolt)前向界限(局部z)。")]
        public float boltForwardLimit = 0.156f;
        [Tooltip("枪机(Bolt)后向界限(局部z)。")]
        public float boltRearLimit = 0.185f;
        [Tooltip("枪机往复速度(m/s)。")]
        public float boltSpeed = 3.5f;

        [Header("Master switch")]
        [SerializeField] private bool rigEnabled = true;

        public bool RigEnabled => rigEnabled;

        // ---- cached rig pieces (found by type/name at runtime; serialized so a
        //      script reload in Play mode doesn't null them and silently break the
        //      driver's WeaponAnchor / weight calls) ----
        [SerializeField] private RigBuilder rigBuilder;
        [SerializeField] private Animator animator;
        [SerializeField] private HipsPoseConstraint hipsPose;
        [SerializeField] private MultiPositionConstraint hipsPos;   // hips 位置约束(蹲/趴 bodyposition 下移)
        [SerializeField] private SpineAimConstraint spineAim;
        [SerializeField] private TwoBoneIKConstraint footIKL, footIKR;
        [SerializeField] private TwoBoneIKConstraint handIKR, handIKL;
        [SerializeField] private HandGripConstraint gripR, gripL;
        [SerializeField] private SkeletonPoseConstraint crouchPose, pronePose;
        [SerializeField] private Transform footTipL, footTipR;
        [SerializeField] private Transform yawDriver, offsetDriver, aimSource, aimTarget, weaponAnchor;
        [SerializeField] private Transform footTargetL, footTargetR, footHintL, footHintR;
        [SerializeField] private Transform sprintAimSource;

        [Header("Weapon basis (PHASE14 角色结构)")]
        [Tooltip("枪械模型基准。PHASE14 结构:胶囊根 -> 角色模型 + 枪械基准(同级)-> 枪模。\n" +
                 "基准在模型之外(动画层级外),故 IK 目标读的是实时 Transform,不会经\n" +
                 "AnimationStream 读到过期值。留空时由 BindWeapon 取枪的父级自动填充。")]
        [SerializeField] private Transform weaponBasis;
        [Tooltip("若无头部 AimSource 时的回落:让枪跟随胸骨(否则停在原位)。")]
        [SerializeField] private bool weaponFollowChest = true;
        [Tooltip("腰射持枪位:枪械基准相对头部 AimSource 的**持久偏移**(相机空间,m)。\n" +
                 "AimSource 随相机朝向旋转,故该偏移被整体带转 → 枪恒定停在视野右下角。\n" +
                 "x=右,y=下,z=前。z 受**手臂可及性**约束:RearGrip 在枪原点后 ≈0.23m,\n" +
                 "相机又比身体轴线前伸 ≈0.25m;z 过大(>0.4)会把枪推到手臂够不到的地方\n" +
                 "(右肩->后握把 > 手臂长 0.63m),手就会脱把/枪看起来跑到身后。")]
        public Vector3 hipHoldOffset = new Vector3(0.05f, -0.24f, 0.20f);

        private GameObject boundGun;
        private AimConstraint gunAim;
        private bool cacheBuilt;
        private bool pendingRebuild;      // 绑定后等 Animator 就绪再重建一次动画图
        private Transform modelRoot;      // 角色模型根(= animator 节点,胶囊的子物体)
        private Vector3 modelBaseLocalPos;// 模型根的静止局部位置(蹲/趴位移的基准)
        private float footWeightL, footWeightR;
        private float footPlantL, footPlantR;          // 0..1 触地程度(驱动侧给;冻结世界位置防滑动)
        private bool footFrozenL, footFrozenR;
        private Vector3 footFrozenPosL, footFrozenPosR;
        private float footFrozenYawL, footFrozenYawR;
        private float footYawLag;                 // 脚 IK 点滞后朝向(世界 yaw,度)
        private Vector3 footPivot;                // 公转轴心(身体竖轴上的点)
        private Transform chestBone;
        private Transform aimBone;              // aimSource 的父骨(头),诊断用
        private Vector3 adsEyePos;              // ADS 目标眼位(驱动侧每帧给出)
        private bool adsEyeValid;
        private bool weaponFollowInit;
        private int weaponBaseSetFrame = -1;

        /// <summary>
        /// 腰射枪位 = AimSource 位姿 ∘ 持久偏移。AimSource 是头部骨骼的子对象、随相机
        /// 水平/俯仰旋转,故偏移被整体带转:枪恒定停在**视野右下角**(PHASE14 腰射)。
        /// </summary>
        private Vector3 HipHoldWorld()
        {
            if (aimSource == null) return Vector3.zero;
            return aimSource.position
                 + aimSource.right * hipHoldOffset.x
                 + aimSource.up * hipHoldOffset.y
                 + aimSource.forward * hipHoldOffset.z;
        }

        /// <summary>腰射枪朝向 = AimSource 朝向(与头部/相机一致)。</summary>
        private Quaternion HipHoldRotation() => aimSource != null ? aimSource.rotation : transform.rotation;

        /// <summary>
        /// ADS 眼位(世界)。驱动侧每帧给出;枪位 = lerp(腰射位, 眼位, 瞄准量)。
        /// 瞳距由 adsEyeRelief 沿枪管轴后移实现。
        /// </summary>
        public void SetWeaponAimEye(Vector3 worldEye)
        {
            adsEyePos = worldEye;
            adsEyeValid = true;
        }

        // ---- weapon presentation cache (PHASE14 枪械专项) ----
        private Transform gunBolt, gunRearSight, gunFrontSight;
        private Transform gunRearAim;                       // RearAim(照门锚,ADS 求解用)
        private Transform gunSightAim;                      // SightAim(准星锚,与 RearAim 共线=枪管轴)
        private Vector3 gunAimLocalAxis = Vector3.forward;   // 枪管轴向(枪根局部)
        private Vector3 gunAimLocalUp = Vector3.up;          // 枪“上方”(枪根局部)
        private Quaternion gunLocalRot = Quaternion.identity;// 枪相对 WeaponAnchor 的固定局部旋转
        private Vector3 weaponAnchorBase;                    // 本帧基准位置(未叠后座/冲刺偏移)
        private bool weaponAnchorBaseValid;
        private float boltPhase;                              // 枪机往复相位(0=前向界限)
        private uint lastShotCount;
        private float sprintSwayPhase;
        private float adsSmoothing;                            // 内部平滑(0..1)

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

            // 脚本重载会清掉非序列化引用(animator/weaponAnchor 等)。这些字段已
            // SerializeField,重载后仍有效,但 cacheBuilt 是普通 bool 也会被复位,
            // 故这里做一次“缓存失效自愈”:任一关键引用为空就重建。
            if (animator != null && rigBuilder != null && weaponAnchor != null &&
                footTargetL != null && yawDriver != null)
            {
                cacheBuilt = true;
                if (modelRoot == null) modelRoot = animator.transform;
                if (hipsPose != null) { hipsPose.weight = 0f; hipsPose.enabled = false; }
                if (hipsPos != null) { hipsPos.weight = 0f; hipsPos.enabled = false; }
                ApplyMasterSwitch();
                return;
            }

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
            aimTarget = EnsureChild(driverRoot, "Driver_AimTarget");
            weaponAnchor = EnsureChild(driverRoot, "WeaponAnchor");
            footTargetL = EnsureChild(driverRoot, "Driver_FootTarget_L");
            footTargetR = EnsureChild(driverRoot, "Driver_FootTarget_R");
            footHintL = EnsureChild(driverRoot, "Driver_FootHint_L");
            footHintR = EnsureChild(driverRoot, "Driver_FootHint_R");

            // 瞄准源(PHASE14 腰射/脊柱瞄准):必须挂**动画骨架之外**(Rig_Drivers 下),
            // 与枪械基准同理。原因:动画骨架内(Avatar 骨骼子物体)的 Transform 由
            // AnimationStream 持有独立副本;驱动侧在 Update 里写它的世界位姿,rig 作业
            // 经 stream 读到的却是"动画旧值" → SpineAim 永远算不出反扭(上半身不转)。
            // 骨架外的 Transform 无流副本,绑定为句柄时读的是实时场景值(双手 IK 目标
            // 正是如此,故握把误差 0.0000)。**不可**再用 FindDeep 全局搜 "AimSource":
            // 枪模自带同名子物体,搜到它会让枪位由自己的子物体推导(自反馈 → 枪飞头顶)。
            aimSource = FindDeep(driverRoot, "Driver_AimSource");
            if (aimSource == null) aimSource = EnsureChild(driverRoot, "Driver_AimSource");
            aimBone = aimSource.parent;   // Rig_Drivers(诊断用)

            // HipsPose 约束(旧方案:约束作业写 Humanoid 根骨骼)实测不生效,且会拖垮
            // 整个 Rig_LowerBody 层(脚部 IK 一起失效)。下半身朝向改用模型根旋转后,
            // 这里永久停用该约束;MultiPosition(HipsPos)同理常关。
            if (hipsPose != null) { hipsPose.weight = 0f; hipsPose.enabled = false; }
            if (hipsPos != null) { hipsPos.weight = 0f; hipsPos.enabled = false; }

            // 模型根 = Animator 所在节点(胶囊的子物体)。下半身朝向/蹲趴位移直接作用
            // 在它上面 —— 绕身体轴旋转整个下半身,避免 Humanoid 根约束失效的问题。
            modelRoot = animator.transform;
            modelBaseLocalPos = modelRoot.localPosition;

            // SpineAim 的 aimSource 必须正是这里驱动的那一个。Fatui 预制体上烘的是
            // Driver_AimSource(无人驱动 → 上半身永不跟随瞄准);运行时统一点到头部
            // AimSource,保证脊柱反扭与相机朝向一致。
            if (spineAim != null && spineAim.data.aimSource != aimSource)
            {
                var sd = spineAim.data;
                sd.aimSource = aimSource;
                spineAim.data = sd;
            }

            // 冲刺持枪基准(PHASE14:枪自身 AimConstraint 源 = 人物对象 SprintAim)。
            // 预制体上通常已摆好;缺失则建在右前下方(收枪位)。
            sprintAimSource = FindDeep(animator.transform, "SprintAim");
            if (sprintAimSource == null)
            {
                sprintAimSource = EnsureChild(animator.transform, "SprintAim");
                sprintAimSource.localPosition = new Vector3(0.22f, 1.05f, 0.30f);
                sprintAimSource.localRotation = Quaternion.Euler(28f, 0f, 0f);   // 枪口下压的收枪位
            }

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
            // 绑定后等 Animator 就绪,再重建一次动画图 —— 只此一次,不再有逐帧自愈。
            // OnEnable 阶段 Avatar/骨骼映射常未就绪,那时建的图会把骨骼句柄绑错。
            if (pendingRebuild && animator != null && animator.isInitialized)
            {
                pendingRebuild = false;
                RebuildRig();
            }
            if (footWeightL > 0.01f && footTipL != null)
                UpdateFoot(footTipL, footTargetL, footHintL, footPlantL,
                           ref footFrozenL, ref footFrozenPosL, ref footFrozenYawL);
            if (footWeightR > 0.01f && footTipR != null)
                UpdateFoot(footTipR, footTargetR, footHintR, footPlantR,
                           ref footFrozenR, ref footFrozenPosR, ref footFrozenYawR);
        }

        /// <summary>
        /// 单脚地面 IK。plant 高(&gt;0.5)时冻结世界 XZ(只贴实时地面高度),并让该脚
        /// **绕身体竖轴按 footYawLag 滞后公转** —— 下半身转向时脚不立即跟着转,而是
        /// 滞后地"挪步"绕过身体轴,直到追上身体朝向(PHASE14:下半身=脚IK滞后同步;
        /// 胯部随身体转,脚 IK 点滞后同步)。
        /// plant 低(抬脚)时释放冻结,跟随脚骨实时位置。
        /// </summary>
        private void UpdateFoot(Transform tip, Transform target, Transform hint, float plant,
                                ref bool frozen, ref Vector3 frozenPos, ref float frozenYaw)
        {
            Vector3 ankle = tip.position;
            Vector3 origin = ankle + Vector3.up * footRayUp;
            if (Physics.Raycast(origin, Vector3.down, out var hit,
                                footRayUp + footRayDown, groundMask, QueryTriggerInteraction.Ignore))
            {
                Vector3 ground = hit.point + Vector3.up * footHeight;
                bool wantFreeze = plant > 0.5f;
                if (wantFreeze)
                {
                    if (!frozen) { frozenPos = ground; frozenYaw = footYawLag; frozen = true; }
                    // 触地脚:绕身体竖轴(pivot)按 lag 变化量公转 —— 身体转了、脚滞后跟上。
                    Quaternion orbit = Quaternion.Euler(0f, footYawLag - frozenYaw, 0f);
                    Vector3 planted = footPivot + orbit * (frozenPos - footPivot);
                    target.position = new Vector3(planted.x, ground.y, planted.z);
                }
                else
                {
                    frozen = false;
                    target.position = ground;
                }
                Vector3 fwd = transform.forward;
                fwd.y = 0f;
                fwd.Normalize();
                target.rotation = Quaternion.LookRotation(fwd, hit.normal);
            }
            else
            {
                frozen = false;
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

        /// <summary>
        /// 下半身(滞后)朝向,单位度。**通过旋转模型根节点实现**,而不是 HipsPose 约束。
        ///
        /// 原因(实测):约束作业写 Humanoid 根骨骼(Hips=armature)不生效,且该作业失败会
        /// 拖垮整个 Rig_LowerBody 层 —— 连同一层的脚部 IK 一起失效(脚不贴地、不随目标)。
        /// 模型根是胶囊的子物体(与枪械基准同级),直接旋转它即可让整个下半身**绕身体轴**
        /// 转过来(腿部不再交叉);上半身的差速由 SpineAim 在模型根基础上反扭补齐。
        /// </summary>
        public void SetHipsYaw(float yawDegrees)
        {
            if (modelRoot == null) return;
            float parentYaw = modelRoot.parent != null ? modelRoot.parent.eulerAngles.y : 0f;
            modelRoot.localRotation = Quaternion.Euler(0f, yawDegrees - parentYaw, 0f);
        }

        /// <summary>
        /// 脚 IK 点的滞后朝向(世界 yaw,度)+ 公转轴心(身体竖轴上的点)。
        /// 胯部随身体转,脚 IK 点按此滞后值绕轴心公转,滞后追上(PHASE14)。
        /// </summary>
        public void SetFootLagYaw(float yawDegrees, Vector3 pivot)
        {
            footYawLag = yawDegrees;
            footPivot = pivot;
        }

        /// <summary>
        /// root 空间身体位移(蹲/趴/滑铲),平滑由调用方完成。同样直接移动模型根节点 ——
        /// 原 MultiPositionConstraint 也约束 Humanoid 根,存在与 HipsPose 相同的失效风险。
        /// </summary>
        public void SetBodyOffset(Vector3 rootSpaceOffset)
        {
            if (modelRoot != null) modelRoot.localPosition = modelBaseLocalPos + rootSpaceOffset;
        }

        /// <summary>hips 位置约束权重。已改用模型根位移,这里保留 API 但不再启用失效约束。</summary>
        public void SetHipsPosWeight(float w)
        {
            // 不写 hipsPos.weight:该约束约束 Humanoid 根,启用会破坏 Rig_LowerBody 层。
        }

        /// <summary>
        /// 瞄准源(PHASE14 腰射):眼睛位置 + 瞄准方向(世界)。
        /// AimSource 是头部骨骼的子对象,故写入世界位姿后随之运动;枪械基准由它导出,
        /// 于是枪恒定停在视野右下角(而不是绕枪自身中心旋转)。
        /// </summary>
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

        /// <summary>
        /// 显式指定枪械基准位置(如 NetworkSoldierAnimator 的 ADS 眼高混合)。
        /// 未在当帧调用时,SetWeaponState 会自动退回“胸骨跟随”。
        /// </summary>
        public void SetWeaponAnchorPosition(Vector3 worldPos)
        {
            weaponAnchorBase = worldPos;
            weaponAnchorBaseValid = true;
            weaponBaseSetFrame = Time.frameCount;
            var basis = ResolvedBasis;
            if (basis != null) basis.position = worldPos;
        }

        /// <summary>枪械基准:优先用外部(与模型同级)的 weaponBasis;无则用内部 Driver。</summary>
        private Transform ResolvedBasis => weaponBasis != null ? weaponBasis : weaponAnchor;

        /// <summary>枪械模型基准(PHASE14 结构:胶囊下、与模型同级)。NetworkGun 用它显隐枪。</summary>
        public Transform WeaponBasis => ResolvedBasis;

        public Transform WeaponAnchor => ResolvedBasis;
        public Transform AimTarget => aimTarget;
        public Transform YawDriver => yawDriver;      // 验证/调试用
        public Transform OffsetDriver => offsetDriver;
        public Transform FootTipL => footTipL;        // 脚 IK 链 tip(踝),供驱动侧做高度门控
        public Transform FootTipR => footTipR;

        /// <summary>
        /// 是否有姿态层(SkeletonPose 蹲/趴资产)。Natlan 有(Rig_Pose),趴下时由它
        /// 全权接管、髋部转向权重需让位;Fatui 无(趴用 body offset),髋部转向须恒开,
        /// 否则趴下无法转身。NetworkSoldierAnimator 据此决定 hips 权重。
        /// </summary>
        public bool HasPoseLayer => (crouchPose != null || pronePose != null);

        // ---------------------------------------------------------------
        // WEIGHTS (0..1,平滑由调用方完成)
        // ---------------------------------------------------------------

        // 髋部朝向已改由模型根旋转实现(SetHipsYaw),不再使用 HipsPose 约束。此处保留
        // API 供调用方语义清晰,但约束恒关。
        public void SetHipsWeight(float w) { /* 模型根驱动,无需约束权重 */ }
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

        /// <summary>
        /// 触地程度(0..1)。>0.5 时把该脚 IK 目标的世界位置冻结在冻结瞬间,身体
        /// 转身/平移时脚不再被拖走 → 原地"转脚"而非滑动;抬脚(&lt;0.5)即释放。
        /// 平滑/滞后状态由调用方负责。
        /// </summary>
        public void SetFootPlant(float left, float right)
        {
            footPlantL = Mathf.Clamp01(left);
            footPlantR = Mathf.Clamp01(right);
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
        /// 绑定枪械(枪械模型基准的子物体)。锚点解析(PHASE14):
        ///   RearGrip(后握把) + RearGripCap  -> 右手
        ///   BarrelGrip(枪管) / MagGrip(弹匣) + *GripCap -> 左手(leftHandUsesMagGrip 选择)
        ///   Rear_Sight/Sight -> 枪管轴向与上方向;  Bolt -> 枪机往复
        /// 关键:枪必须在**动画层级之外**(与模型同级挂在胶囊下),这样 IK 目标/胶囊
        /// 读的是实时 Transform,不会经 AnimationStream 读到过期值(手不动/枪不降的根因)。
        /// 绑定只重建一次动画图,并等 Animator 就绪后执行。
        /// </summary>
        public void BindWeapon(GameObject gun)
        {
            EnsureCache();
            if (gun == null || !cacheBuilt) return;

            boundGun = gun;
            // 枪械基准 = 枪的父级(PHASE14:基准是胶囊的子对象,与模型同级)
            if (gun.transform.parent != null && gun.transform.parent != animator.transform)
                weaponBasis = gun.transform.parent;

            // ---- 手部锚点 ----
            Transform rightAnchor = FindDeep(gun.transform, "RearGrip");
            Transform leftAnchor = FindDeep(gun.transform, leftHandUsesMagGrip ? "MagGrip" : "BarrelGrip");
            CapsuleCollider capR = FindDeep(gun.transform, "RearGripCap")?.GetComponent<CapsuleCollider>();
            CapsuleCollider capL = FindDeep(gun.transform, leftHandUsesMagGrip ? "MagGripCap" : "BarrelGripCap")?.GetComponent<CapsuleCollider>();

            if (rightAnchor == null || leftAnchor == null)
            {
                // 旧约定回落
                rightAnchor = FindDeep(gun.transform, "GripRootR");
                leftAnchor = FindDeep(gun.transform, "GripRootL");
                capR = FindDeep(gun.transform, "GripCapR")?.GetComponent<CapsuleCollider>();
                capL = FindDeep(gun.transform, "GripCapL")?.GetComponent<CapsuleCollider>();
            }

            if (handIKR != null && rightAnchor != null) handIKR.data.target = rightAnchor;
            if (handIKL != null && leftAnchor != null) handIKL.data.target = leftAnchor;
            if (gripR != null) gripR.data.grip = capR;
            if (gripL != null) gripL.data.grip = capL;

            InitWeaponPresentation(gun);
            pendingRebuild = true;   // 在 Update 里等 Animator 就绪后重建一次
        }

        /// <summary>
        /// 初始化武器“表现”缓存(照门/枪机/枪管轴/相对锚点的固定旋转/胸骨跟随)。
        /// 不动任何约束引用 —— 烘进预制体的枪直接调用本方法即可,无需重建 graph。
        /// </summary>
        public void InitWeaponPresentation(GameObject gun)
        {
            if (gun == null) return;
            boundGun = gun;

            // ---- 枪管轴向 / 上方向 ----
            gunRearSight = FindExact(gun.transform, "Rear_Sight") ?? FindDeep(gun.transform, "RearAim");
            gunFrontSight = FindExact(gun.transform, "Sight") ?? FindDeep(gun.transform, "SightAim");
            gunBolt = FindExact(gun.transform, "Bolt");
            gunRearAim = FindExact(gun.transform, "RearAim") ?? gunRearSight;   // ADS 瞳距锚
            // 准星锚:SightAim(与 RearAim 同为 ***Aim 锚点、共线于枪管轴)。注意不能用
            // "Sight" —— 它与 RearAim 不共线,会把 ADS 轴向解歪。
            gunSightAim = FindDeep(gun.transform, "SightAim");
            ComputeAimAxis(gun);
            // 枪相对 WeaponAnchor 的固定局部旋转(驱动锚点旋转时用来换算枪的朝向)
            gunLocalRot = gun.transform.localRotation;

            // 瞄准由 WeaponAnchor 的旋转完成(见 SetWeaponState),**不再**用枪自身
            // 的 AimConstraint:Animation constraint 在 rig 之后求值,会让握把锚点
            // 在手臂 IK 解算后才移动,手永远差一截。停用(保留组件不销毁)。
            gunAim = gun.GetComponent<AimConstraint>();
            if (gunAim != null)
            {
                gunAim.constraintActive = false;
                var srcs = new System.Collections.Generic.List<ConstraintSource>();
                gunAim.GetSources(srcs);
                srcs.Clear();
                gunAim.SetSources(srcs);
            }

            // ---- 瞄准源(PHASE14 腰射驱动)----
            // AimSource 是头部骨骼的子对象,由驱动侧每帧写入相机眼位与朝向;枪械基准
            // 由它 + 持久偏移导出(见 SetWeaponState)。无 AimSource 时才回落跟随胸骨。
            chestBone = animator != null ? animator.GetBoneTransform(HumanBodyBones.Chest) : null;
            weaponFollowInit = aimSource != null;


            boltPhase = 0f;
            lastShotCount = 0;
            adsSmoothing = 0f;
            weaponBaseSetFrame = -1;
        }

        /// <summary>由照门/专用 AimSource 求枪管轴向(枪根局部)与上方向。</summary>
        private void ComputeAimAxis(GameObject gun)
        {
            var aimAxisChild = FindDeep(gun.transform, "AimSource");
            if (aimAxisChild != null)
            {
                Vector3 localDir = gun.transform.InverseTransformPoint(aimAxisChild.position);
                if (localDir.sqrMagnitude > 1e-6f) gunAimLocalAxis = localDir.normalized;
                return;
            }

            if (gunRearSight != null && gunFrontSight != null)
            {
                // 照门 -> 准星 = 枪管指向
                Vector3 dir = gun.transform.InverseTransformPoint(gunFrontSight.position)
                            - gun.transform.InverseTransformPoint(gunRearSight.position);
                if (dir.sqrMagnitude > 1e-8f) gunAimLocalAxis = dir.normalized;
                // 照门在枪管上方 -> “本地哪根轴朝上” = 照门相对枪根方向中 ⊥ 枪管的分量。
                // 直接用该向量(不翻转符号):SceneUp 下 upVector 就是本地朝上轴,
                // 照门在 +Y 侧即得 (0,1,0);拿反的枪模会自动得到正确结果。
                Vector3 up = gunRearSight.localPosition;
                up -= gunAimLocalAxis * Vector3.Dot(up, gunAimLocalAxis);
                if (up.sqrMagnitude > 1e-8f) { gunAimUpVector = up.normalized; gunAimLocalUp = up.normalized; }
            }
        }

        public void ClearWeapon()
        {
            if (!cacheBuilt) return;
            // 只清目标,保留 hint:肘部提示是模型静态引用(烘在预制体上),
            // 之前连带清空会在重绑时被替换成枪局部偏移的临时 hint,导致 IK 解不收敛。
            if (handIKR != null) handIKR.data.target = null;
            if (handIKL != null) handIKL.data.target = null;
            if (gripR != null) gripR.data.grip = null;
            if (gripL != null) gripL.data.grip = null;
            // AimConstraint 可能已烘进枪预制体:停用+清空 sources,不销毁(解绑/重绑周期内保留烘焙组件)
            if (gunAim != null)
            {
                gunAim.constraintActive = false;
                gunAim.rotationOffset = Vector3.zero;
                var srcs = new System.Collections.Generic.List<ConstraintSource>();
                gunAim.GetSources(srcs);
                srcs.Clear();
                gunAim.SetSources(srcs);
            }
            gunAim = null;
            gunBolt = null;
            gunRearSight = null;
            gunFrontSight = null;
            gunSightAim = null;
            boundGun = null;
            weaponAnchorBaseValid = false;
            weaponFollowInit = false;
            pendingRebuild = true;
        }

        public GameObject BoundWeapon => boundGun;

        // ---------------------------------------------------------------
        // WEAPON PRESENTATION (PHASE14 枪械专项)
        // ---------------------------------------------------------------

        /// <summary>
        /// 每帧枪械表现(由 NetworkSoldierAnimator 在 SetWeaponAnchorPosition 之后调用,
        /// 脚本 Update 阶段 —— 早于 RigBuilder 的 LateUpdate,故握把锚点在本帧
        /// 手臂 IK 解算前就已就位):
        ///  - 瞄准:WeaponAnchor 旋转对齐 AimTarget 方向(SceneUp 滚转,枪管指向前方)
        ///  - ADS:基准点沿枪管轴向后移 adsEyeRelief*aimAmount(瞳距可调)
        ///  - 冲刺:基准点后撤 + 绕本地 X 左右摆枪(直接用 SprintAim 朝向)
        ///  - 后座:基准点后撤 + 抬枪/偏摆
        ///  - 枪机:Bolt 局部 z 在 [前向,后向] 界限间往复,最终停在前向界限
        /// </summary>
        public void SetWeaponState(float aimAmount, bool sprinting, float recoil, uint shotCount)
        {
            if (boundGun == null || !cacheBuilt || weaponBasis == null) return;

            aimAmount = Mathf.Clamp01(aimAmount);
            adsSmoothing = Mathf.MoveTowards(adsSmoothing, aimAmount, Time.deltaTime * 6f);

            // 基准位置:本帧若无人显式指定(SetWeaponAnchorPosition),则由 AimSource 导出:
            //   腰射 = AimSource ∘ 偏移(枪停在视野右下角)
            //   ADS  = 向眼位混合(枪抬到准线)
            if (weaponBaseSetFrame != Time.frameCount && weaponFollowInit && aimSource != null)
            {
                Vector3 hipPos = HipHoldWorld();
                weaponAnchorBase = (adsEyeValid && adsSmoothing > 0.001f)
                    ? Vector3.Lerp(hipPos, adsEyePos, adsSmoothing)
                    : hipPos;
                weaponAnchorBaseValid = true;
            }
            // 回落(无 AimSource):跟随胸骨,至少保证蹲/趴时枪随身体移动。
            else if (weaponBaseSetFrame != Time.frameCount && weaponFollowChest && chestBone != null)
            {
                weaponAnchorBase = chestBone.position
                    + transform.right * 0.16f + Vector3.up * 0.02f + transform.forward * 0.34f;
                weaponAnchorBaseValid = true;
            }

            float swayX = 0f;
            if (sprinting)
            {
                sprintSwayPhase += Time.deltaTime * sprintSwayFrequency * Mathf.PI * 2f;
                swayX = Mathf.Sin(sprintSwayPhase) * sprintSwayAmplitude;
            }
            else sprintSwayPhase = 0f;

            // 后座:抬枪(负 X)+ 偏摆(Y);与冲刺摆枪(本地 X)叠加
            Vector3 recoilEuler = new Vector3(-recoil * recoilPitchPerUnit + swayX,
                                              recoil * recoilYawPerUnit, 0f);

            // 瞄准方向:冲刺 = SprintAim 朝向(收枪位);否则 = 相机射线目标点
            Vector3 aimDir = Vector3.zero;
            bool haveDir = false;
            if (sprinting && sprintAimSource != null)
            {
                aimDir = sprintAimSource.forward;
                haveDir = true;
            }
            else if (aimTarget != null)
            {
                Vector3 to = aimTarget.position - weaponBasis.position;
                if (to.sqrMagnitude > 1e-8f) { aimDir = to.normalized; haveDir = true; }
            }

            Quaternion adsAimRot = aimSource != null ? aimSource.rotation : transform.rotation;

            // 完全 ADS:每帧把枪重新求解到相机射线上 —— RearAim 与 SightAim **同时**落在
            // 射线上(PHASE14)。后座只体现在相机自身俯仰(renderPitch 已含后座),故
            // 相机一抬,枪随之重解,照门与准星始终同步,不会出现"准星偏、枪不偏"。
            // (单次取值不行:后座/移动每帧都在变,必须每帧重解。)
            if (adsSmoothing >= 0.999f && adsEyeValid && !sprinting
                && SolveAdsOnRail(adsAimRot, out Vector3 railPos, out Quaternion railRot))
            {
                weaponBasis.position = railPos;
                weaponBasis.rotation = railRot * Quaternion.Inverse(gunLocalRot);
                UpdateBolt(shotCount);
                return;
            }

            // 枪朝向:腰射 = 与瞄准方向平行(偏移已在位置上把枪放到视野右下角);
            // ADS 过渡 = 向射线解 + 后座;冲刺 = SprintAim 收枪位。
            Quaternion gunRot;
            if (sprinting && sprintAimSource != null)
                gunRot = GunLookRotation(sprintAimSource.forward);
            else if (adsSmoothing > 0.5f && adsEyeValid)
                gunRot = AdsSolveRotation(adsAimRot);
            else if (haveDir)
                gunRot = GunLookRotation(aimDir);
            else
                gunRot = HipHoldRotation() * gunLocalRot;
            gunRot = gunRot * Quaternion.Euler(recoilEuler);

            // 基准点:腰射=AimSource∘偏移(视野右下角);ADS=显式求解使瞄准锚点落在射线
            // 起点后方 adsEyeRelief 处(瞳距可调);再叠加冲刺/后座后撤。
            Vector3 basePos = weaponAnchorBase;
            if (adsSmoothing > 0.001f && adsEyeValid)
            {
                Vector3 adsBasisPos = AdsSolvePosition(adsAimRot, gunRot);
                basePos = Vector3.Lerp(HipHoldWorld(), adsBasisPos, adsSmoothing);
            }
            if (weaponAnchorBaseValid)
            {
                Vector3 axisWorld = gunRot * gunAimLocalAxis;   // 枪管世界方向
                Vector3 offset = axisWorld * (-(sprinting ? sprintDropback : 0f)
                                              - (recoil * recoilKickPerUnit));
                weaponBasis.position = basePos + offset;
            }
            // 基准的旋转使其子物体(枪,局部 rot=gunLocalRot)得到 gunRot
            weaponBasis.rotation = gunRot * Quaternion.Inverse(gunLocalRot);

            UpdateBolt(shotCount);
        }

        /// <summary>枪机往复:Bolt 局部 z 在 [前向,后向] 界限间来回,最终停在前向界限。</summary>
        private void UpdateBolt(uint shotCount)
        {
            if (gunBolt == null) return;
            if (shotCount != lastShotCount)
            {
                lastShotCount = shotCount;
                boltPhase = 1f;   // 触发一次往复
            }
            float travel = Mathf.Max(0.001f, boltRearLimit - boltForwardLimit);
            if (boltPhase > 0f)
                boltPhase = Mathf.Max(0f, boltPhase - Time.deltaTime * (boltSpeed / travel));
            // 0 -> 前向界限;1 -> 后向界限(往复包络)
            float env = boltPhase <= 0.5f ? boltPhase * 2f : (1f - boltPhase) * 2f;
            Vector3 p = gunBolt.localPosition;
            p.z = boltForwardLimit + travel * env;
            gunBolt.localPosition = p;
        }

        /// <summary>
        /// 把枪解到相机射线上,使 **RearAim 与 SightAim 同时**落在射线上(PHASE14 ADS):
        ///  1) 局部枪管轴 b = normalize(SightAim - RearAim);求旋转 R 使 R·b = rayDir(相机前向)。
        ///  2) 取 R 让枪的局部“上方”(照门侧)对齐相机 up,消除滚转二义。
        ///  3) 位置 P = (射线起点 + rayDir·adsEyeRelief) - R·RearAim_local(瞳距可调)。
        /// 因 RearAim/SightAim 在局部共线,两锚点会同时落到射线上。
        /// </summary>
        private bool SolveAdsOnRail(Quaternion aimRot, out Vector3 pos, out Quaternion rot)
        {
            pos = Vector3.zero; rot = Quaternion.identity;
            if (gunRearAim == null || gunSightAim == null || boundGun == null) return false;
            Transform gun = boundGun.transform;

            Vector3 rearLocal = gun.InverseTransformPoint(gunRearAim.position);
            Vector3 sightLocal = gun.InverseTransformPoint(gunSightAim.position);
            Vector3 bLocal = sightLocal - rearLocal;
            if (bLocal.sqrMagnitude < 1e-8f) return false;
            bLocal.Normalize();

            Vector3 rayDir = (aimRot * Vector3.forward).normalized;
            Vector3 worldUp = aimRot * Vector3.up;

            Vector3 upLocal = gunAimLocalUp - bLocal * Vector3.Dot(gunAimLocalUp, bLocal);
            if (upLocal.sqrMagnitude < 1e-8f) upLocal = Vector3.up - bLocal * Vector3.Dot(Vector3.up, bLocal);
            if (upLocal.sqrMagnitude < 1e-8f) return false;
            upLocal.Normalize();

            rot = Quaternion.LookRotation(rayDir, worldUp)
                * Quaternion.Inverse(Quaternion.LookRotation(bLocal, upLocal));

            Vector3 anchorOnRay = adsEyePos + rayDir * adsEyeRelief;
            pos = anchorOnRay - rot * rearLocal;
            return true;
        }

        /// <summary>
        /// ADS 朝向:让照门→准星连线(枪管轴)与相机射线重合,即瞄准时枪指向相机前方。
        /// 由瞄准源朝向(相机)直接给出,保证枪管与视线平行。
        /// </summary>
        private Quaternion AdsSolveRotation(Quaternion aimRot)
        {
            return GunLookRotation(aimRot * Vector3.forward);
        }

        /// <summary>
        /// ADS 位置:把枪放到使 **RearAim 锚点** 落在相机射线上、且距离相机 adsEyeRelief
        /// 的位置(PHASE14:枪上方两个子对象位于相机射线上,瞳距可调)。
        /// 解法:先由朝向求出“锚点在射线上的枪位”,再把整枪沿射线后移一个瞳距。
        /// </summary>
        private Vector3 AdsSolvePosition(Quaternion aimRot, Quaternion gunWorldRot)
        {
            if (gunRearAim == null) return adsEyePos;

            Vector3 rayOrigin = adsEyePos;
            Vector3 rayDir = aimRot * Vector3.forward;

            // gunWorldRot(= 枪的世界旋转)下,RearAim 相对枪原点(gun 根)的偏移(世界)。
            // anchorLocal 已在枪局部空间,直接 gunWorldRot * anchorLocal;再乘一次
            // gunLocalRot 会把偏移转两次 → 枪位解错(ADS 时枪偏到身体后面)。
            Vector3 anchorLocal = boundGun.transform.InverseTransformPoint(gunRearAim.position);
            Vector3 anchorOffset = gunWorldRot * anchorLocal;

            // 让 anchor 落在射线上:枪根 = (anchor 所在处) - anchorOffset
            // anchor 取射线上距相机 adsEyeRelief 的点
            Vector3 anchorOnRay = rayOrigin + rayDir * adsEyeRelief;
            return anchorOnRay - anchorOffset;
        }
        /// (local gunAimLocalUp,= 照门一侧)对齐世界上方。握把在枪的 -up 侧,故
        /// 这样得到握把朝下、枪管朝前的正确姿态;拿反的枪模由其 local 轴自动修正。
        /// </summary>
        private Quaternion GunLookRotation(Vector3 worldForward)
        {
            Vector3 f = worldForward.normalized;
            Vector3 up = Vector3.up;
            if (Mathf.Abs(Vector3.Dot(f, up)) > 0.98f) up = Vector3.forward;
            Quaternion worldBasis = Quaternion.LookRotation(f, up);
            Vector3 localFwd = gunAimLocalAxis.sqrMagnitude > 1e-6f ? gunAimLocalAxis.normalized : Vector3.forward;
            Vector3 localUp = gunAimLocalUp.sqrMagnitude > 1e-6f ? gunAimLocalUp.normalized : Vector3.up;
            Quaternion localBasis = Quaternion.LookRotation(localFwd, localUp);
            return worldBasis * Quaternion.Inverse(localBasis);
        }

        private static Transform FindExact(Transform root, string name)
        {
            if (root == null) return null;
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                var r = FindExact(root.GetChild(i), name);
                if (r != null) return r;
            }
            return null;
        }

        private void RebuildRig()
        {
            if (rigBuilder != null && Application.isPlaying && rigBuilder.enabled)
                rigBuilder.Build();
        }
    }
}
