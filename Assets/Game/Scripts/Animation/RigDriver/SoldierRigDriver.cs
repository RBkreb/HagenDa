using HagenDa.Animation.Rigging;   // HandGripConstraint（复用件，PHASE14 决策：仅它不重写）
using HagenDa.Networking;          // PlayerPosture
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Animations.Rigging;

namespace HagenDa.Animation.RigDriver
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
        [Tooltip("趴姿脚 IK 贴地高度（米）。站立 footHeight=0.21 是“踝骨到脚底”的高度，\n" +
                 "但趴下时腿是平放的，踝离地只有 ~0.03m。若趴姿仍用 0.21，脚会被**抬起**，\n" +
                 "髋-踝距离从 0.886 缩到 0.618 → 两骨 IK 屈膝把膝盖顶到髋上方（“趴着腿抬着”）。\n" +
                 "按 proneCur 在两者间插值。")]
        public float footHeightProne = 0.03f;
        [Tooltip("射线起点在踝骨上方的距离。")]
        public float footRayUp = 0.15f;
        [Tooltip("射线向下长度（须盖过踝高+余量）。")]
        public float footRayDown = 0.45f;
        [Tooltip("触地带宽（米，相对该脚观测最低踝高=落地帧）。")]
        public float footPlantBand = 0.02f;
        [Tooltip("释放带宽（米，相对最低踝高）。")]
        public float footLiftBand = 0.09f;
        [Tooltip("静止 IK 权重上限（1=完全钉地）。")]
        [Range(0f, 1f)] public float footIKCapIdle = 1f;
        [Tooltip("移动 IK 权重上限（<1 软钉，保留剪辑抬脚自由度）。")]
        [Range(0f, 1f)] public float footIKCapMoving = 0.15f;
        [Tooltip("蹲姿脚 IK 权重上限。蹲下时模型根整体下沉（crouchOffset≈-0.5m）而剪辑仍是站立\n" +
                 "高度的走/跑，踝骨被一起带沉到地面以下 —— 若沿用移动上限(0.15)只有 15% 修正量，\n" +
                 "脚会穿地 0.44m。蹲姿因此给满权重。\n" +
                 "蹲姿**移动**时该上限会再乘 (1-useCrouchWalkLegs 的程序化权重)，由\n" +
                 "ApplyCrouchWalkLegs 接管（约束的目标取自自身输出会把脚钉死，见那里说明）。")]
        [Range(0f, 1f)] public float footIKCapCrouch = 1f;
        public float footWeightSpeed = 8f;

        [Header("Crouch-walk legs (程序化解算)")]
        [Tooltip("蹲走用程序化两骨 IK 替代约束。原因(实测)：约束的 IK 目标 XZ 取自 tip.position，\n" +
                 "而 tip 正是**上一帧约束自己的输出** —— 权重=1 时“目标 XZ = 上一帧落点”构成不动点，\n" +
                 "脚被完全钉住：蹲走步幅 0.767m→0.031m、大腿摆动 54°→6°（实测）。\n" +
                 "站姿靠 0.15 权重才侥幸保留步幅。\n" +
                 "改为 LateUpdate 程序化解算：约束权重归零 → 读到的是**干净的剪辑姿态** →\n" +
                 "目标 XZ 直接取剪辑踝位(XZ) → 步幅/前后摆动完全保留；高度取“地面+踝高+抬脚量”。")]
        public bool useCrouchWalkLegs = true;
        [Tooltip("蹲走抬脚量增益：抬脚量 = 剪辑踝高相对本周期最低点的抬升 × 该增益。\n" +
                 "蹲下时剪辑脚整体在地面之下，靠这个把“剪辑自己的抬脚”抬回地面以上。")]
        public float crouchLiftGain = 1f;
        [Tooltip("蹲走抬脚量的上限(米) —— 目标高度 = 地面 + 踝高 + 抬脚量，故这只是**摆动幅度**\n" +
                 "的上限（不是用来补偿蹲降量的：目标 Y 是绝对值，IK 会自己向下够到地面）。\n" +
                 "实测剪辑自然抬脚约 0.24m，超过此值即饱和；取 0.35 留余量。0 = 脚完全钉在地面。")]
        public float crouchLiftMax = 0.35f;

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
        [Tooltip("趴：腹部（枢轴点）在实体本地的目标位置：y=离地高度，z=前后偏移。\n" +
                 "z=0 表示腹部正落在实体竖轴上 → 趴姿水平旋转绕**腹部**（而不是绕脚根把全身扫一大圈），\n" +
                 "同时与趴姿胶囊（以实体原点为中心）对齐 —— 身体不会探出胶囊外穿墙。")]
        public Vector3 proneBellyTarget = new Vector3(0f, 0.28f, 0f);
        [Tooltip("（旧）趴根位移：仅作兼容保留，趴姿位移已改由 proneBellyTarget 决定。")]
        public Vector3 proneOffset = new Vector3(0f, -0.9f, 0.25f);
        [Tooltip("趴：模型根前倾角（度，正=前倾脸朝下贴地；负=仰躺）。")]
        public float pronePitch = 75f;   // 正=前倾脸朝下（绕X正角使+Z前向转向-Y）；负值会成仰躺
        [Tooltip("身体旋转枢轴高度（米，模型本地）：趴/前倾的旋转轴高度。\n" +
                 "<=0 = 运行时自动取骨盆骨骼（DEF-spine）高度 → 绕**肚子**旋转，而不是绕脚根/膝盖。")]
        public float bodyPivotHeight = 0f;
        [Tooltip("姿态位移/前倾渐变速率（每秒）。")]
        public float postureRampSpeed = 5f;

        [Header("Aim")]
        [Tooltip("腰射瞄准目标点距离（米）——枪基准 AimConstraint 的源，即“相机射线多远处的点”。\n" +
                 "只影响约束解算的数值余量，不影响枪的朝向（朝向由脚本按 Slerp 混合后写入）。")]
        public float aimTargetDistance = 50f;
        [Tooltip("腰射枪位：让**枪托(Stock)**落在右肩 GunCylinder 柱面上 → 枪绕右肩运动。\n" +
                 "旧实现用眼空间固定偏移，下俯时该偏移被相机俯仰带出“向身后”的分量，枪托捅进后背。\n" +
                 "绕肩柱面运动则下俯时枪只绕肩下垂 —— 不会穿模。0 = 自动取 GunCylinder 胶囊世界半径。")]
        public float hipOrbitRadius = 0f;
        [Tooltip("腰射回落偏移（仅在 GunCylinder / Stock 缺失时使用）。AimSource 空间：x=右 y=下 z=前。")]
        public Vector3 hipHoldOffset = new Vector3(0.05f, -0.24f, 0.20f);
        [Tooltip("ADS 瞳距（照门沿枪管轴到眼睛的距离，米，可调）。")]
        public float adsEyeRelief = 0.13f;
        [Tooltip("aimAmount→枪位/约束的过渡速率（每秒）。")]
        public float aimBlendSpeed = 6f;

        [Header("Sprint (收枪摆动)")]
        [Tooltip("冲刺枪位相对腰射位的横向偏移（眼空间 x；负 = 向左）。冲刺时枪整体偏右 → 给负值拉回。")]
        public float sprintLateralOffset = -0.07f;
        [Tooltip("冲刺枪位相对腰射位的**高度**偏移（米；正 = 抬高、负 = 压低）。\n" +
                 "冲刺姿态已**去俯仰**（枪不随视线点头），故这里是**世界竖直**方向，\n" +
                 "与平视时一致、不随相机俯仰倾斜。0 = 与腰射柱面点等高。")]
        public float sprintHeightOffset = 0f;
        [Tooltip("冲刺时枪相对基准下压的后撤量(m)。")]
        public float sprintDropback = 0.06f;
        [Tooltip("腰射↔冲刺过渡速率（每秒）—— 位置/瞄准权重/摆枪幅度都按此淡入淡出。")]
        public float sprintBlendSpeed = 6f;
        [Tooltip("冲刺摆枪的**横向平移**振幅(m)。AimConstraint 瞄准的是源的位置，所以摆动必须\n" +
                 "以平移表示(只转 SprintAim 自身不会改变其位置、不影响约束输出)。")]
        public float sprintSwayLateral = 0.06f;
        [Tooltip("冲刺摆枪的**滚转**振幅(度) —— 绕枪自身枪管轴，枪顶左右倾。\n" +
                 "约束的 worldUpType=SceneUp 把滚转钉在世界朝上，所以这个量**不能**靠转 SprintAim\n" +
                 "实现（那是死代码），改为直接写在枪的局部旋转上，与横向平移共用同一相位。")]
        public float sprintSwayAmplitude = 5f;
        [Tooltip("冲刺摆枪频率(Hz)。")]
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

        [Header("Idle 脚位 (静止时的落点)")]
        [Tooltip("肘/膝的 IK pole(hint) 固定复用预制体 Hint 对象的 RtElbow/LtElbow/RtKnee/LtKnee ——\n" +
                 "这些点相对角色静态（只随整体 yaw/趴姿俯仰转动），位置恒可预测，不会像每帧重算\n" +
                 "那样在部分姿势跑到身后，导致肘/膝绕错误平面弯曲。\n\n" +
                 "静止时脚位 = 腿根 + 模型旋转 × T pose 下“腿根->踝”偏移（同样是静态基准）。\n" +
                 "该偏移随身体姿态旋转：站立=脚下、分开；趴下=身后、仍分开。\n" +
                 "用静态基准而非“上一帧 IK 输出”后，换姿态时脚会立刻落到新姿态对应位置\n" +
                 "（修：趴→蹲时脚留在趴位、趴下两脚并到一起）。")]
        public bool useRestFootPlacement = true;

        [Header("Head pitch (DEF-spine.006 跟随相机俯仰)")]
        [Tooltip("头部俯仰跟随相机：MultiAimConstraint 约束 **DEF-spine.006**，源=Driver_AimTarget\n" +
                 "（Rig_Drivers 子对象，位于相机射线 50m 处，世界旋转/位置含相机俯仰），\n" +
                 "**只启用俯仰轴**（constrainedXAxis）。\n\n" +
                 "为什么用 MultiAim 而不是 MultiRotation：MultiAim 是**指向世界点**，\n" +
                 "俯仰由“头→目标”的仰角天然给出，不需要把相机旋转语义复制过来；\n" +
                 "配合轴掩码只取 X，偏航/翻滚仍由动画与脊柱约束决定。\n" +
                 "轴掩码语义见 MultiAimConstraintJob：对 localEuler 做**按轴 Select**，\n" +
                 "且旋转轴 crossDir 也被掩码投影 → mask=(X,0,0) 即“只解俯仰”。\n" +
                 "该骨骼世界旋转≈单位阵（父 .005 仅 ~5.5° 倾斜）→ 其局部 X≈世界右轴=俯仰轴。")]
        public bool headPitchEnabled = true;
        [Tooltip("头部俯仰约束权重（0=不跟随，1=完全跟随相机俯仰）。")]
        [Range(0f, 1f)] public float headPitchWeight = 1f;
        [Tooltip("俯仰跟随的关节限位（度，相对“头→目标”的夹角）。±180=不限位。")]
        public Vector2 headPitchLimits = new Vector2(-180f, 180f);

        [Header("Knee outward correction (内八修正)")]
        [Tooltip("纠正膝/脚内八：绕**髋→踝轴**旋转大腿，使膝的弯曲方向朝“正前 + 向外偏 kneeOutwardDeg”。\n\n" +
                 "为什么这样修：内八是**剪辑自带的姿态**（实测站立静止时膝相对髋→踝轴偏内 11.8mm\n" +
                 "≈1.5°），而 IK 的 pole 救不了它 —— 腿接近伸直时“弯曲平面”本身退化\n" +
                 "(abProj≈0)，pole 对膝位几乎无影响（实测 outward 0→8° 仅改变 ~3mm 且站立为 0）。\n" +
                 "绕髋→踝轴旋转则**不改变踝的位置**（旋转轴穿过踝），只把膝与脚掌的朝向转正，\n" +
                 "因此不破坏任何触地/IK 结果，也不会与 foot IK 打架。\n" +
                 "0 = 不修正（保留剪辑原样）。")]
        public bool kneeOutwardCorrect = true;
        [Tooltip("膝弯曲方向的目标外偏角（度）。正=向外。约 1.5~3° 即可抵消剪辑内八。")]
        public float kneeOutwardDeg = 2f;
        [Tooltip("修正的平滑速率（每秒）；避免姿态切换瞬间跳变。")]
        public float kneeOutwardSmooth = 8f;

        // ---------------------------------------------------------------
        // Cached rig pieces
        // ---------------------------------------------------------------
        [SerializeField] private Animator animator;
        [SerializeField] private RigBuilder rigBuilder;
        [SerializeField] private UpperBodyAimConstraint upperAim;
        [SerializeField] private MultiAimConstraint headAim;   // DEF-spine.006 俯仰跟随（MultiAim→Driver_AimTarget）
        [SerializeField] private TwoBoneIKConstraint footIKL, footIKR;
        [SerializeField] private TwoBoneIKConstraint handIKR, handIKL;
        [SerializeField] private HandGripConstraint gripR, gripL;
        [SerializeField] private Transform footTipL, footTipR;
        [SerializeField] private Transform footTargetL, footTargetR;
        [SerializeField] private Transform footHintL, footHintR;
        [SerializeField] private Transform driverAimSource;   // 眼位（骨架外，rig 作业可实时读）
        [SerializeField] private Transform driverAimTarget;   // 相机射线 50m 目标点（**恒=相机指向**）
        [SerializeField] private Transform gunAimTarget;      // 枪械瞄准目标（腰射=相机 / 冲刺=SprintAim 混合）
        [SerializeField] private Transform headBone;          // DEF-spine.005
        [SerializeField] private Transform headPitchBone;     // DEF-spine.006（俯仰跟随的被约束骨骼）
        [SerializeField] private Transform aimSourceHead;     // 头部骨骼子对象 AimSource（PHASE14 规范节点）
        [SerializeField] private Transform sprintAimSource;   // 人物子对象 SprintAim（冲刺收枪基准）
        [SerializeField] private Transform headAnchor;        // headcollider（相机球）

        // 关节/基准点缓存。hint 用预制体 Hint 对象里的**静态**基准点（见 EnsureCache）：
        // 相对角色静止，不会每帧被求解推到意外位置。
        [SerializeField] private Transform hipBone;           // DEF-spine（腹部枢轴）
        [SerializeField] private Transform thighBoneR, thighBoneL;   // DEF-thigh.*（腿根，静止脚位基准）
        [SerializeField] private Transform staticHintRoot;    // 预制体 Hint 对象（4 个静态基准点的父级）
        private Vector3 footRestOffsetL;                      // T pose 下 腿根->踝 的模型空间偏移（左）
        private Vector3 footRestOffsetR;                      // 右
        private Quaternion sprintAimBaseRot = Quaternion.identity;   // SprintAim 摆动基准（局部）
        private Vector3 sprintAimBasePos;                            // SprintAim 摆动基准（局部位置）

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
        private Transform gunStock;                      // M4 Stock：腰射时落在右肩柱面上的枪托锚点
        private Transform gunCylinder;                   // Fatui GunCylinder：右肩柱体（腰射绕它运动）
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
        private float minAnkleL = float.MaxValue, minAnkleR = float.MaxValue;
        private float crouchLegProcCur;                                  // 蹲走程序化解算权重 0..1
        private float minCrouchAnkleL = float.MaxValue, minCrouchAnkleR = float.MaxValue;  // 蹲走剪辑踝高基线
        private float crouchCur, proneCur;      // 姿态 0..1 渐变
        private Vector3 lastBodyOffsetExtra;    // 最近一帧额外根位移（跳跃 dip 等，LateUpdate 复写用）
        private float adsSmooth;                 // aimAmount 平滑
        private float sprintBlend;               // 腰射↔冲刺过渡量 0..1（位置/朝向/摆枪共用）
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

            // 枪械瞄准目标（与 Driver_AimTarget **分开**）：
            // 结构约定（用户设计）——
            //   Driver_AimTarget  **恒等于相机指向**（每帧由眼位+视线方向写入，任何状态下都不被改写）
            //   Driver_GunAimTarget 枪械瞄准目标：腰射=相机指向；冲刺=SprintAim 方向（脚本 Slerp 混合）
            // 腰射时枪械由 Driver_AimTarget 约束、冲刺时由 SprintAim 约束；头部俯仰跟随读
            // Driver_AimTarget（恒=相机），故冲刺收枪不会再带动头部。
            // 旧实现在冲刺时把 Driver_AimTarget 本身改写成侧向点（实测偏离相机 94°）→ 头部跟着甩。
            if (gunAimTarget == null)
            {
                gunAimTarget = drivers.Find("Driver_GunAimTarget");
                if (gunAimTarget == null)
                {
                    var go = new GameObject("Driver_GunAimTarget");
                    go.transform.SetParent(drivers, false);
                    gunAimTarget = go.transform;
                }
            }

            headBone = FindDeep(modelRoot, "DEF-spine.005");
            if (headBone == null && animator.isHuman)
                headBone = animator.GetBoneTransform(HumanBodyBones.Head);
            if (headBone == null) Debug.LogWarning($"[SoldierRigDriver] {name}: head bone DEF-spine.005 not found", this);
            else if (aimSourceHead == null) aimSourceHead = headBone.Find("AimSource");

            // 俯仰跟随骨骼 DEF-spine.006（头骨本体，.005 的子级）。缺失时退回 .005。
            if (headPitchBone == null)
            {
                headPitchBone = FindDeep(modelRoot, "DEF-spine.006");
                if (headPitchBone == null && headBone != null && headBone.childCount > 0)
                    headPitchBone = headBone.GetChild(0);
                if (headPitchBone == null) headPitchBone = headBone;
            }

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
            sprintAimBasePos = sprintAimSource.localPosition;

            // headcollider：优先 Head 命中箱，否则按名查找。
            if (headAnchor == null)
            {
                foreach (var t in modelRoot.GetComponentsInChildren<Transform>(true))
                    if (t.name.ToLowerInvariant().Contains("headcollider")) { headAnchor = t; break; }
            }

            // ---- IK pole(hint)：直接复用预制体 Hint 对象里的 4 个静态基准点 ----
            // Hint 是模型根的**直接子物体**（在动画骨架之外），所以它不随动画变形，只随
            // 角色整体位姿（yaw/趴姿俯仰）转动 —— 正是 pole 需要的“相对角色静态”。
            // 旧实现每帧按关节位置+方向重算 hint，会在部分姿势把 hint 推到身后等意外位置，
            // 导致肘/膝绕错误平面弯曲。改为静态基准点后位置恒可预测。
            hipBone = FindDeep(modelRoot, "DEF-spine");
            thighBoneR = FindDeep(modelRoot, "DEF-thigh.R");
            thighBoneL = FindDeep(modelRoot, "DEF-thigh.L");
            staticHintRoot = modelRoot.Find("Hint");
            Transform hintElbowR = staticHintRoot != null ? staticHintRoot.Find("RtElbow") : null;
            Transform hintElbowL = staticHintRoot != null ? staticHintRoot.Find("LtElbow") : null;
            Transform hintKneeR = staticHintRoot != null ? staticHintRoot.Find("RtKnee") : null;
            Transform hintKneeL = staticHintRoot != null ? staticHintRoot.Find("LtKnee") : null;
            // 兜底：Hint 缺失时退回驱动空物体（仍是静态引用，只是位置由别的脚本摆）。
            if (hintElbowR == null) hintElbowR = FindDeep(modelRoot, "ElbowHint_R");
            if (hintElbowL == null) hintElbowL = FindDeep(modelRoot, "ElbowHint_L");
            if (hintKneeR == null) hintKneeR = footHintR;
            if (hintKneeL == null) hintKneeL = footHintL;

            if (handIKR != null && hintElbowR != null)
            { var d = handIKR.data; d.hint = hintElbowR; d.hintWeight = 1f; handIKR.data = d; }
            if (handIKL != null && hintElbowL != null)
            { var d = handIKL.data; d.hint = hintElbowL; d.hintWeight = 1f; handIKL.data = d; }
            if (footIKR != null && hintKneeR != null)
            { var d = footIKR.data; d.hint = hintKneeR; d.hintWeight = 1f; footIKR.data = d; }
            if (footIKL != null && hintKneeL != null)
            { var d = footIKL.data; d.hint = hintKneeL; d.hintWeight = 1f; footIKL.data = d; }

            SetupHeadPitchConstraint();

            // ---- 静止脚位基准：腿根 + 模型空间 rest 偏移（见 StepFoot）----
            // 在 Awake/OnEnable 阶段动画尚未求值，骨骼正处于预制体的绑定/静止姿态，
            // 故这里量到的就是 T pose 下“腿根 -> 踝”的模型空间偏移。存成常量后，
            // 静止时脚位不再依赖上一帧 IK 的输出（旧实现因此自锁：趴姿换蹲时脚留在原地）。
            if (thighBoneL != null && footTipL != null)
                footRestOffsetL = modelRoot.InverseTransformPoint(footTipL.position)
                                - modelRoot.InverseTransformPoint(thighBoneL.position);
            if (thighBoneR != null && footTipR != null)
                footRestOffsetR = modelRoot.InverseTransformPoint(footTipR.position)
                                - modelRoot.InverseTransformPoint(thighBoneR.position);

            cachesValid = true;
            return true;
        }

        /// <summary>
        /// 头部俯仰跟随（DEF-spine.006 ← Driver_AimTarget，仅俯仰轴）。
        ///
        /// 用 MultiAimConstraint 指向**世界点** Driver_AimTarget（相机射线 50m 处）：
        /// 俯仰 = “头→目标”的仰角，天然跟随相机俯仰，无需复制相机旋转语义。
        /// constrainedAxes 只开 X：MultiAimConstraintJob 对 localEuler 做按轴 Select，
        /// 且旋转轴 crossDir 也被掩码投影 → 只解俯仰，偏航/翻滚仍归动画与脊柱约束。
        ///
        /// **必须挂在某个 Rig 之下**（RigUtils.GetConstraints = rig.GetComponentsInChildren
        /// &lt;IRigConstraint&gt;）→ 复用既有 Rig_UpperBody，避免新建 Rig/图层。
        /// 幂等：重复调用只更新 data，不重复建节点；新挂组件后置 pendingRebuild
        /// 让 rig 图重建以绑定新句柄。
        /// </summary>
        private void SetupHeadPitchConstraint()
        {
            if (headPitchBone == null || driverAimTarget == null) return;

            // 宿主：Rig_UpperBody（含 UpperAim 的那个 Rig）→ 若缺失则退回模型根下的同名节点。
            Transform host = null;
            if (upperAim != null) host = upperAim.transform.parent;
            if (host == null) host = transform.Find("Rig_UpperBody");
            if (host == null)
            {
                var go = new GameObject("Rig_UpperBody");
                go.transform.SetParent(transform, false);
                host = go.transform;
            }

            Transform existing = host.Find("HeadPitch");
            bool created = false;
            if (headAim == null)
            {
                headAim = existing != null ? existing.GetComponent<MultiAimConstraint>() : null;
                if (headAim == null)
                {
                    var go = new GameObject("HeadPitch");
                    go.transform.SetParent(host, false);
                    headAim = go.AddComponent<MultiAimConstraint>();
                    created = true;
                }
            }

            var d = headAim.data;
            d.constrainedObject = headPitchBone;
            var src = new WeightedTransformArray(1);
            src[0] = new WeightedTransform(driverAimTarget, 1f);
            d.sourceObjects = src;
            d.aimAxis = MultiAimConstraintData.Axis.Z;      // 头骨局部 +Z = 视线方向
            d.upAxis = MultiAimConstraintData.Axis.Y;
            d.worldUpType = MultiAimConstraintData.WorldUpType.None;
            d.maintainOffset = false;
            d.limits = headPitchLimits;
            d.constrainedXAxis = true;                      // 只跟随俯仰
            d.constrainedYAxis = false;
            d.constrainedZAxis = false;
            headAim.data = d;
            headAim.weight = headPitchEnabled ? headPitchWeight : 0f;

            // 新组件不在已构建的图里 → 重建（与绑枪同路径，等 animator 初始化后再 Build）。
            if (created) pendingRebuild = true;
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
        ///  3. 确保枪基准 AimConstraint（源=Driver_GunAimTarget：腰射=相机点/冲刺=SprintAim 方向）
        ///     与枪自身 SprintAim AimConstraint（源=人物子对象 SprintAim）存在
        ///     （默认停用，按三态切换）。
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
            gunRearSight = FindDeep(gun.transform, "Rear_Sight");   // 近眼瞄具（3D 件）
            gunFrontSight = FindDeep(gun.transform, "Sight");       // 近枪口瞄具（3D 件）
            gunStock = FindDeep(gun.transform, "Stock");            // 枪托：腰射落右肩柱面
            // 右肩柱体（Fatui 预制体的 GunCylinder）——腰射绕它运动。
            gunCylinder = FindDeep(modelRoot, "GunCylinder");

            // 枪管轴（枪根局部，= 枪口方向）——单一事实源。
            // **真瞄准点**：M4 预制体 RearAim(挂在 Rear_Sight 下) / SightAim(挂在 Sight 下)，
            // 二者共线于枪管，是精确瞄准点 → 首选：轴 = SightAim - RearAim。
            // 缺失时回落瞄具 3D 件：Sight - Rear_Sight；再缺失：枪根本地 +Z。
            Vector3 axis;
            if (gunRearAim != null && gunSightAim != null)
            {
                axis = gun.transform.InverseTransformPoint(gunSightAim.position)
                     - gun.transform.InverseTransformPoint(gunRearAim.position);
            }
            else if (gunRearSight != null && gunFrontSight != null)
            {
                axis = gun.transform.InverseTransformPoint(gunFrontSight.position)
                     - gun.transform.InverseTransformPoint(gunRearSight.position);
            }
            else
            {
                axis = Vector3.forward;
            }
            if (axis.sqrMagnitude > 1e-8f)
            {
                gunAimLocalAxis = axis.normalized;
                // 上方向 = 近眼瞄准点相对枪管轴的垂直分量（瞄准点在枪管上方 → +Y 侧）。
                Vector3 upRef = gunRearAim != null
                    ? gun.transform.InverseTransformPoint(gunRearAim.position)
                    : (gunRearSight != null ? gun.transform.InverseTransformPoint(gunRearSight.position) : Vector3.up);
                Vector3 up = upRef - gunAimLocalAxis * Vector3.Dot(upRef, gunAimLocalAxis);
                if (up.sqrMagnitude > 1e-8f) gunAimLocalUp = up.normalized;
            }
            // 对齐旋转：把枪口轴映射到枪根本地 +Z → 基准本地 +Z 恒=枪口方向，
            // 基准 AimConstraint(默认 aimVector=+Z) 与 ADS 直解共用此约定。
            gunAlignRot = Quaternion.Inverse(Quaternion.LookRotation(gunAimLocalAxis, gunAimLocalUp));
            gun.transform.localRotation = gunAlignRot;

            // 枪基准 AimConstraint：源 = **Driver_GunAimTarget**（枪械瞄准目标，与相机目标分离）。
            // 腰射时该点=相机射线 50m 点（脚本按相机写入）→ 等效“枪由 Driver_AimTarget 约束”；
            // 冲刺时该点=沿 SprintAim 方向（脚本 Slerp 混合）→ 等效“枪由 SprintAim 约束”。
            // 这样 Driver_AimTarget 保持纯相机语义（头部俯仰跟随它），不再被冲刺改写。
            if (basisAim == null) basisAim = weaponBasis.GetComponent<AimConstraint>();
            if (basisAim == null) basisAim = weaponBasis.gameObject.AddComponent<AimConstraint>();
            basisAim.constraintActive = false;
            basisAim.SetSources(new System.Collections.Generic.List<ConstraintSource>
            {
                new ConstraintSource { sourceTransform = gunAimTarget, weight = 1f }
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

            // ---- 转向：模型**即时跟随胶囊**，下半身滞后由**脚 IK 基准点滞后**形成（原设计）----
            // 用户确认的设计：
            //   角色模型与胶囊是一体 —— 胶囊随相机转向时模型作为子级自然同步旋转，故
            //   模型根（含上半身）**必须即时**对齐，**不能**给它加滞后（否则上半身一起滞后）。
            //   "下半身滞后"的观感来自**脚 IK 基准点 footLagYaw 滞后跟随**：躯干已转过去，
            //   脚仍钉在原世界位置 → 腿被"拧"着（这才是滞后），脚锚随后按 turnStartDelay /
            //   footLagSmoothTime 逐步追上新朝向（挪步），拧劲随之解开。
            float aimYaw = s.eyeRot.eulerAngles.y;
            bool proneNow = s.dead || s.posture == PlayerPosture.Prone;
            dampedYaw = aimYaw;                       // 模型根（上/下半身）即时对齐，无差速

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
                    // 静止转向：身体已即时转过去，脚锚**钉住 turnStartDelay 秒**后才追赶
                    // （PHASE14 转身专项：上半身开始转向后 1s 脚才开始挪）。
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
                Quaternion bodyRot = Quaternion.Euler(pitch, dampedYaw - parentYaw, 0f);
                // 枢轴 = 腹部。站立时取**骨盆骨骼**高度（DEF-spine，模型本地 ~1.09m）：
                //   - 趴下时身体绕肚子转（而不是绕脚根把整个头顶扫一大圈）；
                //   - 水平旋转（bodyRot 的 yaw 分量）本来就绕实体竖轴，与枢轴高度无关。
                // 显式 bodyPivotHeight>0 时仍以它为准（保留旧行为/用户可调）。
                float pivotH = bodyPivotHeight > 0f ? bodyPivotHeight
                    : (hipBone != null ? modelRoot.InverseTransformPoint(hipBone.position).y : 0.7f);
                // 位移公式 p = bellyLocal - R*pivot 使腹部精确落到 proneBellyTarget：
                //   站立(proneCur=0)：bellyLocal=pivot → 位移恒 0；
                //   趴下(proneCur=1)：腹部落到目标点，身体绕腹部旋转且天然不穿地。
                Vector3 pivot = Vector3.up * pivotH;
                Vector3 bellyLocal = Vector3.Lerp(pivot, proneBellyTarget, proneCur);
                modelDrop.localRotation = bodyRot;
                modelDrop.localPosition = modelBaseLocalPos
                    + crouchOffset * crouchCur
                    + s.bodyOffsetExtra
                    + (bellyLocal - bodyRot * pivot);
            }
            lastBodyOffsetExtra = s.bodyOffsetExtra;

            // ---- 瞄准源/目标点（模型根写定之后再写世界位姿：枪基准 AimConstraint
            //      与 UpperAim 都读它们；本帧内根变换不再变动，位姿精确）----
            // **Driver_AimTarget 恒=相机指向**：每帧无条件写成“眼位 + 视线×距离”，
            // 任何状态（腰射/冲刺/ADS）都不再改写它 —— 头部俯仰跟随（MultiAim→该点）
            // 因此永远只反映相机。枪械的冲刺朝向改由 Driver_GunAimTarget 承担（见下）。
            if (driverAimSource != null)
            {
                driverAimSource.position = s.eyePos;
                driverAimSource.rotation = s.eyeRot;
            }
            if (driverAimTarget != null)
                driverAimTarget.position = s.eyePos + s.eyeRot * Vector3.forward * AimTargetDistanceNow();
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

            // 头部俯仰跟随：存活时按开关/权重生效（趴下时身体已贴地，头部无需再抬）。
            if (headAim != null)
            {
                float headTarget = (headPitchEnabled && !s.dead) ? headPitchWeight * (1f - proneCur) : 0f;
                headAim.weight = Mathf.MoveTowards(headAim.weight, headTarget, dt * 5f);
            }

            // ---- 脚部贴地 IK ----
            bool crouched = s.posture == PlayerPosture.Crouch;
            // 蹲走交给程序化解算（见 useCrouchWalkLegs）：约束权重按 crouchLegProcCur 让位，
            // 否则“目标 XZ ← 约束自己的输出”会把脚钉死。
            bool crouchWalk = useCrouchWalkLegs && crouched && s.moving
                              && !s.airborne && !s.dead;
            crouchLegProcCur = Mathf.MoveTowards(crouchLegProcCur, crouchWalk ? 1f : 0f,
                footWeightSpeed * dt);
            float cap = (s.moving && !crouched) ? footIKCapMoving
                      : (crouched ? footIKCapCrouch : footIKCapIdle);
            cap *= 1f - crouchLegProcCur;   // 让位给程序化解算
            // 滑铲仍要脚贴地（仅 Y 固定）；滞空/死亡完全释放。
            float airGate = (s.airborne || s.dead) ? 0f : 1f;
            bool yOnly = s.sliding;   // 滑铲：仅 Y 固定，脚贴地滑行
            // 强制贴地：蹲/滑铲/静止都用持续 IK（低位与非落地帧下基线门控会漏 → 脚嵌地）。
            bool forcePlant = (!s.moving || s.posture == PlayerPosture.Crouch || s.sliding)
                              && !s.airborne && !s.dead;
            // 静止且未移动 → 脚位取静态基准（T pose 落点），并由 footLagYaw 的滞后量做
            // **反向公转**（见 StepFoot）→ 这就是"下半身滞后"的来源：躯干已即时转向，
            // 脚仍留在原世界位置，腿被拧着；脚锚随后追上，拧劲解开（挪步）。
            //
            // **不能**在这里再用"滞后已收敛(lag<1°)"当门控：反向公转只在 restPlace=true
            // 时执行，而转身过程中 lag 必然非零 —— 两者互为条件，等于把整个滞后机制
            // 短路掉（脚会随身体一起立刻转，看不出任何滞后）。保持"静止即用静态基准"。
            bool restPlace = useRestFootPlacement && forcePlant && !s.moving && !yOnly;
            footPlantL = StepFoot(footTipL, footTargetL, thighBoneL, footRestOffsetL, cap * airGate,
                                  yOnly, forcePlant, restPlace, dt, ref minAnkleL, ref footWeightL, footIKL);
            footPlantR = StepFoot(footTipR, footTargetR, thighBoneR, footRestOffsetR, cap * airGate,
                                  yOnly, forcePlant, restPlace, dt, ref minAnkleR, ref footWeightR, footIKR);

            // ---- 枪械三态表现 ----
            UpdateWeaponPose(s, dt);
        }

        /// <summary>
        /// 单脚贴地 IK（无状态）：射线查地面 → 目标 = 落点 + 贴地高度。
        ///
        /// 脚位来源两选一：
        ///  - restPlace=true（静止）：腿根 + 模型旋转 × T pose 下“腿根->踝”偏移。
        ///    静态基准，**不依赖上一帧 IK 输出** —— 换姿态（趴→蹲）时脚立刻落到新姿态
        ///    对应位置，而不是留在原姿态的位置；左右两脚各用自己的偏移，故始终分开。
        ///  - restPlace=false（移动/滑铲）：当前动画踝下方，保留走路抬脚与滑行。
        ///
        /// 转身滞后：静止转向时脚位绕实体竖轴按 lag = footLagYaw - aimYaw 反向公转
        /// （等价旧“冻结世界位置 + 按滞后角公转”，但不再需要冻结状态）。
        /// 返回触地程度 0..1（供调试/后续使用）。
        /// </summary>
        private float StepFoot(Transform tip, Transform target, Transform thighBone, Vector3 restOffset,
            float capWeight, bool yOnly, bool forcePlant, bool restPlace, float dt,
            ref float minAnkle, ref float weightCur, TwoBoneIKConstraint ik)
        {
            if (tip == null || target == null)
            {
                if (ik != null) { weightCur = 0f; ik.weight = 0f; }
                return 0f;
            }

            // ---- 门控（触地程度）：最低踝高基线，自适应标定 + 极慢回弹适应坡面。
            //      静止/蹲/滑铲强制触地（forcePlant，覆盖基线门控）。----
            float rel = tip.position.y - (entityRoot != null ? entityRoot.position.y : transform.position.y);
            if (rel < minAnkle) minAnkle = rel;
            else minAnkle = Mathf.MoveTowards(minAnkle, rel, dt * 0.05f);
            float t = (rel - (minAnkle + footPlantBand)) / Mathf.Max(0.01f, footLiftBand - footPlantBand);
            float plant = forcePlant ? 1f : Mathf.Clamp01(1f - t);

            // ---- IK 权重 = 上限 × 触地程度：**脚落地才开 IK、抬脚就关**（PHASE14 约定）。
            //      门控值此前只被记录、未参与权重 → 移动中 IK 是恒定软钉（"间歇性触地"的观感
            //      其实来自 0.15 权重本身）。乘上 plant 后：触地相 ≈ cap、摆动相 → 0（纯剪辑）。
            //      静止/蹲/滑铲 forcePlant=1 → 权重恒 = cap（持续贴地）。
            if (ik != null)
            {
                float want = capWeight * plant;
                float w = Mathf.MoveTowards(weightCur, want, footWeightSpeed * dt);
                weightCur = w;
                ik.weight = w;
            }

            // ---- 目标 XZ 来源 ----
            Vector3 ankle = tip.position;
            Vector3 xz;
            if (restPlace && thighBone != null)
            {
                // 静态基准：腿根 + 模型旋转 × rest 偏移（模型下方向随姿态转 → 站=脚下、趴=身后）。
                Vector3 desired = thighBone.position + modelRoot.rotation * restOffset;
                // 转身滞后：脚位绕实体竖轴按 lag 反公转（身体已转、脚滞后跟上）。
                float lag = Mathf.DeltaAngle(footLagYaw, dampedYaw);
                if (Mathf.Abs(lag) > 0.01f)
                {
                    Vector3 pivot = entityRoot != null ? entityRoot.position : transform.position;
                    Vector3 r = desired - pivot;
                    r.y = 0f;
                    Vector3 rot = Quaternion.Euler(0f, -lag, 0f) * r;
                    desired = new Vector3(pivot.x + rot.x, desired.y, pivot.z + rot.z);
                }
                xz = desired;
            }
            else
                xz = ankle;   // 移动/滑铲：动画踝位置（保留抬脚/滑行自由度）

            // ---- 地面高度 ----
            Vector3 origin = xz + Vector3.up * footRayUp;
            // 地面探测必须**排除本实体自身**的碰撞体（运动胶囊 + 模型命中箱 + 枪）。
            // 脚踝在胶囊半径内，从脚踝向下射会先打到自己的胶囊 → 目标被顶到胶囊表面，
            // 趴姿（胶囊沿 Z 放长）时尤其严重：脚被悬空、膝被顶高。
            if (!ProbeGround(origin, footRayUp + footRayDown, out var hit))
            {
                // 探不到（沉地/悬空）→ 从实体根上方重探（自恢复）。
                Vector3 origin2 = (entityRoot != null ? entityRoot.position : transform.position)
                                  + Vector3.up * 0.6f;
                float far = proneCur > 0.5f ? 3f : 1.5f;   // 趴姿身体前伸，需更长射线
                if (!ProbeGround(origin2, far, out hit))
                    ProbeGround(xz + Vector3.up * 1.5f, 2.5f, out hit);
            }
            if (hit.collider != null)
            {
                // 趴姿踝离地高度远小于站立（腿平放）→ 按 proneCur 在 footHeight 与
                // footHeightProne 之间插值，否则趴姿会把脚抬起来、逼出屈膝。
                float fh = Mathf.Lerp(footHeight, footHeightProne, proneCur);
                target.position = new Vector3(xz.x, hit.point.y + fh, xz.z);
                Vector3 fwd = entityRoot != null ? entityRoot.forward : transform.forward;
                fwd.y = 0f;
                if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.forward;
                target.rotation = Quaternion.LookRotation(fwd.normalized, hit.normal);
            }
            else
                target.position = new Vector3(xz.x, ankle.y, xz.z);

            return plant;
        }

        /// <summary>
        /// 地面探测（排除本实体自身的碰撞体：运动胶囊 / 模型命中箱 / 枪）。
        /// 脚踝位于胶囊半径内，直接 Raycast 会先命中自己的胶囊 → 脚目标被顶到胶囊
        /// 表面（趴姿胶囊沿 Z 放长时最严重：脚悬空、膝被顶高）。用 NonAlloc 取最近
        /// 的“非自身”命中。
        /// </summary>
        private bool ProbeGround(Vector3 origin, float distance, out RaycastHit best)
        {
            best = default;
            int n = Physics.RaycastNonAlloc(origin, Vector3.down, _rayHits, distance,
                                            groundMask, QueryTriggerInteraction.Ignore);
            float bestDist = float.MaxValue;
            bool found = false;
            for (int i = 0; i < n; i++)
            {
                var c = _rayHits[i].collider;
                if (c == null) continue;
                if (entityRoot != null && c.transform.IsChildOf(entityRoot)) continue;   // 自身
                if (_rayHits[i].distance < bestDist)
                {
                    bestDist = _rayHits[i].distance;
                    best = _rayHits[i];
                    found = true;
                }
            }
            return found;
        }

        private readonly RaycastHit[] _rayHits = new RaycastHit[16];

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
            ApplyCrouchWalkLegs();
            ApplyKneeOutward();
        }

        // ---------------------------------------------------------------
        // 膝外偏修正（内八）
        // ---------------------------------------------------------------

        /// <summary>
        /// 绕**髋→踝轴**旋转大腿，把膝的弯曲方向转正到“前向 + 外偏 kneeOutwardDeg”。
        ///
        /// 关键性质：旋转轴穿过髋与踝 → **踝位置不变**，只改变膝位方向与脚掌朝向，
        /// 因此可与脚 IK/触地共存，不会把脚推出地面或与自己打架（区别于直接改目标点）。
        /// 腿接近伸直时“弯曲平面”退化（perp≈0），此时跳过，避免绕不定轴乱转。
        ///
        /// 每侧维护自己的平滑量，避免蹲起/落地瞬间跳变。
        /// </summary>
        private void ApplyKneeOutward()
        {
            if (!kneeOutwardCorrect) return;
            float dt = Time.deltaTime;
            SolveKneeOutward(footIKL, -1f, dt, ref kneeOutFixL);   // 左腿：向外 = 模型 −X
            SolveKneeOutward(footIKR, +1f, dt, ref kneeOutFixR);   // 右腿：向外 = 模型 +X
        }

        private void SolveKneeOutward(TwoBoneIKConstraint ik, float outwardSign, float dt,
            ref float applied)
        {
            if (ik == null) return;
            Transform hip = ik.data.root, mid = ik.data.mid, tip = ik.data.tip;
            if (hip == null || mid == null || tip == null) return;

            Vector3 axis = tip.position - hip.position;
            if (axis.sqrMagnitude < 1e-8f) return;
            Vector3 n = axis.normalized;

            // 当前弯曲方向（膝相对 髋→踝轴 的垂向偏移）；退化时跳过（腿伸直，平面不定）。
            Vector3 d = mid.position - hip.position;
            Vector3 perp = d - n * Vector3.Dot(d, n);
            if (perp.sqrMagnitude < 1e-6f) return;
            perp.Normalize();

            // 目标方向：身体前向 + 向外偏（都投影到垂直于腿轴的平面）。
            Vector3 fwd = entityRoot != null ? entityRoot.forward : transform.forward;
            Vector3 right = entityRoot != null ? entityRoot.right : transform.right;
            Vector3 want = fwd + right * (outwardSign * Mathf.Tan(kneeOutwardDeg * Mathf.Deg2Rad));
            Vector3 wantPerp = want - n * Vector3.Dot(want, n);
            if (wantPerp.sqrMagnitude < 1e-6f) return;
            wantPerp.Normalize();

            // 绕腿轴的带符号夹角 → 旋转大腿（轴过髋与踝 ⇒ 踝不动）。
            // 实测：剪辑自带的膝方向相对“正前”偏内约 **25°**（膝的横向偏移只有 ~12mm，
            // 但其**方向**偏离正前 25°，这正是可见的内八）。故限幅必须明显大于该值，
            // 否则会饱和、kneeOutwardDeg 失去作用（旧限幅 25° 正好卡在边界，实测 0~30° 无差异）。
            float ang = Vector3.SignedAngle(perp, wantPerp, n);
            float target = Mathf.Clamp(ang, -50f, 50f);
            applied = Mathf.MoveTowards(applied, target, kneeOutwardSmooth * 30f * dt);
            if (Mathf.Abs(applied) < 0.001f) return;
            hip.rotation = Quaternion.AngleAxis(applied, n) * hip.rotation;
        }

        private float kneeOutFixL, kneeOutFixR;

        // ---------------------------------------------------------------
        // 蹲走腿部（程序化两骨 IK）
        // ---------------------------------------------------------------

        /// <summary>
        /// 蹲走时替代 TwoBoneIKConstraint 解算腿部。
        ///
        /// 为什么必须程序化（实测）：约束的 IK 目标 XZ 取自 `tip.position`，而 tip 就是
        /// 约束**上一帧的输出**。权重=1 时“目标XZ = 上一帧落点”构成不动点 —— 剪辑把脚
        /// 送到哪都没用，脚被钉住：蹲走步幅 0.767→0.031m、大腿摆动 54°→6°、小腿 75°→5°。
        /// 站姿只有 0.15 权重才侥幸保留步幅（85% 由剪辑驱动）。
        ///
        /// 本函数在 LateUpdate 运行（动画相位之后、约束已按降权后的权重解算完），
        /// 此时把脚约束权重压到 ~0 → **读到的是干净的剪辑姿态**，于是：
        ///   - 目标 XZ = 剪辑踝位 XZ            → 步幅与大腿/小腿前后摆动完全保留（不钉腿）
        ///   - 目标 Y   = 地面 + 踝高 + 抬脚量  → 不穿地；抬脚量取“剪辑踝相对本周期最低点的
        ///                抬升”，把剪辑自带的抬脚抬回地面以上（蹲下时剪辑脚整体在地面之下）
        /// 触地/抬脚因此天然分离：触地相抬脚量≈0 → 脚钉在地面；抬脚相抬脚量>0 → 脚离地。
        /// </summary>
        private void ApplyCrouchWalkLegs()
        {
            float w = crouchLegProcCur;
            if (w <= 0.001f)
            {
                minCrouchAnkleL = minCrouchAnkleR = float.MaxValue;
                return;
            }
            SolveCrouchFoot(footIKL, footTargetL, footTipL, ref minCrouchAnkleL, w);
            SolveCrouchFoot(footIKR, footTargetR, footTipR, ref minCrouchAnkleR, w);
        }

        private void SolveCrouchFoot(TwoBoneIKConstraint ik, Transform target, Transform tip,
            ref float minAnkleRel, float w)
        {
            if (ik == null || target == null || tip == null) return;
            Transform hip = ik.data.root, mid = ik.data.mid;
            if (hip == null || mid == null) return;

            // 干净剪辑踝位（约束权重已被压到 ~0）。
            Vector3 ankle = tip.position;
            float entityY = entityRoot != null ? entityRoot.position.y : transform.position.y;

            // 本周期最低踝高基线（自适应坡面），抬脚量 = 相对该基线的抬升。
            float rel = ankle.y - entityY;
            if (rel < minAnkleRel) minAnkleRel = rel;
            else minAnkleRel = Mathf.MoveTowards(minAnkleRel, rel, Time.deltaTime * 0.05f);

            // 地面：从实体高度上方垂直下探（剪辑踝此时可能在地面以下，不能从踝起射）。
            Vector3 probe = new Vector3(ankle.x, entityY + 0.6f, ankle.z);
            if (!ProbeGround(probe, 2f, out var hit) || hit.collider == null) return;

            float lift = Mathf.Clamp((rel - minAnkleRel) * crouchLiftGain, 0f, crouchLiftMax);
            float baseH = Mathf.Lerp(footHeight, footHeightProne, proneCur);
            target.position = new Vector3(ankle.x, hit.point.y + baseH + lift, ankle.z);

            // 弯曲平面：用**身体前向的水平分量**当 pole —— 膝必须朝前弯，这是一个固定、
            // 不随脚位变化的选择，解算出的膝永远落在矢状面前侧同一分支上。
            // **不要**用剪辑膝或静态 Hint 点：蹲姿下它们可能与“胯->踝”轴近共线，
            // pole 退化会使膝瞬间翻到另一侧（实测小腿摆幅 234°，物理不可能）。
            Vector3 poleDir = entityRoot != null ? entityRoot.forward : transform.forward;
            poleDir.y = 0f;
            if (poleDir.sqrMagnitude < 1e-6f) poleDir = Vector3.forward;
            SolveLimb(hip, mid, tip, target, poleDir, w);

            // 脚掌朝向**不动** —— 沿用剪辑姿态。
            // 烘焙时 FootIK 的 rotationWeight=0（见 SoldierRigBaker），即设计上脚 IK 只管
            // 位置、朝向交给动画；SolveLimb 结束时也已把 tip.rotation 还原为本帧动画值。
            // 曾在此**直接写 tip.rotation = LookRotation(fwd, groundNormal)**：那是往真实
            // 骨骼上写，而 DEF-foot 的局部轴不是"趾尖朝 +Z"（Blender 导出、rest 带 270° 翻转），
            // 结果脚底被翻转到朝上（用户实测截图）。目标 Transform 的 rotation 在
            // rotationWeight=0 时是惰性的，所以别处那样写没事，这里写 tip 就有事。
        }

        // ---------------------------------------------------------------
        // WEAPON POSE (PHASE14 枪械专项：腰射 / 瞄准 / 冲刺 三态)
        // ---------------------------------------------------------------

        /// <summary>
        /// 三态切换（约束源 + 基准位姿）。
        ///
        /// **结构约定（用户设计）**：
        ///   Driver_AimTarget   恒跟随**相机指向**（每帧无条件写入，任何状态都不改写）
        ///   Driver_GunAimTarget 枪械瞄准目标：腰射=相机指向；冲刺=SprintAim 方向
        /// 于是等价于“腰射：枪由 Driver_AimTarget 约束；冲刺：枪由 SprintAim 约束”，
        /// 而 Driver_AimTarget 始终是纯相机语义 → 头部俯仰跟随不会因收枪而跑偏。
        ///
        ///  - 腰射：枪基准 AimConstraint 启用（源=gunAimTarget≈相机点）；SprintAim 约束停用。
        ///  - 瞄准：基准停用；直解基准位姿使 RearAim 与 SightAim 同时落在相机射线上
        ///    （瞳距沿枪管轴后移 adsEyeRelief，可调）；按 aimAmount 从腰射过渡。
        ///  - 冲刺：基准仍启用，但源点沿 SprintAim 方向（见 BlendedAimRotation 的 Slerp）。
        /// 后座作用在枪根（不动基准 → 不影响手臂 IK 目标以外的任何动作）。
        ///
        /// **腰射↔冲刺的朝向混合不能交给约束的多源加权**：约束是把各源的 (源−自身)
        /// 向量**加权相加**，而腰射(相机前视)与冲刺(指向 SprintAim)的枪管方向实测相差
        /// 125° —— 两个大角度向量相加时方向在权重中段急剧扫过，实测一帧转 82°、枪位跳 0.5m。
        /// 故约束只挂**单源**(gunAimTarget)，混合好的朝向由脚本按 Slerp 编码进该源的位置。
        /// 脚本只负责**位置**(腰射绕肩柱面 / 冲刺左移后撤)与 ADS 直解。
        /// </summary>
        private void UpdateWeaponPose(in SoldierFrameState s, float dt)
        {
            if (weaponBasis == null || boundGun == null) return;

            bool alive = !s.dead && s.weaponHeld;
            adsSmooth = Mathf.MoveTowards(adsSmooth, alive ? Mathf.Clamp01(s.aimAmount) : 0f, aimBlendSpeed * dt);
            // 腰射↔冲刺过渡量：位置/瞄准权重/摆枪幅度共用同一进度 → 两态之间平滑插值。
            sprintBlend = Mathf.MoveTowards(sprintBlend, (alive && s.sprinting) ? 1f : 0f, sprintBlendSpeed * dt);

            // ---- 冲刺摆枪：两路叠加 ----
            // (a) **平移**：SprintAim 节点沿局部 X 平移。AimConstraint 瞄的是源的**位置**，
            //     所以只有平移能改变约束输出（武器随之左右摆）。
            //     幅度按 sprintBlend 淡入淡出 → 过渡期间摆幅渐增/渐消。
            // (b) **滚转**：绕枪自身枪管轴滚（枪顶左右倾）。约束的 worldUpType=SceneUp 把
            //     枪的滚转**钉在世界朝上**，故改 SprintAim 的朝向（yaw）对枪零影响 ——
            //     旧的“转 SprintAim 当摆幅”是死代码（实测 AMPL=20 时枪转动 0.000°）。
            //     滚转改为直接写在枪的局部旋转上（见下，与后座同一处施加）。
            if (alive && sprintBlend > 0.001f)
            {
                sprintSwayPhase += dt * sprintSwayFrequency * Mathf.PI * 2f;
                float sway = Mathf.Sin(sprintSwayPhase) * sprintSwayLateral * sprintBlend;
                sprintAimSource.localPosition = sprintAimBasePos + Vector3.right * sway;
                sprintAimSource.localRotation = sprintAimBaseRot;   // 朝向不参与（见 (b)）
            }
            else
            {
                sprintSwayPhase = 0f;
                sprintAimSource.localPosition = sprintAimBasePos;
                sprintAimSource.localRotation = sprintAimBaseRot;
            }
            // 滚转相位与平移同源（同一 sin），故“左右平移 + 枪顶侧倾”是同一个摆动动作。
            float swayRoll = (alive && sprintBlend > 0.001f)
                ? Mathf.Sin(sprintSwayPhase) * sprintSwayAmplitude * sprintBlend
                : 0f;

            // ---- 后座旋转（**先于基准求解**应用）----
            // ADS 基准解算会整体抵消枪的当前局部旋转（含后座增量），因此瞄具连线
            // （RearAim/SightAim）在震动中仍恒落在相机射线上（后座只表现为相机上抬）。
            Quaternion recoilRot = s.recoil > 0.0001f
                ? Quaternion.Euler(-s.recoil * recoilPitchPerUnit, s.recoil * recoilYawPerUnit, 0f)
                : Quaternion.identity;
            // 冲刺滚转绕**枪管轴**（枪局部空间的 gunAimLocalAxis）施加 → 枪顶左右倾。
            Quaternion rollRot = Mathf.Abs(swayRoll) > 0.0001f
                ? Quaternion.AngleAxis(swayRoll, gunAimLocalAxis)
                : Quaternion.identity;
            if (boundGun.transform.parent == weaponBasis)
                boundGun.transform.localRotation = gunAlignRot * recoilRot * rollRot;

            // ---- 约束启停 ----
            // 腰射↔冲刺的**朝向混合不能交给约束的多源加权**：约束是把各源的 (源−自身)
            // 向量**加权相加**，而腰射(相机前视)与冲刺(指向 SprintAim)的枪管方向实测相差
            // **125°** —— 两个大角度向量相加时方向在权重中段急剧扫过，实测一帧转 82°、
            // 枪位跳 0.5m。任何线性权重都救不了这种大角度反向，必须沿旋转路径走（Slerp）。
            //
            // 做法：约束只挂**一个**源(gunAimTarget，权重恒 1)，把混合好的朝向编码成
            // 该源的位置 → 约束仍然负责“瞄准 + SceneUp 滚转”，但混合路径由脚本控制。
            bool adsEngaged = alive && adsSmooth > 0.001f;
            if (basisAim != null)
            {
                basisAim.constraintActive = alive && !adsEngaged;
                if (basisAim.constraintActive) ApplyAimWeights(1f, 0f);   // 单源:只用 gunAimTarget
            }
            // 枪自身约束不再使用（统一由基准约束承担,避免双解算器互写）。
            if (gunSprintAim != null) gunSprintAim.constraintActive = false;

            // ---- 基准位置（腰射=枪托落在右肩柱面；冲刺=同柱面+左移/抬高/后撤；ADS=向射线解过渡）----
            if (driverAimSource != null)
            {
                Transform eye = driverAimSource;
                Vector3 pos;
                if (gunStock != null && gunCylinder != null)
                {
                    // 枪托目标 = 柱面点(与相机同式:柱心 + 视线方向×半径,俯仰随相机)
                    //           + 冲刺的眼空间偏移:横向(左移) / 高度(抬高) / 后撤。
                    //          按 sprintBlend 淡入淡出。
                    // 偏移必须加在**枪托目标**上,不能加在“腰射基准位”上:基准位是相对
                    // 当前状态的量,拿它再叠加偏移会重复计入当前状态(实测 −0.40m,应为 −0.07m)。
                    Vector3 aimDir = eye.forward;
                    if (aimDir.sqrMagnitude < 1e-6f) aimDir = Vector3.forward;
                    aimDir.Normalize();
                    // 冲刺**不跟随相机俯仰**(收枪抱在身前,视线上下扫时枪不跟着点头)：
                    // 绕柱面滑动的方向把俯仰分量按 sprintBlend 淡出 → 冲刺时枪托停在固定高度。
                    // 腰射(sprintBlend=0)保持全俯仰跟随(此前已确认需求:枪俯仰=相机俯仰)。
                    Vector3 cylAim = PitchLockedAim(aimDir, sprintBlend);
                    // 冲刺偏移也必须用**去俯仰**的基（见下）：若用 eye.forward/eye.up，
                    // 相机俯仰会改变枪托沿枪管轴/竖直方向的位置 → BlendedAimRotation 里
                    // (SprintAim − 枪位) 的方向随之摆动 → 枪的**水平**朝向被俯仰带偏。
                    // 实测枪 yaw 相对相机有 ±3.3° 起伏、与相机俯仰同相（"水平跟随不同步"真因）。
                    // 横向用 eye.right：yaw+pitch 分解下 right 恒在水平面内（pitch 绕 right 转），
                    // 本就与俯仰无关且不会退化；高度用世界 up；后撤用水平前向。
                    Vector3 flatFwd = new Vector3(aimDir.x, 0f, aimDir.z);
                    if (flatFwd.sqrMagnitude < 1e-6f)
                        flatFwd = entityRoot != null ? entityRoot.forward : Vector3.forward;
                    flatFwd.y = 0f;
                    if (flatFwd.sqrMagnitude < 1e-6f) flatFwd = Vector3.forward;
                    flatFwd.Normalize();
                    float radius = hipOrbitRadius > 0f ? hipOrbitRadius : CylinderWorldRadius(gunCylinder);
                    Vector3 stockTarget = gunCylinder.position + cylAim * radius
                        + eye.right * (sprintLateralOffset * sprintBlend)
                        + Vector3.up * (sprintHeightOffset * sprintBlend)
                        - flatFwd * (sprintDropback * sprintBlend);
                    pos = SolveStockOnCylinder(stockTarget);
                }
                else
                {
                    pos = eye.position
                        + eye.right * hipHoldOffset.x
                        + eye.up * hipHoldOffset.y
                        + eye.forward * hipHoldOffset.z;
                }

                if (adsEngaged && SolveAdsRail(s, out Vector3 railPos, out Quaternion railRot))
                {
                    // ADS：位置与朝向都脚本直解(向射线解插值，含从腰射/冲刺过渡)。
                    pos = Vector3.Lerp(pos, railPos, adsSmooth);
                    weaponBasis.position = pos;
                    weaponBasis.rotation = railRot;
                    _barrelWorldDir = (s.eyeRot * Vector3.forward).normalized;
                }
                else
                {
                    // 腰射/冲刺：位置脚本给，**朝向交给约束**（源=gunAimTarget）。
                    weaponBasis.position = pos;
                    // 把本帧混合后的朝向编码进 **Driver_GunAimTarget** 的位置 ——
                    // 约束据此解出与 BlendedAimRotation(pos) 一致的朝向。
                    // 注意写的是 gunAimTarget（枪专用），**不是** driverAimTarget：
                    // 后者必须保持纯相机语义，否则冲刺收枪会把头部一起带偏（实测偏 94°）。
                    Quaternion blended = BlendedAimRotation(pos);
                    if (gunAimTarget != null && basisAim != null && basisAim.constraintActive)
                        gunAimTarget.position = pos + (blended * Vector3.forward) * aimTargetDistance;
                    _barrelWorldDir = (blended * Vector3.forward).normalized;
                }
            }

            // ---- 后座位移（枪根沿枪管轴后撤）----
            // **必须用基准局部空间的枪管轴**：`boundGun` 是 `weaponBasis` 的子物体，其
            // localPosition 处于**基准局部空间**。此前写入的是 `_barrelWorldDir`（**世界**
            // 方向）—— 两者只在基准旋转与实体根对齐时才偶然一致（实测 yaw=0/180 恰好对，
            // 45°/90°/135° 出现横向分量）：所以症状是"只在某些朝向下枪模横向平移"，且
            // 世界朝向一转，位移方向就跟着错。
            // 实测(yaw 扫掠, recoil=25)：修前 gunLocal 在 yaw=90° 变成 (-0.100,0,0)
            // —— 横向偏 0.100m；修后恒为 (0,0,-0.100)（纯沿枪管后撤），任意朝向一致。
            if (boundGun.transform.parent == weaponBasis)
            {
                if (s.recoil > 0.0001f)
                {
                    Vector3 barrelLocal = weaponBasis.InverseTransformDirection(_barrelWorldDir);
                    if (barrelLocal.sqrMagnitude < 1e-6f) barrelLocal = Vector3.forward;
                    boundGun.transform.localPosition =
                        -barrelLocal.normalized * (s.recoil * recoilKickPerUnit);
                }
                else
                {
                    boundGun.transform.localPosition = Vector3.zero;
                }
            }

            UpdateBolt(s.shotCount, dt);
        }

        private Vector3 _barrelWorldDir = Vector3.forward;

        /// <summary>调试：枪管世界朝向的水平角（度）。</summary>
        public float GunBarrelYawDebug => _barrelWorldDir.sqrMagnitude > 1e-6f
            ? Quaternion.LookRotation(_barrelWorldDir, Vector3.up).eulerAngles.y : 0f;

        /// <summary>调试：枪管世界朝向的俯仰角（度，正=向下）。</summary>
        public float GunBarrelPitchDebug
        {
            get
            {
                if (_barrelWorldDir.sqrMagnitude < 1e-6f) return 0f;
                float p = Quaternion.LookRotation(_barrelWorldDir, Vector3.up).eulerAngles.x;
                return p > 180f ? p - 360f : p;
            }
        }

        /// <summary>
        /// 本帧腰射瞄准目标该放多远（= aimTargetDistance）。
        /// 单源瞄准下无需再匹配 SprintAim 的距离（历史上双源加权时因模长悬殊而需要）。
        /// </summary>
        private float AimTargetDistanceNow()
        {
            return aimTargetDistance;
        }

        /// <summary>
        /// 设置枪基准 AimConstraint 的源。**只用单源**(gunAimTarget，权重 1) ——
        /// 腰射↔冲刺的混合由脚本按 Slerp 做好后编码进该源的位置（见 BlendedAimRotation），
        /// 不用约束的多源加权(大角度反向时那会瞬间翻转)。
        /// 保留第二槽位是为了兼容曾烘进预制体的两源配置，这里把 sprint 权重压成 0。
        /// </summary>
        private void ApplyAimWeights(float wHip, float wSprint)
        {
            if (basisAim == null) return;
            if (_aimSources.Count < 1)
            {
                _aimSources.Clear();
                _aimSources.Add(new ConstraintSource { sourceTransform = gunAimTarget });
            }
            var a = _aimSources[0]; a.sourceTransform = gunAimTarget; a.weight = 1f; _aimSources[0] = a;
            basisAim.SetSources(_aimSources);
        }

        private readonly System.Collections.Generic.List<ConstraintSource> _aimSources =
            new System.Collections.Generic.List<ConstraintSource>(1);

        /// <summary>
        /// 把**枪托(Stock)**送到指定世界目标点（腰射=右肩 GunCylinder 柱面点；冲刺=再加左移/后撤）。
        ///
        /// 相机与枪托同式滑动：
        ///     相机: pos = 球心 + 视线方向 × 半径
        ///     枪托: 目标 = 柱心 + 视线方向 × 半径（**含俯仰** → 枪的俯仰角 = 相机俯仰角）
        /// 于是枪托恒在柱面（前表面=靠腹部一侧）上滑动，几何有界 → 下俯不会把枪托推进后背。
        ///
        /// 求解是**解析**的（不是误差反馈）：枪相对基准是刚体 → Stock 在基准局部空间的
        /// 偏移 offsetLocal 恒定；把基准摆到 `目标 − R·offsetLocal` 即让 Stock 精确落点。
        /// R 复现基准 AimConstraint 的旋转（aimVector=+Z、worldUp=SceneUp、双源加权累加）。
        ///
        /// **不能用误差反馈**（`basis.pos + (目标 − stock.pos)`）：约束在本帧稍后才改写
        /// 基准旋转，反馈读到的 stock.pos 属于上一帧旋转 → 冲刺↔腰射这种大角度切换时
        /// 会先算错、下一帧再弹回，实测单帧跳变 0.49m（可见的瞬移）。
        /// </summary>
        private Vector3 SolveStockOnCylinder(Vector3 stockTargetWorld)
        {
            // Stock 在基准局部空间的偏移（刚体常量）。读在写入之前。
            Vector3 offsetLocal = weaponBasis.InverseTransformPoint(gunStock.position);
            // 方向依赖基准位置、而基准位置正是解 → 迭代几次求不动点（源较远，收敛很快）。
            Vector3 p = weaponBasis.position;
            for (int i = 0; i < 3; i++)
            {
                Quaternion r = BlendedAimRotation(p);
                p = stockTargetWorld - r * offsetLocal;
            }
            return p;
        }

        /// <summary>
        /// 本帧基准的瞄准旋转 = **沿旋转路径**混合腰射与冲刺朝向。
        ///
        ///     hipRot    = LookRotation(相机前视, SceneUp)          —— 腰射:枪管随视线
        ///     sprintRot = LookRotation(SprintAim − 基准位, SceneUp) —— 冲刺:枪管指向收枪位
        ///     return    = Slerp(hipRot, sprintRot, sprintBlend)
        ///
        /// **不能**改成“两个源交给约束加权”：约束把 (源−自身) 向量**加权相加**，而两者
        /// 方向实测相差 125°，相加时方向在权重中段急剧扫过（实测单帧 82°）。Slerp 沿
        /// 最短旋转路径过渡，与两源夹角无关。
        /// 约束最终仍负责解算朝向（脚本只是把结果编码成 gunAimTarget 的位置）。
        /// </summary>
        private Quaternion BlendedAimRotation(Vector3 fromPos)
        {
            Transform eye = driverAimSource;
            Vector3 hipDir = eye != null ? eye.forward : Vector3.forward;
            if (hipDir.sqrMagnitude < 1e-6f) hipDir = Vector3.forward;
            hipDir.Normalize();
            Quaternion hipRot = Quaternion.LookRotation(hipDir, Vector3.up);

            float w = Mathf.Clamp01(sprintBlend);
            if (w <= 0.0001f || sprintAimSource == null) return hipRot;

            // 冲刺朝向**俯仰锁定**：只取水平分量 → 枪管保持水平,不随相机俯仰点头。
            Vector3 sprintDir = sprintAimSource.position - fromPos;
            Vector3 sprintFlat = new Vector3(sprintDir.x, 0f, sprintDir.z);
            if (sprintFlat.sqrMagnitude < 1e-6f) return hipRot;
            Quaternion sprintRot = Quaternion.LookRotation(sprintFlat.normalized, Vector3.up);

            return w >= 0.9999f ? sprintRot : Quaternion.Slerp(hipRot, sprintRot, w);
        }

        /// <summary>按 t 把方向的俯仰分量淡出（t=0 全跟随, t=1 水平）。</summary>
        private static Vector3 PitchLockedAim(Vector3 dir, float t)
        {
            float k = Mathf.Clamp01(t);
            Vector3 flat = new Vector3(dir.x, 0f, dir.z);
            // 视线接近正上/正下时水平分量退化：无法定义“水平朝向”，保持原方向。
            if (flat.sqrMagnitude < 1e-6f) return dir;
            flat.Normalize();
            if (k <= 0.0001f) return dir;
            if (k >= 0.9999f) return flat;
            return Vector3.Slerp(dir, flat, k).normalized;
        }

        /// <summary>柱面世界半径：优先取 GunCylinder 上的 Collider 尺寸，其次用 lossyScale。</summary>
        private static float CylinderWorldRadius(Transform cyl)
        {
            var cap = cyl.GetComponent<Collider>();
            if (cap != null)
            {
                Vector3 s = cap.bounds.size;
                float max = Mathf.Max(s.x, Mathf.Max(s.y, s.z));
                float mid = Mathf.Max(Mathf.Min(s.x, s.y), Mathf.Min(Mathf.Max(s.x, s.y), s.z));
                if (mid > 1e-4f) return mid * 0.5f;
            }
            Vector3 sc = cyl.lossyScale;
            return Mathf.Max(Mathf.Abs(sc.x), Mathf.Abs(sc.z)) * 0.5f;
        }

        /// <summary>
        /// ADS 直解（用户原理：**每帧求解枪械基准的位置/朝向，使 RearAim 与 SightAim
        /// 恒落在相机射线上**）。
        ///  1) 枪的目标世界旋转 gunWorldTarget = LookRotation(射线方向, 相机 up) ∘
        ///     Inverse(LookRotation(枪口轴局部, 瞄具上局部)) —— 枪口轴对射线、瞄具“上”
        ///     对相机 up（滚转消除）。
        ///  2) 基准旋转 = gunWorldTarget ∘ Inverse(枪当前局部旋转)：**抵消枪自身局部
        ///     旋转（含后座震动增量）**，因此枪的世界朝向恒等于 gunWorldTarget ——
        ///     后座只表现为相机上抬，瞄具连线不会脱离射线（实测踩坑：不抵消时后座
        ///     1.6 会让瞄具连线偏离射线 ~2.2°）。
        ///  3) 基准位置 = (眼位 + 射线方向·瞳距) − gunWorldTarget·贴眼锚_local：
        ///     贴眼锚（RearAim）落射线，距眼=瞳距（可调）；锚共线于枪管 → SightAim
        ///     同时上射线。枪根的轴向后座位移沿射线方向，不影响垂直偏离。
        /// </summary>
        private bool SolveAdsRail(in SoldierFrameState s, out Vector3 pos, out Quaternion rot)
        {
            pos = Vector3.zero;
            rot = Quaternion.identity;
            // 贴眼锚：**RearAim**（Rear_Sight 的子对象 = 真瞄准点）。
            // 锚落射线 + 瞄准轴（RearAim→SightAim）对齐射线 → 两点同时上射线。
            // 退化顺序：RearAim → Rear_Sight(3D 件) → SightAim。
            Transform aimAnchor = gunRearAim != null ? gunRearAim
                                : (gunRearSight != null ? gunRearSight : gunSightAim);
            if (aimAnchor == null || boundGun == null) return false;

            Vector3 rayDir = (s.eyeRot * Vector3.forward).normalized;
            Vector3 worldUp = s.eyeRot * Vector3.up;

            // 瞄准轴/上方向：**每帧从当前瞄准点位置重算**（RearAim/SightAim 为瞄准点，
            // 不是瞄具网格；缓存值还会因 FBX 子物体变换漂移产生恒定夹角）。
            Vector3 a = gunAimLocalAxis, u = gunAimLocalUp;
            {
                Transform rearRef = gunRearAim != null ? gunRearAim : gunRearSight;
                Transform frontRef = gunSightAim != null ? gunSightAim : gunFrontSight;
                if (rearRef != null && frontRef != null)
                {
                    Vector3 rPos = boundGun.transform.InverseTransformPoint(rearRef.position);
                    Vector3 fPos = boundGun.transform.InverseTransformPoint(frontRef.position);
                    Vector3 axis = fPos - rPos;
                    if (axis.sqrMagnitude > 1e-8f)
                    {
                        a = axis.normalized;
                        Vector3 up = rPos - a * Vector3.Dot(rPos, a);   // 近眼瞄准点垂直分量 = 瞄具“上”
                        if (up.sqrMagnitude > 1e-8f) u = up.normalized;
                    }
                }
            }

            // 枪的目标世界旋转（枪口轴 → 射线，瞄具上 → 相机上）
            Quaternion gunWorldTarget = Quaternion.LookRotation(rayDir, worldUp)
                * Quaternion.Inverse(Quaternion.LookRotation(a, u));

            // 抵消枪当前局部旋转（含后座），使枪世界朝向恒为 gunWorldTarget
            Quaternion gunLocal = boundGun.transform.localRotation;
            rot = gunWorldTarget * Quaternion.Inverse(gunLocal);

            // 贴眼瞄准点在枪根局部的偏移（嵌套层级用 InverseTransformPoint）
            Vector3 anchorLocal = boundGun.transform.InverseTransformPoint(aimAnchor.position);
            Vector3 anchorOnRay = s.eyePos + rayDir * adsEyeRelief;
            pos = anchorOnRay - gunWorldTarget * anchorLocal;
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
            if (headAim != null) headAim.weight = 0f;
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

        /// <summary>复活：基线重置（朝向对齐、踝高标定、姿态渐变清零）。</summary>
        public void ApplyRevive()
        {
            deadApplied = false;
            footLagInit = false;
            footLagVel = 0f;
            crouchCur = proneCur = 0f;
            minAnkleL = minAnkleR = float.MaxValue;
            footWeightL = footWeightR = 0f;
            adsSmooth = 0f;
            sprintBlend = 0f;
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
