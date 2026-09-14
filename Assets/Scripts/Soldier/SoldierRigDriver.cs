using HagenDa.Animation.Rigging;   // HandGripConstraint（复用件，PHASE14 决策：仅它不重写）
using HagenDa.Networking;          // PlayerPosture
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Animations.Rigging;

namespace HagenDa.Soldier
{
    /// <summary>动画驱动侧每帧喂给 rig 的服务端权威状态（PHASE14：动作全部由服务端权威状态触发）。</summary>
    public struct SoldierFrameState
    {
        public bool dead;
        public PlayerPosture posture;
        public bool sliding;
        public bool airborne;        // 滞空（含跳/飞扑/坠落）
        public bool moving;          // 水平速度高于阈值
        public Vector3 eyePos;       // 瞄准眼位（世界；本地玩家=相机，其余=pitch/yaw 推导）
        public Quaternion eyeRot;    // 瞄准朝向（世界）
        public bool weaponHeld;      // 主武器在持（NetworkGun activeSlot<0 且存活）
        public float aimAmount;      // 0=腰射 1=全 ADS（服务端 aimAmount SyncVar）
        public bool sprinting;
        public float recoil;
        public uint shotCount;
        public Vector3 bodyOffsetExtra;   // 跳跃下蹲包络等额外根位移（世界/实体根空间）
    }

    /// <summary>
    /// PHASE14 士兵 rig 运行时驱动（从0重写，仅复用 HandGripConstraint）。
    /// 挂在角色模型根（Animator 节点，如 Fatui with Collider）下，由
    /// SoldierAnimatorDriver 每帧喂 <see cref="SetFrameState"/>。
    ///
    /// 职责（全部 Transform/约束权重操作，不含网络逻辑）：
    ///  - 下半身差速转身（模型根旋转）：站/蹲静止=上半身开始转向后 1s 追赶、
    ///    移动=持续追赶、空中=立即同步、趴=全身随相机转；脊柱反扭上限 60°
    ///    由 UpperBodyAimConstraint 承担（本驱动只控制其权重）。
    ///  - 脚部贴地 IK：脚骨上方射线查地面，落地帧（该脚最低踝高基线，自适应标定）
    ///    启用、抬脚释放；站/蹲/趴=三维固定，滑铲=仅 Y 固定（脚贴地滑行）；
    ///    移动=软钉上限（保留剪辑抬脚自由度）。触地脚绕身体竖轴按滞后角公转。
    ///  - 姿态：站=下半身 T pose（移动层权重由动画侧控制）；蹲=模型根下降+脚 IK；
    ///    趴=模型根下降+前倾贴地+脚/手 IK 贴地（膝/肘），移动=极缓蹭行（速度在 3C）。
    ///  - 手臂：TwoBoneIK 目标=枪握把锚点 + HandGripConstraint（armIK=false，仅卷指）。
    ///  - 枪械三态（PHASE14 枪械专项）：腰射=枪基准 AimConstraint（源=相机射线 50m 目标点）
    ///    启用+枪自身 SprintAim 约束停用；瞄准=两约束全停，直解使 RearAim 与 SightAim
    ///    同时落在相机射线上（瞳距可调，按 aimAmount 过渡）；冲刺=枪基准停用、
    ///    枪自身 AimConstraint（源=SprintAim，其局部 X 由本驱动左右摆动）启用。
    ///    开火=枪根叠加后座震动 + Bolt 局部 z 在 [0.156, 0.185] 往复、停前向界限。
    ///  - 死亡：rig 全零（纯剪辑/ragdoll 占位）；复活：基线重置。
    /// </summary>
    [DisallowMultipleComponent]
    public class SoldierRigDriver : MonoBehaviour
    {
        // ---------------------------------------------------------------
        // Tuning
        // ---------------------------------------------------------------
        [Header("Foot IK (ground align)")]
        public LayerMask groundMask = ~0;
        [Tooltip("脚 IK 目标的贴地高度偏移（该模型踝骨到脚底的高度，Fatui 实测≈0.21）。")]
        public float footHeight = 0.21f;
        [Tooltip("射线起点在踝骨上方的距离。")]
        public float footRayUp = 0.15f;
        [Tooltip("射线向下长度（须盖过踝高+余量）。")]
        public float footRayDown = 0.45f;
        public float footHintForward = 0.3f;
        public float footHintUp = 0.35f;
        [Tooltip("触地带宽（米，相对该脚观测最低踝高=落地帧）。")]
        public float footPlantBand = 0.02f;
        [Tooltip("释放带宽（米，相对最低踝高）。")]
        public float footLiftBand = 0.09f;
        [Tooltip("静止 IK 权重上限（1=完全钉地）。")]
        [Range(0f, 1f)] public float footIKCapIdle = 1f;
        [Tooltip("移动 IK 权重上限（<1 软钉，保留剪辑抬脚自由度）。")]
        [Range(0f, 1f)] public float footIKCapMoving = 0.15f;
        public float footWeightSpeed = 8f;

        [Header("Differential turn (下半身滞后)")]
        public float turnSmoothTime = 0.18f;
        [Tooltip("滞后超 60° 后的额外追赶增益。")]
        public float turnCatchupBoost = 6f;
        [Tooltip("站/蹲静止：上半身开始转向后延迟多久下半身才追赶（秒）。")]
        public float turnStartDelay = 1.0f;
        [Tooltip("判定“开始转向”的滞后角死区（度）。")]
        public float turnStartDeadband = 4f;
        [Tooltip("脚 IK 锚点滞后朝向时间常数（秒）——触地脚“挪步”的迟滞。")]
        public float footLagSmoothTime = 0.45f;

        [Header("Posture (模型根位移/前倾)")]
        public Vector3 crouchOffset = new Vector3(0f, -0.5f, 0f);
        [Tooltip("趴：根位移（下压+前移）。")]
        public Vector3 proneOffset = new Vector3(0f, -0.9f, 0.25f);
        [Tooltip("趴：模型根前倾角（度，正=前倾脸朝下贴地；负=仰躺）。")]
        public float pronePitch = 75f;   // 正=前倾脸朝下（绕X正角使+Z前向转向-Y）；负值会成仰躺
        [Tooltip("姿态位移/前倾渐变速率（每秒）。")]
        public float postureRampSpeed = 5f;

        [Header("Aim")]
        [Tooltip("瞄准目标点距离（米）——枪基准 AimConstraint 的源。")]
        public float aimTargetDistance = 50f;
        [Tooltip("腰射持枪偏移（AimSource 空间：x=右 y=下 z=前）。未初始化建议右前胸，可在 Inspector 调。")]
        public Vector3 hipHoldOffset = new Vector3(0.05f, -0.24f, 0.20f);
        [Tooltip("ADS 瞳距（照门沿枪管轴到眼睛的距离，米，可调）。")]
        public float adsEyeRelief = 0.13f;
        [Tooltip("aimAmount→枪位/约束的过渡速率（每秒）。")]
        public float aimBlendSpeed = 6f;

        [Header("Sprint (收枪摆动)")]
        public float sprintDropback = 0.06f;
        public float sprintSwayAmplitude = 5f;
        public float sprintSwayFrequency = 2.2f;

        [Header("Recoil (枪根震动)")]
        public float recoilPitchPerUnit = 1.2f;
        public float recoilYawPerUnit = 0.35f;
        public float recoilKickPerUnit = 0.004f;

        [Header("Bolt (枪机往复)")]
        [Tooltip("枪机局部 z 前向界限（最终停驻位）。")]
        public float boltForwardLimit = 0.156f;
        [Tooltip("枪机局部 z 后向界限。")]
        public float boltRearLimit = 0.185f;
        public float boltSpeed = 3.5f;

        // ---------------------------------------------------------------
        // Cached rig pieces
        // ---------------------------------------------------------------
        [SerializeField] private Animator animator;
        [SerializeField] private RigBuilder rigBuilder;
        [SerializeField] private UpperBodyAimConstraint upperAim;
        [SerializeField] private TwoBoneIKConstraint footIKL, footIKR;
        [SerializeField] private TwoBoneIKConstraint handIKR, handIKL;
        [SerializeField] private HandGripConstraint gripR, gripL;
        [SerializeField] private Transform footTipL, footTipR;
        [SerializeField] private Transform footTargetL, footTargetR;
        [SerializeField] private Transform footHintL, footHintR;
        [SerializeField] private Transform driverAimSource;   // 眼位（骨架外，rig 作业可实时读）
        [SerializeField] private Transform driverAimTarget;   // 相机射线 50m 目标点
        [SerializeField] private Transform headBone;          // DEF-spine.005
        [SerializeField] private Transform aimSourceHead;     // 头部骨骼子对象 AimSource（PHASE14 规范节点）
        [SerializeField] private Transform sprintAimSource;   // 人物子对象 SprintAim（冲刺收枪基准）
        [SerializeField] private Transform headAnchor;        // headcollider（相机球）
        private Quaternion sprintAimBaseRot = Quaternion.identity;   // SprintAim 摆动基准（局部）

        private Transform modelRoot;          // = animator.transform（IK 作用骨骼链的根）
        private Transform modelDrop;          // 实体根与模型之间的非动画节点（蹲/趴/yaw/pitch 承载）
        private Vector3 modelBaseLocalPos;
        private Transform entityRoot;         // 模型根的父级（胶囊实体根）
        private Transform weaponBasis;        // 枪械基准（实体根下、与模型同级）
        private GameObject boundGun;
        private AimConstraint basisAim;       // 枪基准 AimConstraint（腰射）
        private AimConstraint gunSprintAim;   // 枪自身 AimConstraint（冲刺，源=SprintAim）
        private Transform gunBolt, gunRearAim, gunSightAim;
        private Transform gunRearSight, gunFrontSight;   // FBX 真瞄具：近眼/近枪口（ADS 贴眼锚）
        private Quaternion gunAlignRot = Quaternion.identity;   // 枪相对基准的对齐旋转（使基准 +Z=枪管轴）
        private Vector3 gunAimLocalAxis = Vector3.forward;
        private Vector3 gunAimLocalUp = Vector3.up;

        // ---- turn / foot / posture state ----
        private bool cachesValid;
        private float dampedYaw;
        private float idleTurnTimer;   // 静止转向：上半身开始转向后脚锚追赶的延迟计时
        private bool footLagInit;
        private float footLagYaw;
        private float footLagVel;
        private float footWeightL = 1f, footWeightR = 1f;
        private float footPlantL, footPlantR;
        private bool footFrozenL, footFrozenR;
        private Vector3 footFrozenPosL, footFrozenPosR;
        private float footFrozenYawL, footFrozenYawR;
        private float minAnkleL = float.MaxValue, minAnkleR = float.MaxValue;
        private float crouchCur, proneCur;      // 姿态 0..1 渐变
        private Vector3 lastBodyOffsetExtra;    // 最近一帧额外根位移（跳跃 dip 等，LateUpdate 复写用）
        private float adsSmooth;                 // aimAmount 平滑
        private float sprintSwayPhase;
        private float boltPhase;
        private uint lastShotCount;
        private bool deadApplied;
        private float handsWCur = 1f;   // 手臂/指节 IK 混合权重（LateUpdate 程序化用）
        private bool pendingRebuild;    // Rebind/绑枪后等 animator 初始化再重建 rig 图

        [Header("IK 路径")]
        [Tooltip("true=LateUpdate 程序化两骨 IK（应急后备）；false=Animation Rigging 作业（规范路径）。")]
        [SerializeField] private bool useProceduralFallback = false;

        private static readonly int MoveX = Animator.StringToHash("MoveX");
        private static readonly int MoveZ = Animator.StringToHash("MoveZ");

        public Animator ModelAnimator => animator;
        public Transform ModelRoot => modelRoot;
        public Transform WeaponBasis => weaponBasis;
        public GameObject BoundGun => boundGun;
        public Transform HeadAnchor => headAnchor;

        private const float SpineMaxTwist = 60f;   // 与 UpperBodyAimConstraint.maxTwist 一致（追赶判定用）

        /// <summary>运行时尽早自初始化（幂等）：缓存 Animator/约束/锚点，供驱动侧首帧读取。</summary>
        private void Awake() => EnsureCache();
        private void OnEnable() => EnsureCache();

        // ---------------------------------------------------------------
        // CACHE
        // ---------------------------------------------------------------

        /// <summary>按名字/类型查找并缓存 rig 组件与锚点（防御式，缺失仅告警）。幂等。</summary>
        public bool EnsureCache()
        {
            if (cachesValid) return true;

            if (animator == null) animator = GetComponent<Animator>();
            if (animator == null) animator = GetComponentInChildren<Animator>(true);
            if (animator == null)
            {
                Debug.LogWarning($"[SoldierRigDriver] {name}: no Animator", this);
                return false;
            }
            modelRoot = animator.transform;
            entityRoot = modelRoot.parent != null ? modelRoot.parent : modelRoot;
            // ModelDrop 中间节点（实体根 → ModelDrop → 模型）：承载蹲/趴位移与
            // 身体 yaw/pitch。非动画节点 → Update 写入在动画相位保持，rig 作业
            // 解算时看到最终骨架；直接写模型根会被 Humanoid 动画每帧覆盖（实测）。
            if (modelDrop == null)
            {
                var dropT = entityRoot.Find("ModelDrop");
                if (dropT == null)
                {
                    var go = new GameObject("ModelDrop");
                    go.transform.SetParent(entityRoot, false);
                    dropT = go.transform;
                }
                modelDrop = dropT;
                if (modelRoot.parent != modelDrop) modelRoot.SetParent(modelDrop, false);
            }
            modelBaseLocalPos = modelDrop.localPosition;
            if (rigBuilder == null) rigBuilder = modelRoot.GetComponent<RigBuilder>();
            // Animation Rigging 作业实测有效（最小实验：烘焙约束/全新 Rig 均精确追踪
            // 移动目标，avg=0.000）。唯一失效场景：animator.Rebind()（死亡→复活）
            // 摧毁图内作业且不自动恢复 —— pendingRebuild（复活后等 animator 初始化
            // 再 Build）兜底。程序化 IK 仅作 useProceduralFallback 后备开关。
            if (rigBuilder != null && !rigBuilder.enabled) rigBuilder.enabled = true;

            var all = modelRoot.GetComponentsInChildren<MonoBehaviour>(true);
            foreach (var c in all)
            {
                switch (c)
                {
                    case UpperBodyAimConstraint u: upperAim = u; break;
                    case TwoBoneIKConstraint ik: CacheTwoBoneIK(ik); break;
                    case HandGripConstraint g:
                        if (g.data.side == HandGripSide.Left) gripL = g;
                        else gripR = g;
                        break;
                }
            }

            var drivers = modelRoot.Find("Rig_Drivers");
            if (drivers == null)
            {
                Debug.LogWarning($"[SoldierRigDriver] {name}: Rig_Drivers missing — 先运行 Bake Fatui Rig", this);
                return false;
            }
            driverAimSource = Require(drivers, "Driver_AimSource");
            driverAimTarget = Require(drivers, "Driver_AimTarget");
            footTargetL = Require(drivers, "Driver_FootTarget_L");
            footTargetR = Require(drivers, "Driver_FootTarget_R");
            footHintL = Require(drivers, "Driver_FootHint_L");
            footHintR = Require(drivers, "Driver_FootHint_R");

            headBone = FindDeep(modelRoot, "DEF-spine.005");
            if (headBone == null && animator.isHuman)
                headBone = animator.GetBoneTransform(HumanBodyBones.Head);
            if (headBone == null) Debug.LogWarning($"[SoldierRigDriver] {name}: head bone DEF-spine.005 not found", this);
            else if (aimSourceHead == null) aimSourceHead = headBone.Find("AimSource");

            // 冲刺收枪基准（人物子对象；预制体已烘，缺失则在右前下方补建）。
            if (sprintAimSource == null) sprintAimSource = FindDeep(modelRoot, "SprintAim");
            if (sprintAimSource == null)
            {
                var go = new GameObject("SprintAim");
                go.transform.SetParent(modelRoot, false);
                go.transform.localPosition = new Vector3(0.22f, 1.05f, 0.30f);
                go.transform.localRotation = Quaternion.Euler(28f, 0f, 0f);
                sprintAimSource = go.transform;
            }
            sprintAimBaseRot = sprintAimSource.localRotation;

            // headcollider：优先 Head 命中箱，否则按名查找。
            if (headAnchor == null)
            {
                foreach (var t in modelRoot.GetComponentsInChildren<Transform>(true))
                    if (t.name.ToLowerInvariant().Contains("headcollider")) { headAnchor = t; break; }
            }

            cachesValid = true;
            return true;
        }

        private void CacheTwoBoneIK(TwoBoneIKConstraint ik)
        {
            string tip = ik.data.tip != null ? ik.data.tip.name : string.Empty;
            bool isFoot = tip.Contains("foot");
            bool isLeft = tip.Contains(".L");
            if (isFoot)
            {
                if (isLeft) { footIKL = ik; footTipL = ik.data.tip; }
                else { footIKR = ik; footTipR = ik.data.tip; }

                // 脚 IK 只钉位置不强制旋转：鞋底朝向由 locomotion 剪辑保持
                //（LookRotation 会把非标准前向轴的脚骨翻成鞋底朝上，旧分支实测）。
                var d = ik.data;
                d.targetRotationWeight = 0f;
                ik.data = d;
            }
            else
            {
                if (isLeft) handIKL = ik;
                else handIKR = ik;
            }
        }

        private static Transform Require(Transform parent, string name)
        {
            var t = parent.Find(name);
            if (t == null) Debug.LogWarning($"[SoldierRigDriver] missing driver '{name}' under {parent.name}");
            return t;
        }

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

        // ---------------------------------------------------------------
        // WEAPON BINDING
        // ---------------------------------------------------------------

        /// <summary>
        /// 绑定枪模（枪械基准 WeaponBasis 的子物体）。装配约定（PHASE14 角色结构）：
        /// 胶囊实体根 -> [角色模型 + 枪械基准(同级)] -> 枪模。本方法：
        ///  1. 双手 TwoBoneIK 目标 = RearGrip(右)/BarrelGrip(左)，HandGrip 握把胶囊换绑；
        ///  2. 枪相对基准的对齐旋转（基准本地 +Z = 枪管轴），使基准 AimConstraint 生效；
        ///  3. 确保枪基准 AimConstraint（源=Driver_AimTarget）与枪自身 SprintAim
        ///     AimConstraint（源=人物子对象 SprintAim）存在（默认停用，按三态切换）。
        /// </summary>
        public bool BindWeapon(GameObject gun)
        {
            if (!EnsureCache() || gun == null) return false;

            boundGun = gun;
            if (gun.transform.parent != null && gun.transform.parent != modelRoot)
                weaponBasis = gun.transform.parent;
            if (weaponBasis == null) return false;

            Transform rightAnchor = FindDeep(gun.transform, "RearGrip");
            Transform leftAnchor = FindDeep(gun.transform, "BarrelGrip");
            CapsuleCollider capR = FindDeep(gun.transform, "RearGripCap")?.GetComponent<CapsuleCollider>();
            CapsuleCollider capL = FindDeep(gun.transform, "BarrelGripCap")?.GetComponent<CapsuleCollider>();
            if (rightAnchor == null || leftAnchor == null)
            {
                Debug.LogWarning($"[SoldierRigDriver] {gun.name}: RearGrip/BarrelGrip anchors missing", this);
                return false;
            }

            if (handIKR != null) { var d = handIKR.data; d.target = rightAnchor; handIKR.data = d; }
            if (handIKL != null) { var d = handIKL.data; d.target = leftAnchor; handIKL.data = d; }
            if (gripR != null) { var d = gripR.data; d.grip = capR; d.armIK = false; gripR.data = d; }
            if (gripL != null) { var d = gripL.data; d.grip = capL; d.armIK = false; gripL.data = d; }

            InitWeaponPresentation(gun);
            pendingRebuild = true;   // 延迟到 animator 初始化后重建（过早 Build 会绑错句柄）
            return true;
        }

        public void ClearWeapon()
        {
            if (handIKR != null) { var d = handIKR.data; d.target = null; handIKR.data = d; }
            if (handIKL != null) { var d = handIKL.data; d.target = null; handIKL.data = d; }
            if (gripR != null) { var d = gripR.data; d.grip = null; gripR.data = d; }
            if (gripL != null) { var d = gripL.data; d.grip = null; gripL.data = d; }
            boundGun = null;
            gunBolt = null;
            gunRearAim = null;
            gunSightAim = null;
            if (basisAim != null) basisAim.constraintActive = false;
            if (gunSprintAim != null) gunSprintAim.constraintActive = false;
            weaponBasis = null;
            basisAim = null;
            gunSprintAim = null;
        }

        private void InitWeaponPresentation(GameObject gun)
        {
            gunBolt = FindDeep(gun.transform, "Bolt");
            gunRearAim = FindDeep(gun.transform, "RearAim");
            gunSightAim = FindDeep(gun.transform, "SightAim");
            gunRearSight = FindDeep(gun.transform, "Rear_Sight");   // 近眼瞄具（ADS 贴眼锚）
            gunFrontSight = FindDeep(gun.transform, "Sight");       // 近枪口瞄具

            // 枪管轴（枪根局部，= 枪口方向）——单一事实源。
            // 首选 FBX 真瞄具：Sight(近枪口) - Rear_Sight(近眼) = 枪口方向（语义无歧义）。
            // 缺失时回落锚点经验方向：本枪模 RearAim(z=+0.112) 在枪口端、SightAim(z=-0.198)
            // 在枪托/贴眼侧 —— 名义命名与实际相反，故轴 = RearAim - SightAim。
            // 再缺失：枪根本地 +Z。
            Transform rearSight = FindDeep(gun.transform, "Rear_Sight");
            Transform frontSight = FindDeep(gun.transform, "Sight");
            Vector3 axis;
            if (rearSight != null && frontSight != null)
            {
                axis = gun.transform.InverseTransformPoint(frontSight.position)
                     - gun.transform.InverseTransformPoint(rearSight.position);
            }
            else
            {
                Vector3 rear = gunRearAim != null
                    ? gun.transform.InverseTransformPoint(gunRearAim.position)
                    : Vector3.zero;
                Vector3 sight = gunSightAim != null
                    ? gun.transform.InverseTransformPoint(gunSightAim.position)
                    : Vector3.forward;
                axis = rear - sight;
            }
            if (axis.sqrMagnitude > 1e-8f)
            {
                gunAimLocalAxis = axis.normalized;
                // 上方向 = 近眼瞄具相对枪管轴的垂直分量（瞄具在枪管上方 → +Y 侧）。
                Vector3 upRef = rearSight != null
                    ? gun.transform.InverseTransformPoint(rearSight.position)
                    : (gunRearAim != null ? gun.transform.InverseTransformPoint(gunRearAim.position) : Vector3.up);
                Vector3 up = upRef - gunAimLocalAxis * Vector3.Dot(upRef, gunAimLocalAxis);
                if (up.sqrMagnitude > 1e-8f) gunAimLocalUp = up.normalized;
            }
            // 对齐旋转：把枪口轴映射到枪根本地 +Z → 基准本地 +Z 恒=枪口方向，
            // 基准 AimConstraint(默认 aimVector=+Z) 与 ADS 直解共用此约定。
            gunAlignRot = Quaternion.Inverse(Quaternion.LookRotation(gunAimLocalAxis, gunAimLocalUp));
            gun.transform.localRotation = gunAlignRot;
            gunRearSight = rearSight;    // 近眼瞄具（ADS 贴眼锚）
            gunFrontSight = frontSight;  // 近枪口瞄具

            // 枪基准 AimConstraint：源=Driver_AimTarget（相机射线 50m 目标点）。
            // 默认 aimVector=本地 +Z、worldUpType=Vector/(0,1,0)，配合枪对齐旋转即所需。
            if (basisAim == null) basisAim = weaponBasis.GetComponent<AimConstraint>();
            if (basisAim == null) basisAim = weaponBasis.gameObject.AddComponent<AimConstraint>();
            basisAim.constraintActive = false;
            basisAim.SetSources(new System.Collections.Generic.List<ConstraintSource>
            {
                new ConstraintSource { sourceTransform = driverAimTarget, weight = 1f }
            });

            // 枪自身 AimConstraint：源=人物子对象 SprintAim（冲刺三态）。
            // aimVector=枪口轴（本枪模枪口为局部 -Z，SightAim-RearAim 实测）：
            // 约束把**枪口**对向 SprintAim；默认 +Z 会把枪托对向源 → 枪口反向
            // 指着角色（实测踩坑）。
            if (gunSprintAim == null) gunSprintAim = gun.GetComponent<AimConstraint>();
            if (gunSprintAim == null) gunSprintAim = gun.gameObject.AddComponent<AimConstraint>();
            gunSprintAim.aimVector = gunAimLocalAxis;
            gunSprintAim.constraintActive = false;
            gunSprintAim.SetSources(new System.Collections.Generic.List<ConstraintSource>
            {
                new ConstraintSource { sourceTransform = sprintAimSource, weight = 1f }
            });

            boltPhase = 0f;
            lastShotCount = 0;
            adsSmooth = 0f;
        }

        // ---------------------------------------------------------------
        // PER-FRAME ENTRY
        // ---------------------------------------------------------------

        /// <summary>延迟重建：Rebind/绑枪后等 animator 初始化再重建 rig 图（过早重建会绑错句柄）。</summary>
        private void Update()
        {
            if (pendingRebuild && animator != null && animator.isInitialized && rigBuilder != null)
            {
                pendingRebuild = false;
                rigBuilder.Build();
            }
        }

        /// <summary>每帧由 SoldierAnimatorDriver 在 Update 调用（早于动画求值与 RigBuilder）。</summary>
        public void SetFrameState(in SoldierFrameState s)
        {
            if (!EnsureCache()) return;
            float dt = Time.deltaTime;

            // ---- 转向（PHASE14 修正：**身体立即跟随瞄准 yaw**，滞后只发生在脚 IK 锚点
            //      —— 上/下半身同步转向，触地脚经"冻结世界位置+滞后公转"实现钉地与挪步）----
            float aimYaw = s.eyeRot.eulerAngles.y;
            bool proneNow = s.dead || s.posture == PlayerPosture.Prone;
            dampedYaw = aimYaw;                       // 模型根（全身）立即对齐，无差速

            if (proneNow || s.airborne)
            {
                footLagYaw = aimYaw;                  // 趴/空中：脚锚立即同步
                footLagVel = 0f;
                footLagInit = true;
                idleTurnTimer = 0f;
            }
            else
            {
                float footLagBefore = Mathf.Abs(Mathf.DeltaAngle(footLagYaw, aimYaw));
                if (!footLagInit) { footLagYaw = aimYaw; footLagInit = true; footLagVel = 0f; }
                else if (s.moving)
                {
                    // 移动=持续追赶（脚锚公转"挪步"）。
                    idleTurnTimer = 0f;
                    footLagYaw = Mathf.SmoothDampAngle(footLagYaw, aimYaw, ref footLagVel,
                        Mathf.Max(0.01f, footLagSmoothTime), Mathf.Infinity, dt);
                }
                else if (footLagBefore <= turnStartDeadband)
                {
                    // 静止且未转向：脚锚保持，计时清零。
                    idleTurnTimer = 0f;
                    footLagVel = 0f;
                }
                else
                {
                    // 静止转向：上半身/身体已随瞄准转，脚锚**钉住 1s** 后才开始
                    // 滞后追赶（PHASE14 转身专项：上半身开始转向后 1s，追赶转向）。
                    idleTurnTimer += dt;
                    if (idleTurnTimer >= turnStartDelay)
                        footLagYaw = Mathf.SmoothDampAngle(footLagYaw, aimYaw, ref footLagVel,
                            Mathf.Max(0.01f, footLagSmoothTime), Mathf.Infinity, dt);
                    else
                        footLagVel = 0f;
                }
            }

            // ---- 姿态渐变（蹲/趴）+ ModelDrop 位姿 ----
            // 蹲/趴/跳位移与身体 yaw/pitch 作用在 **ModelDrop 节点**（实体根与模型之间的
            // 非动画节点）：Update 写入后在动画相位保持不被覆盖 → Animation Rigging
            // 作业解算时看到的就是下沉/旋转后的骨架（蹲=脚贴地屈膝，手够得到枪）。
            // 直接写模型根会被 Humanoid 动画每帧重写为剪辑根位姿（实测），且 LateUpdate
            // 再压会导致 rig 解算结果被整体拖低一个蹲降量（实测 0.5m，"手到胯部"根因）。
            float crouchTarget = (!s.dead && (s.posture == PlayerPosture.Crouch || s.sliding)) ? 1f : 0f;
            float proneTarget = proneNow ? 1f : 0f;
            crouchCur = Mathf.MoveTowards(crouchCur, crouchTarget, postureRampSpeed * dt);
            proneCur = Mathf.MoveTowards(proneCur, proneTarget, postureRampSpeed * dt);

            float parentYaw = entityRoot != null ? entityRoot.eulerAngles.y : 0f;
            float pitch = proneCur > 0.001f ? pronePitch * proneCur : 0f;
            if (modelDrop != null)
            {
                modelDrop.localRotation = Quaternion.Euler(pitch, dampedYaw - parentYaw, 0f);
                modelDrop.localPosition = modelBaseLocalPos
                    + crouchOffset * crouchCur
                    + proneOffset * proneCur
                    + s.bodyOffsetExtra;
            }
            lastBodyOffsetExtra = s.bodyOffsetExtra;

            // ---- 瞄准源/目标点（模型根写定之后再写世界位姿：枪基准 AimConstraint
            //      与 UpperAim 都读它们；本帧内根变换不再变动，位姿精确）----
            if (driverAimSource != null)
            {
                driverAimSource.position = s.eyePos;
                driverAimSource.rotation = s.eyeRot;
            }
            if (driverAimTarget != null)
                driverAimTarget.position = s.eyePos + s.eyeRot * Vector3.forward * aimTargetDistance;
            // 规范节点：头部骨骼子对象 AimSource 同步眼位（原生约束阶段可实时读到）。
            if (aimSourceHead != null)
            {
                aimSourceHead.position = s.eyePos;
                aimSourceHead.rotation = s.eyeRot;
            }

            // ---- 约束权重 ----
            if (upperAim != null)
            {
                // 上半身反扭：持枪且存活时启用；趴下（全身随相机）让位归零。
                float spineTarget = (s.weaponHeld && !s.dead) ? 1f - proneCur : 0f;
                upperAim.weight = Mathf.MoveTowards(upperAim.weight, spineTarget, dt * 5f);
            }
            float handsTarget = (s.weaponHeld && !s.dead) ? 1f : 0f;
            handsWCur = Mathf.MoveTowards(handsWCur, handsTarget, dt * 7f);
            if (handIKR != null) handIKR.weight = handsWCur;
            if (handIKL != null) handIKL.weight = handsWCur;
            if (gripR != null) gripR.weight = handsWCur;
            if (gripL != null) gripL.weight = handsWCur;

            // ---- 脚部贴地 IK ----
            float cap = s.moving ? footIKCapMoving : footIKCapIdle;
            // 滑铲仍要脚贴地（仅 Y 固定）；滞空/死亡完全释放。
            float airGate = (s.airborne || s.dead) ? 0f : 1f;
            bool yOnly = s.sliding;   // 滑铲：仅 Y 固定，脚贴地滑行
            // 静止（站/蹲/趴）强制贴地：T-pose 等无落地帧的状态下踝高基线门控
            // 永不触发，会导致脚悬空——静止时脚必须钉在地面。
            bool forcePlant = !s.moving && !s.airborne && !s.dead;
            footPlantL = StepFoot(footTipL, footTargetL, footHintL, cap * airGate,
                                  yOnly, forcePlant, dt, ref footFrozenL, ref footFrozenPosL, ref footFrozenYawL,
                                  ref minAnkleL, ref footWeightL, footIKL);
            footPlantR = StepFoot(footTipR, footTargetR, footHintR, cap * airGate,
                                  yOnly, forcePlant, dt, ref footFrozenR, ref footFrozenPosR, ref footFrozenYawR,
                                  ref minAnkleR, ref footWeightR, footIKR);

            // ---- 枪械三态表现 ----
            UpdateWeaponPose(s, dt);
        }

        /// <summary>
        /// 单脚贴地 IK：射线查地面 → 目标=落点+footHeight；门控=该脚最低踝高基线
        /// （落地帧）自适应：低于 最低+plantBand=启用、高于 最低+liftBand=释放。
        /// 触地且权重高时冻结世界位置（站/蹲/趴三维固定；滑铲仅 Y 固定），
        /// 冻结点绕身体竖轴按滞后角公转（下半身转身时脚滞后“挪步”）。
        /// 返回触地程度 0..1（供调试/后续使用）。
        /// </summary>
        private float StepFoot(Transform tip, Transform target, Transform hint, float capWeight,
            bool yOnly, bool forcePlant, float dt, ref bool frozen, ref Vector3 frozenPos, ref float frozenYaw,
            ref float minAnkle, ref float weightCur, TwoBoneIKConstraint ik)
        {
            if (ik != null)
            {
                float w = Mathf.MoveTowards(weightCur, capWeight, footWeightSpeed * dt);
                weightCur = w;
                ik.weight = w;
            }
            if (tip == null || target == null) return 0f;

            // ---- 门控（触地程度）：最低踝高基线，自适应标定 + 极慢回弹适应坡面。
            //      静止态强制触地（forcePlant，覆盖基线门控）。----
            float rel = tip.position.y - (entityRoot != null ? entityRoot.position.y : transform.position.y);
            if (rel < minAnkle) minAnkle = rel;
            else minAnkle = Mathf.MoveTowards(minAnkle, rel, dt * 0.05f);
            float t = (rel - (minAnkle + footPlantBand)) / Mathf.Max(0.01f, footLiftBand - footPlantBand);
            float plant = forcePlant ? 1f : Mathf.Clamp01(1f - t);

            // ---- 目标求解 ----
            Vector3 ankle = tip.position;
            Vector3 origin = ankle + Vector3.up * footRayUp;
            if (!Physics.Raycast(origin, Vector3.down, out var hit,
                                 footRayUp + footRayDown, groundMask, QueryTriggerInteraction.Ignore))
            {
                // 踝已沉入地下时探不到 → 从实体根上方重探（自恢复）。
                Vector3 origin2 = (entityRoot != null ? entityRoot.position : transform.position)
                                  + Vector3.up * 0.6f;
                Physics.Raycast(origin2, Vector3.down, out hit, 1.5f, groundMask, QueryTriggerInteraction.Ignore);
            }
            if (hit.collider != null)
            {
                Vector3 ground = hit.point + Vector3.up * footHeight;
                bool wantFreeze = plant > 0.5f && capWeight > 0.05f;
                if (wantFreeze)
                {
                    if (!frozen) { frozenPos = ground; frozenYaw = footLagYaw; frozen = true; }
                    // 触地脚：绕身体竖轴按滞后变化量公转（身体转、脚滞后跟上）。
                    Quaternion orbit = Quaternion.Euler(0f, footLagYaw - frozenYaw, 0f);
                    Vector3 pivot = entityRoot != null ? entityRoot.position : transform.position;
                    Vector3 planted = pivot + orbit * (frozenPos - pivot);
                    target.position = yOnly
                        ? new Vector3(ankle.x, ground.y, ankle.z)     // 滑铲：仅 Y 固定，贴地滑行
                        : new Vector3(planted.x, ground.y, planted.z);
                }
                else
                {
                    frozen = false;
                    target.position = yOnly ? new Vector3(ankle.x, ground.y, ankle.z) : ground;
                }
                Vector3 fwd = entityRoot != null ? entityRoot.forward : transform.forward;
                fwd.y = 0f;
                if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.forward;
                target.rotation = Quaternion.LookRotation(fwd.normalized, hit.normal);
            }
            else
            {
                frozen = false;
                target.position = ankle;
            }

            if (hint != null)
            {
                Vector3 fwd = entityRoot != null ? entityRoot.forward : transform.forward;
                fwd.y = 0f;
                if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.forward;
                float up = proneCur > 0.5f ? 0.05f : footHintUp;   // 趴：膝/肘 hint 压低贴地
                hint.position = target.position + fwd.normalized * footHintForward + Vector3.up * up;
            }
            return plant;
        }

        /// <summary>
        /// 动画阶段后的根位姿最终裁定。实测：Humanoid 动画即使 applyRootMotion=false，
        /// 也会每帧把模型根 localPosition 重写为剪辑根位姿（Kevin Iglesias 就地剪辑
        /// Root Position 未 Bake Into Pose）——Update 里写的蹲/趴位移会被吞掉。
        /// LateUpdate（动画之后）重新写入：蹲/趴位移、转向 yaw、前倾、跳跃 dip。
        /// </summary>
        private void LateUpdate()
        {
            // 根位姿已由 ModelDrop 在 Update 承载（非动画节点，动画相位保持不被覆盖）。
            // 此处不得再重写模型根 —— 否则会对 rig 作业解算结果二次施加蹲/趴位移
            //（实测"蹲下手到胯部/脚卡地里"的根因）。
            ApplyProceduralRig();
        }

        // ---------------------------------------------------------------
        // WEAPON POSE (PHASE14 枪械专项：腰射 / 瞄准 / 冲刺 三态)
        // ---------------------------------------------------------------

        /// <summary>
        /// 三态切换（约束启停 + 基准位姿）：
        ///  - 腰射：枪基准 AimConstraint 启用（源=相机射线 50m 点），基准位置=
        ///    AimSource∘持枪偏移（右前胸）；枪自身 SprintAim 约束停用。
        ///  - 瞄准：两约束全停；直解基准位姿使 RearAim 与 SightAim 同时落在相机
        ///    射线上（瞳距沿枪管轴后移 adsEyeRelief，可调）；按 aimAmount 从腰射过渡。
        ///  - 冲刺：枪基准停用；枪自身 AimConstraint（源=SprintAim）启用；
        ///    摆枪（局部 X）由 SprintAim 节点自身振荡实现（约束跟随，无写冲突）。
        /// 后座作用在枪根（不动基准 → 不影响手臂 IK 目标以外的任何动作）。
        /// </summary>
        private void UpdateWeaponPose(in SoldierFrameState s, float dt)
        {
            if (weaponBasis == null || boundGun == null) return;

            bool alive = !s.dead && s.weaponHeld;
            adsSmooth = Mathf.MoveTowards(adsSmooth, alive ? Mathf.Clamp01(s.aimAmount) : 0f, aimBlendSpeed * dt);

            // ---- 冲刺摆枪：SprintAim 节点绕局部 X 振荡（枪自身 AimConstraint 跟随）----
            if (alive && s.sprinting && gunSprintAim != null)
            {
                sprintSwayPhase += dt * sprintSwayFrequency * Mathf.PI * 2f;
                float swayX = Mathf.Sin(sprintSwayPhase) * sprintSwayAmplitude;
                sprintAimSource.localRotation = sprintAimBaseRot * Quaternion.AngleAxis(swayX, Vector3.right);
            }
            else
            {
                sprintSwayPhase = 0f;
                sprintAimSource.localRotation = sprintAimBaseRot;
            }

            // ---- 约束启停（三态互斥）----
            // 腰射/冲刺：旋转交由启用的原生 AimConstraint（约束求值阶段晚于本写入，
            // 早于 RigBuilder 手臂 IK —— 手读到的恒是最终枪位姿）。
            // 全 ADS：两约束停用，旋转由脚本直解（rail）接管。
            bool adsMode = alive && adsSmooth >= 0.999f;
            if (basisAim != null) basisAim.constraintActive = alive && !s.sprinting && !adsMode;
            if (gunSprintAim != null) gunSprintAim.constraintActive = alive && s.sprinting && !adsMode;

            // ---- 基准位置（腰射=右前胸持枪位；冲刺=后撤；ADS=向射线解过渡）----
            if (driverAimSource != null)
            {
                Transform eye = driverAimSource;
                Vector3 hipPos = eye.position
                    + eye.right * hipHoldOffset.x
                    + eye.up * hipHoldOffset.y
                    + eye.forward * hipHoldOffset.z;
                Vector3 basisPos = s.sprinting ? hipPos - eye.forward * sprintDropback : hipPos;

                if (adsMode && SolveAdsRail(s, out Vector3 railPos, out Quaternion railRot))
                {
                    weaponBasis.position = railPos;
                    weaponBasis.rotation = railRot;
                }
                else if (adsSmooth > 0.001f && SolveAdsRail(s, out Vector3 blendPos, out Quaternion blendRot))
                {
                    // 瞄准过渡区：位置向射线解收敛；旋转与 rail 一致（约束停用区间内脚本接管，
                    // 到 1.0 后无缝衔接全 ADS）。
                    weaponBasis.position = Vector3.Lerp(basisPos, blendPos, adsSmooth);
                    weaponBasis.rotation = blendRot;
                    if (basisAim != null) basisAim.constraintActive = false;
                    if (gunSprintAim != null) gunSprintAim.constraintActive = false;
                }
                else
                {
                    weaponBasis.position = basisPos;
                }
            }

            // ---- 后座（枪根震动，不动基准）----
            if (s.recoil > 0.0001f)
            {
                Quaternion recoilRot = Quaternion.Euler(
                    -s.recoil * recoilPitchPerUnit, s.recoil * recoilYawPerUnit, 0f);
                boundGun.transform.localRotation = gunAlignRot * recoilRot;
                Vector3 axisWorld = weaponBasis.rotation * gunAimLocalAxis;
                boundGun.transform.localPosition =
                    boundGun.transform.parent == weaponBasis
                        ? Vector3.zero - axisWorld * (s.recoil * recoilKickPerUnit)
                        : boundGun.transform.localPosition;
            }
            else if (boundGun.transform.parent == weaponBasis)
            {
                boundGun.transform.localRotation = gunAlignRot;
                boundGun.transform.localPosition = Vector3.zero;
            }

            UpdateBolt(s.shotCount, dt);
        }

        /// <summary>
        /// ADS 直解（统一约定：基准本地 +Z ≡ 枪口，gunAlignRot 已把枪口轴对到 +Z）。
        ///  1) 旋转 R = LookRotation(射线方向, 相机 up) —— 枪口(=基准+Z)即对齐射线，
        ///     瞄具"上"经 gunAlignRot 映射为基准 +Y = 相机 up，滚转消除。
        ///  2) 位置 P = (眼位 + 射线方向·瞳距) - R·(gunAlignRot·Rear_Sight_local)。
        ///     **贴眼锚 = Rear_Sight（近眼真瞄具）**落射线、距眼=瞳距（可调）；
        ///     两瞄具共线于枪管 → Rear_Sight 与 Sight 同时上射线（PHASE14）。
        /// </summary>
        private bool SolveAdsRail(in SoldierFrameState s, out Vector3 pos, out Quaternion rot)
        {
            pos = Vector3.zero;
            rot = Quaternion.identity;
            if (gunRearSight == null || boundGun == null) return false;

            Vector3 rayDir = (s.eyeRot * Vector3.forward).normalized;
            Vector3 worldUp = s.eyeRot * Vector3.up;

            rot = Quaternion.LookRotation(rayDir, worldUp);

            // 近眼瞄具在枪根局部的偏移（FBX 内可能嵌套，用 InverseTransformPoint）
            Vector3 anchorLocal = boundGun.transform.InverseTransformPoint(gunRearSight.position);
            Vector3 anchorInBasis = gunAlignRot * anchorLocal;                // 锚在基准空间的偏移
            Vector3 anchorOnRay = s.eyePos + rayDir * adsEyeRelief;
            pos = anchorOnRay - rot * anchorInBasis;
            return true;
        }

        /// <summary>枪机往复：局部 z 在 [前向, 后向] 界限间来回，最终停在前向界限。</summary>
        private void UpdateBolt(uint shotCount, float dt)
        {
            if (gunBolt == null) return;
            if (shotCount != lastShotCount)
            {
                lastShotCount = shotCount;
                boltPhase = 1f;
            }
            float travel = Mathf.Max(0.001f, boltRearLimit - boltForwardLimit);
            if (boltPhase > 0f)
                boltPhase = Mathf.Max(0f, boltPhase - dt * (boltSpeed / travel));
            float env = boltPhase <= 0.5f ? boltPhase * 2f : (1f - boltPhase) * 2f;
            Vector3 p = gunBolt.localPosition;
            p.z = boltForwardLimit + travel * env;
            gunBolt.localPosition = p;
        }

        // ---------------------------------------------------------------
        // DEATH / REVIVE
        // ---------------------------------------------------------------

        /// <summary>死亡：rig 全零（纯剪辑/ragdoll 占位），约束停用。</summary>
        public void ApplyDeath()
        {
            if (deadApplied) return;
            deadApplied = true;
            if (upperAim != null) upperAim.weight = 0f;
            if (footIKL != null) footIKL.weight = 0f;
            if (footIKR != null) footIKR.weight = 0f;
            if (handIKR != null) handIKR.weight = 0f;
            if (handIKL != null) handIKL.weight = 0f;
            if (gripR != null) gripR.weight = 0f;
            if (gripL != null) gripL.weight = 0f;
            if (basisAim != null) basisAim.constraintActive = false;
            if (gunSprintAim != null) gunSprintAim.constraintActive = false;
            footWeightL = footWeightR = 0f;
        }

        /// <summary>复活：基线重置（朝向对齐、踝高标定、冻结脚释放、姿态渐变清零）。</summary>
        public void ApplyRevive()
        {
            deadApplied = false;
            footLagInit = false;
            footLagVel = 0f;
            crouchCur = proneCur = 0f;
            minAnkleL = minAnkleR = float.MaxValue;
            footFrozenL = footFrozenR = false;
            footWeightL = footWeightR = 0f;
            adsSmooth = 0f;
            if (animator != null) animator.SetFloat(MoveX, 0f);
            if (animator != null) animator.SetFloat(MoveZ, 0f);
            pendingRebuild = true;   // Rebind 摧毁了 rig 图内作业 → 复活后必须重建
        }

        // ---------------------------------------------------------------
        // LATEUPDATE 程序化骨骼操控（应急后备：useProceduralFallback=true 时启用。
        // 默认走 Animation Rigging 作业路径——最小实验实测作业精确有效；
        // 唯一注意点：Rebind 后须重建图（ApplyRevive 已处理））
        // ---------------------------------------------------------------

        private void ApplyProceduralRig()
        {
            if (!cachesValid || !useProceduralFallback || deadApplied) return;

            // ---- 1) 脊柱瞄准（滞后反扭 + pitch 胸/头分配；身体已即时转向 → 主要是 pitch）----
            if (upperAim != null && upperAim.weight > 0.001f)
            {
                var d = upperAim.data;
                if (d.chest != null && d.aimSource != null && d.lowerBody != null)
                {
                    float w = upperAim.weight;
                    Vector3 aimDir = d.aimSource.rotation * Vector3.forward;
                    Vector3 lowerFwd = d.lowerBody.rotation * Vector3.forward;
                    lowerFwd.y = 0f;
                    Vector3 aimFwd = aimDir; aimFwd.y = 0f;
                    if (lowerFwd.sqrMagnitude > 1e-6f && aimFwd.sqrMagnitude > 1e-6f)
                    {
                        float lag = Vector3.SignedAngle(lowerFwd, aimFwd, Vector3.up);
                        float aimPitch = -Mathf.Asin(Mathf.Clamp(aimDir.y, -1f, 1f)) * Mathf.Rad2Deg;
                        float chestTwist = Mathf.Clamp(lag, -d.maxTwist, d.maxTwist);
                        float headTwist = lag - chestTwist;
                        Vector3 right = d.root != null ? d.root.rotation * Vector3.right : transform.right;
                        right.y = 0f;
                        if (right.sqrMagnitude < 1e-6f) right = Vector3.right;
                        right.Normalize();
                        float chestPitch = -aimPitch * d.chestPitchShare;
                        float headPitch = -aimPitch * d.headPitchShare;
                        d.chest.rotation = Quaternion.AngleAxis(chestTwist * w, Vector3.up)
                                         * Quaternion.AngleAxis(chestPitch * w, right) * d.chest.rotation;
                        if (d.head != null)
                            d.head.rotation = Quaternion.AngleAxis(headTwist * w, Vector3.up)
                                            * Quaternion.AngleAxis(headPitch * w, right) * d.head.rotation;
                    }
                }
            }

            // ---- 2) 两骨 IK：腿（目标位置由 Update 的 StepFoot 维护：贴地/冻结/公转）----
            Vector3 fwd = entityRoot != null ? entityRoot.forward : transform.forward;
            Vector3 rightV = entityRoot != null ? entityRoot.right : transform.right;
            Vector3 kneePoleL = fwd + rightV * -0.35f;
            Vector3 kneePoleR = fwd + rightV * 0.35f;
            if (footIKL != null)
                SolveLimb(footIKL.data.root, footIKL.data.mid, footIKL.data.tip,
                    footTargetL, kneePoleL, footWeightL);
            if (footIKR != null)
                SolveLimb(footIKR.data.root, footIKR.data.mid, footIKR.data.tip,
                    footTargetR, kneePoleR, footWeightR);

            // ---- 3) 两骨 IK：手（目标=枪握把锚点；肘向后下外）----
            Vector3 elbowPoleR = -fwd + Vector3.down * 0.6f + rightV * 0.35f;
            Vector3 elbowPoleL = -fwd + Vector3.down * 0.6f - rightV * 0.35f;
            if (handIKR != null)
                SolveLimb(handIKR.data.root, handIKR.data.mid, handIKR.data.tip,
                    handIKR.data.target, elbowPoleR, handsWCur);
            if (handIKL != null)
                SolveLimb(handIKL.data.root, handIKL.data.mid, handIKL.data.tip,
                    handIKL.data.target, elbowPoleL, handsWCur);

            // ---- 4) 指节卷握（HandGrip 分析常量复用）----
            ApplyFingers(gripR, handsWCur);
            ApplyFingers(gripL, handsWCur);
        }

        /// <summary>
        /// 解析式两骨 IK（LateUpdate 直接写世界旋转）：
        ///  1) 由骨骼长 l1/l2 与目标距离求膝/肘关节位置（弯曲平面=极向量投影）；
        ///  2) 旋转根骨使其子关节指向关节位；旋转中骨使其末端指向目标；
        ///  3) tip（脚/手）世界朝向保持动画值（IK 只管位置，姿态归动画/约定）。
        /// 权重 w：骨骼旋转按 Slerp 混合，0=纯动画姿态。
        /// </summary>
        private void SolveLimb(Transform root, Transform mid, Transform tip, Transform target,
            Vector3 poleDir, float w)
        {
            if (root == null || mid == null || tip == null || target == null || w <= 0.001f) return;
            Vector3 H = root.position, M = mid.position, E = tip.position, T = target.position;
            float l1 = Vector3.Distance(H, M), l2 = Vector3.Distance(M, E);
            if (l1 < 1e-4f || l2 < 1e-4f) return;
            Vector3 toT = T - H;
            float dist = toT.magnitude;
            if (dist < 1e-4f) return;
            dist = Mathf.Clamp(dist, Mathf.Max(1e-3f, Mathf.Abs(l1 - l2) + 1e-3f), l1 + l2 - 1e-3f);
            Vector3 dirT = toT / toT.magnitude;

            Vector3 pole = poleDir - dirT * Vector3.Dot(poleDir, dirT);
            if (pole.sqrMagnitude < 1e-6f) pole = Vector3.Cross(dirT, Vector3.up);
            pole.Normalize();
            float a = (l1 * l1 - l2 * l2 + dist * dist) / (2f * dist);
            float h = Mathf.Sqrt(Mathf.Max(0f, l1 * l1 - a * a));
            Vector3 joint = H + dirT * a + pole * h;

            Quaternion tipW0 = tip.rotation;                       // 动画世界朝向（保持）
            Quaternion rootRot0 = root.rotation;
            Vector3 cur1 = (M - H).normalized;
            Vector3 want1 = (joint - H).normalized;
            Quaternion delta1 = Quaternion.FromToRotation(cur1, want1);
            root.rotation = Quaternion.Slerp(rootRot0, delta1 * rootRot0, w);

            Quaternion dd1 = root.rotation * Quaternion.Inverse(rootRot0);
            Vector3 M1 = H + dd1 * (M - H);
            Vector3 E1 = H + dd1 * (E - H);

            Vector3 cur2 = (E1 - M1).normalized;
            Vector3 want2 = (T - M1).normalized;
            if (cur2.sqrMagnitude > 1e-8f && want2.sqrMagnitude > 1e-8f)
            {
                Quaternion delta2 = Quaternion.FromToRotation(cur2.normalized, want2.normalized);
                Quaternion midRot0 = mid.rotation;
                mid.rotation = Quaternion.Slerp(midRot0, delta2 * midRot0, w);
            }
            tip.rotation = tipW0;   // 末端朝向还原本帧动画值
        }

        /// <summary>指节卷握：复用 HandGripConstraint 的 bind 姿态分析常量（弯曲轴/满握角/方向）。</summary>
        private void ApplyFingers(HandGripConstraint g, float w)
        {
            if (g == null || w <= 0.001f) return;
            var d = g.data;
            if (!d.analyzed || d.fingers == null) return;
            float grip = Mathf.Clamp01(d.curl) * w;
            if (grip <= 0.001f) return;
            foreach (var f in d.fingers)
            {
                if (f.root == null || f.mid == null) continue;
                if (Mathf.Abs(f.flexA) > 0.05f && f.axisA.sqrMagnitude > 0.25f)
                {
                    Quaternion bindA = f.q01Rel * Quaternion.AngleAxis(
                        f.flexA * f.infA * f.sgnA * grip, f.axisA.normalized);
                    f.root.localRotation = Quaternion.Slerp(f.root.localRotation, bindA, grip);
                }
                if (Mathf.Abs(f.flexB) > 0.05f && f.axisB.sqrMagnitude > 0.25f)
                {
                    Quaternion bindB = f.q12Rel * Quaternion.AngleAxis(
                        f.flexB * f.infB * f.sgnB * grip, f.axisB.normalized);
                    f.mid.localRotation = Quaternion.Slerp(f.mid.localRotation, bindB, grip);
                }
            }
        }
    }
}
