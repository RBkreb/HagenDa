using HagenDa.Animation.Rigging;
using Mirror;
using UnityEngine;
using HagenDa.Networking;

namespace HagenDa.Animation.RigGraph
{
    /// <summary>
    /// Server-authoritative soldier animation driver (PHASE13).
    ///
    /// Animator: "NetworkSoldierLayers" (5 layers)
    ///   L0 Base            — empty dummy layer
    ///   L1 LocomotionFull  — 8-dir blend tree, lower-body mask (weight 1 when NO weapon)
    ///   L2 LocomotionLower — same tree, lower-body mask        (weight 1 when weapon held)
    ///   L3 UpperActions    — Shoot/Reload clips, weight forced 0 (PHASE14: upper body
    ///                        must apply NO original clips; rig drives it entirely)
    ///   L4 Death           — Death1-3, no exits (revive = Rebind)
    /// Rig: "SoldierRigSetup" (baked per model prefab) — 3 rig layers:
    ///   LowerBody (hips yaw lag + body offset + foot ground IK)
    ///   UpperBody (spine aim twist, max 60°, pitch 30% chest / 60% head)
    ///   Hands     (two-bone IK to weapon anchors + HandGrip fingers)
    ///
    /// Everything below is driven from ALREADY-SYNCED state (SyncVars + networked
    /// transform) — no extra RPCs / NetworkAnimator:
    ///  - MoveX/MoveZ floats  ← local planar velocity (server: rb, clients: delta)
    ///  - Shoot/Reload/Death triggers on edges, gated by weapon presence / !dead
    ///  - L0/L1 complementary weights by weapon presence (Q14 fallback)
    ///  - rig.SetAimPose/SetAimTarget ← eye + smoothed pitch + root yaw
    ///  - rig.SetHipsYaw ← damped lower-body yaw (exponential chase; lag beyond
    ///    60° accelerates catch-up instead of snapping; large turns dip foot IK)
    ///  - rig.SetBodyOffset ← crouch / prone / slide body offsets (smoothed)
    ///  - rig weights: foot IK (idle/crouch/prone=1, moving/slide/air=0, turn dip),
    ///    hands yield during Shoot/Reload clips, death → all zero.
    ///
    /// Damage animation intentionally removed (PHASE13 decision): PlayDamage is gone.
    /// Jump/slide have no clips in the pack — slide shows the crouch pose, air keeps
    /// locomotion with feet IK off.
    /// </summary>
    [RequireComponent(typeof(NetworkIdentity))]
    public class NetworkSoldierAnimator : NetworkBehaviour
    {
        [Header("Team models (children of the entity root)")]
        public GameObject redModel;
        public GameObject blueModel;
        [Tooltip("队伍未指定时(teamId<0:单人验证场景/无对局管理器)显示的模型。\n" +
                 "留空则回落红队模型。PHASE14 把 Fatui 设为默认验证模型。")]
        public GameObject defaultModel;

        [Header("Differential turn (lower body lag)")]
        [Tooltip("指数趋近时间常数(秒)——等效 SmoothDamp 手感。")]
        public float turnSmoothTime = 0.18f;
        [Tooltip("滞后超过 60° 后每度每秒的额外追赶角速度(度/秒/度)。")]
        public float turnCatchupBoost = 6f;
        [Tooltip("脚 IK 点滞后同步时间常数(秒)。胯部随身体转,脚 IK 点以该滞后绕身体轴\n" +
                 "公转跟上(PHASE14:下半身=脚 IK 滞后同步)。越大脚越迟跟上。")]
        public float footLagSmoothTime = 0.45f;
        [Tooltip("idle 状态:上半身开始转向后,下半身延迟多久才播放走路动画并追赶(秒)。")]
        public float turnStartDelay = 1.0f;
        [Tooltip("判定“上半身开始转向”的滞后角阈值(度);小于此值视为未转向。")]
        public float turnStartDeadband = 4f;

        [Header("Posture model offset (自姿态采集测量,模型根位移)")]
        public Vector3 crouchModelOffset = new Vector3(0f, -0.5f, 0f);
        public Vector3 proneModelOffset = new Vector3(0f, -0.9f, 0.25f);

        [Header("Aim")]
        public float aimTargetDistance = 30f;
        public float pitchSmoothTime = 0.06f;

        [Header("Jump (procedural — 动画包无跳跃剪辑)")]
        [Tooltip("起跳缓冲下蹲量(m)。")]
        public float jumpCrouchAmount = 0.22f;
        [Tooltip("起跳缓冲动作速率(每秒)。")]
        public float jumpCrouchSpeed = 3.5f;

        private NetworkPlayerController controller;
        private NetworkPlayerHealth health;
        private NetworkGun gun;
        private NetworkCombatant combatant;
        private Rigidbody rb;

        private Animator animator;              // animator of the currently shown model
        private SoldierRigSetup rig;            // baked rig companion of the shown model
        private GameObject activeModel;
        private int activeTeam = int.MinValue;

        private static readonly int MoveX = Animator.StringToHash("MoveX");
        private static readonly int MoveZ = Animator.StringToHash("MoveZ");
        private static readonly int LocoSpeed = Animator.StringToHash("LocoSpeed");
        private const int LayerFull = 1, LayerLower = 2, LayerUpper = 3;   // L0 = 哑基础层(权重不可驱动)
        private const float LocoHoldSpeed = 1.2f;   // 静止时保持的最小混合幅度(>0 即远离 Idle 原点)

        // Diff caches (re-baselined after model switch / rebind).
        private bool cachesValid;
        private bool dead;
        private bool reloading;
        private uint shotCount;
        private uint jumpCountCache;    // 跳跃边沿检测(服务端权威 jumpCount)
        private float jumpAnim;         // 起跳缓冲包络 1→0
        private Vector3 smoothedVelocity;
        private Vector3 lastNetworkPosition;

        // Differential turn state.
        private bool yawInit;
        private float dampedYaw;
        private float yawVel;       // SmoothDampAngle 速度状态(PHASE14)
        private float idleTurnTimer;   // idle 转向延迟计时(PHASE14:1s 后下半身才追赶)
        private float layerLowerCur;   // 下半身移动层权重(PHASE14 idle=T pose)
        private float footLagYaw;      // 脚 IK 点滞后朝向(世界 yaw,度)
        private float footLagVel;      // 脚滞后 SmoothDampAngle 速度状态
        private bool footLagInit;

        // Smoothed procedural values.
        private float pitchSmoothed;
        private float pitchVel;
        private float layerFullCur = 1f;
        private float handsWeightCur = 1f;
        private float spineWeightCur;
        private float poseCrouchCur;
        private float poseProneCur;
        private float heldMoveX;        // 保持的混合位置(静止时冻结在上一次移动值)
        private float heldMoveZ = 1.2f;
        private float footWeightLCur = 1f;
        private float footWeightRCur = 1f;

        private const float DeadZoneSpeed = 0.15f;   // planar speed → idle blend
        private const float MovingSpeed = 0.5f;      // feet IK off when moving
        private const float SpineMaxTwist = 60f;     // 与 SpineAimConstraint.data 一致(仅用于 dip)
        private const float FootIKCapIdle = 1f;      // 静止:完全钉地(站立/转身脚不漂)
        private const float FootIKCapMoving = 0.15f; // 移动:软钉(保留剪辑抬脚自由度)
        // 触地带宽(米,相对各脚观测到的“最低踝高”=落地帧)。自适应标定,模型无关:
        // Fatui 静止踝高≈0.175、Natlan≈0.116,硬编码绝对阈值必错其一。
        private const float FootPlantBand = 0.02f;   // 低于 最低+0.02 = 触地
        private const float FootLiftBand = 0.09f;    // 高于 最低+0.09 = 完全释放
        private float minAnkleL = float.MaxValue;
        private float minAnkleR = float.MaxValue;

        private void Awake()
        {
            controller = GetComponent<NetworkPlayerController>();
            health = GetComponent<NetworkPlayerHealth>();
            gun = GetComponent<NetworkGun>();
            combatant = GetComponent<NetworkCombatant>();
            rb = GetComponent<Rigidbody>();
        }

        /// <summary>当前显示模型的 rig(NetworkGun 用于武器重绑/显隐联动)。</summary>
        public SoldierRigSetup ActiveRig => rig;
        /// <summary>当前显示的队伍模型(MirrorView 镜像补丁依赖)。</summary>
        public GameObject ActiveModel => activeModel;
        public bool IsDead => dead;

        private void Update()
        {
            UpdateTeamModel();

            if (activeModel == null || animator == null || rig == null) return;

            if (!cachesValid)
            {
                ResetDiffCaches();
                cachesValid = true;
            }

            UpdateDeathState();
            if (!cachesValid) return;   // revive rebind — re-baseline next frame

            float dt = Time.deltaTime;
            UpdateLocomotionLayers(dt);
            UpdateMovementParams();
            UpdateActionEdges();
            DriveRig(dt);
        }

        // ---------------------------------------------------------------
        // TEAM MODEL SWITCH
        // ---------------------------------------------------------------

        private void UpdateTeamModel()
        {
            int team = combatant != null ? combatant.teamId : -1;
            if (team == activeTeam && activeModel != null) return;
            activeTeam = team;

            // teamId < 0 = 队伍未指定(单人验证场景无对局管理器)。此时用 defaultModel
            // (PHASE14:Fatui 是唯一带完整 rig 的验证模型;Natlan 缺少 PHASE14 约束)。
            GameObject next;
            if (team < 0)
                next = defaultModel != null ? defaultModel
                     : (redModel != null ? redModel : blueModel);
            else
                next = team == (int)MatchTeam.Blue
                     ? (blueModel != null ? blueModel : redModel)
                     : (redModel != null ? redModel : blueModel);

            if (redModel != null && redModel != next) redModel.SetActive(false);
            if (blueModel != null && blueModel != next) blueModel.SetActive(false);
            if (next != null) next.SetActive(true);

            if (next != activeModel)
            {
                activeModel = next;
                animator = next != null ? next.GetComponentInChildren<Animator>(true) : null;
                rig = next != null ? next.GetComponentInChildren<SoldierRigSetup>(true) : null;
                cachesValid = false;   // the new model's animator starts from scratch
                if (controller != null) controller.eyeAnchor = FindHeadAnchor(next);
            }
            EnsureModelVisible();
        }

        /// <summary>
        /// 找到模型的头部碰撞器(Fatui:头部骨骼 DEF-spine.005 的子对象 headcollider),
        /// 作为相机高度的眼锚。优先取 NetworkHitbox.part==Head 的碰撞器,否则按名字。
        /// </summary>
        private static Transform FindHeadAnchor(GameObject model)
        {
            if (model == null) return null;
            foreach (var hb in model.GetComponentsInChildren<NetworkHitbox>(true))
            {
                if (hb.part == HitboxPart.Head && hb.transform != null) return hb.transform;
            }
            foreach (var t in model.GetComponentsInChildren<Transform>(true))
                if (t.name.ToLowerInvariant().Contains("head")) return t;
            return null;
        }

        /// <summary>
        /// 本地玩家同样显示模型(PHASE14:相机在胶囊体外表面,故能看到自身)。
        /// 队伍模型切换/死亡复活后重新确认渲染器可见,避免被其它系统关掉。
        /// </summary>
        private void EnsureModelVisible()
        {
            if (activeModel == null) return;
            foreach (var r in activeModel.GetComponentsInChildren<Renderer>(true))
                if (!r.enabled) r.enabled = true;
        }

        // ---------------------------------------------------------------
        // DEATH / REVIVE
        // ---------------------------------------------------------------

        private void UpdateDeathState()
        {
            bool isDead = health != null && health.IsDead;
            if (isDead == dead) return;

            dead = isDead;
            if (dead)
            {
                // Death1..3. Any State entry, no exit — the corpse holds the pose.
                animator.SetTrigger("Death" + Random.Range(1, 4));
                rig.SetAllWeights(0f);   // 纯剪辑尸体(rig 全零,Q12.4)
            }
            else
            {
                // Revive / redeploy: death states have no exits; rebind cleanly.
                animator.Rebind();
                cachesValid = false;
                yawInit = false;         // 重生朝向直接对齐,不做滞后追赶
            }
        }

        // ---------------------------------------------------------------
        // LOCOMOTION (floats + L0/L1 layer weights)
        // ---------------------------------------------------------------

        private void UpdateLocomotionLayers(float dt)
        {
            // PHASE14:Idle 改为 T pose(绑定姿态)。静止时两个移动层权重归零,露出
            // L0 Base(绑定姿态剪辑),下半身即 T pose 直腿;上半身仍由 rig(SpineAim +
            // 双手 IK)接管,手保持握枪。移动时才播走路剪辑。原地转向由脚 IK 滞后驱动。
            bool moving = smoothedVelocity.SetY0().magnitude > MovingSpeed;
            bool idlePose = !moving;

            bool weaponHeld = WeaponHeld;
            float fullTarget = idlePose ? 0f : (weaponHeld ? 0f : 1f);
            float lowerTarget = idlePose ? 0f : (weaponHeld ? 1f : 0f);
            layerFullCur = Mathf.MoveTowards(layerFullCur, fullTarget, dt * 5f);
            layerLowerCur = Mathf.MoveTowards(layerLowerCur, lowerTarget, dt * 5f);
            animator.SetLayerWeight(LayerFull, layerFullCur);
            animator.SetLayerWeight(LayerLower, layerLowerCur);

            // PHASE14:上半身**完全不应用**原始动画剪辑。UpperActions 层(Shoot/Reload)
            // 恒零权重 —— 上半身姿态全部由 rig(SpineAim + 双手 IK)接管;死亡层不受影响。
            animator.SetLayerWeight(LayerUpper, 0f);
        }

        private void UpdateMovementParams()
        {
            Vector3 v = dead ? Vector3.zero : smoothedVelocity;
            v.y = 0f;
            if (v.magnitude < DeadZoneSpeed) v = Vector3.zero;
            Vector3 local = transform.InverseTransformDirection(v);

            // PHASE14:无 Idle 剪辑。真正静止(idle)时移动层权重归零露出 T pose
            // (见 UpdateLocomotionLayers),这里只需停帧。原地转向由"胯部随身体 +
            // 脚 IK 点滞后公转"实现(SetFootLagYaw),不走走路剪辑。
            // 原地转向不再播放走路剪辑:下半身由"胯部随身体 + 脚 IK 滞后公转"驱动
            // (见 SetHipsYaw/SetFootLagYaw),避免与 T-pose 打架。仅真实移动才走剪辑。
            bool moving = local.sqrMagnitude > 0.01f;
            if (moving)
            {
                heldMoveX = local.x;
                heldMoveZ = local.z;
                // 低于走路下限时抬到 LocoHoldSpeed,避免混合位置落在原点(Idle 方向)
                Vector2 hv = new Vector2(heldMoveX, heldMoveZ);
                if (hv.magnitude < LocoHoldSpeed) hv = hv.normalized * LocoHoldSpeed;
                if (hv.sqrMagnitude < 1e-4f) hv = new Vector2(0f, LocoHoldSpeed);
                heldMoveX = hv.x; heldMoveZ = hv.y;
            }

            animator.SetFloat(MoveX, heldMoveX);
            animator.SetFloat(MoveZ, heldMoveZ);
            // 静止=停帧;移动或原地转向追赶=解冻播放(转向时脚要真的迈步绕行)
            animator.SetFloat(LocoSpeed, moving ? 1f : 0f);
        }

        // ---------------------------------------------------------------
        // ACTION EDGES (Shoot / Reload)
        // ---------------------------------------------------------------

        /// <summary>
        /// PHASE14:上半身不再播放预制开火/换弹剪辑 —— UpperActions 层恒零权重,
        /// 这里只做状态基线(射击/换弹计数)跟踪,不再触发任何动画。开火与换弹的
        /// 视觉表现由 rig(后座、枪机往复)与手势 IK 承担。
        /// </summary>
        private void UpdateActionEdges()
        {
            if (dead) return;
            reloading = gun != null && gun.reloading;
            if (gun != null) shotCount = gun.shotCount;
        }

        // ---------------------------------------------------------------
        // PROCEDURAL RIG (aim / turn / posture / weights)
        // ---------------------------------------------------------------

        private void DriveRig(float dt)
        {
            bool weaponHeld = WeaponHeld;
            var posture = dead ? PlayerPosture.Prone
                               : (controller != null ? controller.posture : PlayerPosture.Stand);
            bool sliding = !dead && controller != null && controller.sliding;

            // ---- 瞄准输入(相机朝向/俯仰),供下半身转向与武器基准共用 ----
            float targetPitch = controller != null ? controller.pitch : 0f;
            pitchSmoothed = Mathf.SmoothDamp(pitchSmoothed, targetPitch, ref pitchVel, pitchSmoothTime);
            float aimYaw = transform.eulerAngles.y;   // 相机水平朝向 = 胶囊 yaw(瞄准基准)

            // ---- 下半身差速转身(SmoothDampAngle 追赶 + 超限加速 + 空中立即同步) ----
            // 趴下:整个身体平趴,相机水平旋转时全身一起转 -> 无滞后。
            // 空中:上下半身立即同步水平旋转(PHASE14 转身专项)。
            bool prone = dead || posture == PlayerPosture.Prone;
            bool airborneNow = Mathf.Abs(smoothedVelocity.y) > 0.6f;
            if (!yawInit) { dampedYaw = aimYaw; yawInit = true; yawVel = 0f; }

            bool movingNow = smoothedVelocity.SetY0().magnitude > MovingSpeed;
            if (prone || airborneNow)
            {
                // 趴下/空中:上下半身立即同步水平旋转
                dampedYaw = aimYaw;
                yawVel = 0f;
                idleTurnTimer = 0f;
            }
            else
            {
                float smoothTime = Mathf.Max(0.01f, turnSmoothTime);
                float lagBefore = Mathf.Abs(Mathf.DeltaAngle(dampedYaw, aimYaw));
                if (movingNow)
                {
                    // 移动:下半身持续 SmoothDampAngle 追赶(无延迟)
                    idleTurnTimer = 0f;
                    dampedYaw = Mathf.SmoothDampAngle(dampedYaw, aimYaw, ref yawVel, smoothTime, Mathf.Infinity, dt);
                }
                else if (lagBefore <= turnStartDeadband)
                {
                    // 静止且未转向:下半身保持当前朝向
                    idleTurnTimer = 0f; yawVel = 0f;
                }
                else
                {
                    idleTurnTimer += dt;
                    // 脊柱反扭超过 60° 上限 → 立即开始追赶;否则等满 turnStartDelay。
                    bool overTwist = lagBefore > SpineMaxTwist;
                    if (overTwist || idleTurnTimer >= turnStartDelay)
                        dampedYaw = Mathf.SmoothDampAngle(dampedYaw, aimYaw, ref yawVel, smoothTime, Mathf.Infinity, dt);
                    else
                        yawVel = 0f;   // 延迟期内保持胯部,反扭由脊柱承担(≤60°)
                }
                // 仍超反扭上限(移动中/延迟追赶起点)→ 额外加速,避免上半身长时间超扭
                float lagNow = Mathf.Abs(Mathf.DeltaAngle(dampedYaw, aimYaw));
                if (lagNow > SpineMaxTwist)
                    dampedYaw = Mathf.MoveTowardsAngle(dampedYaw, aimYaw,
                        (lagNow - SpineMaxTwist) * turnCatchupBoost * dt);
            }
            // 胯部随身体(dampedYaw);脚 IK 点另用一个更慢的滞后朝向绕身体轴公转,
            // 于是转身时脚滞后"挪步"而不是立即跟着转(PHASE14)。
            rig.SetHipsYaw(dampedYaw);
            if (prone || airborneNow || dead)
            {
                footLagYaw = aimYaw; footLagVel = 0f; footLagInit = true;
            }
            else if (!footLagInit)
            {
                footLagYaw = dampedYaw; footLagInit = true;
            }
            else
            {
                footLagYaw = Mathf.SmoothDampAngle(footLagYaw, aimYaw, ref footLagVel,
                    Mathf.Max(0.01f, footLagSmoothTime), Mathf.Infinity, dt);
            }
            rig.SetFootLagYaw(footLagYaw, transform.position);

            // ---- 蹲/趴:bodyposition 下移 = MultiPositionConstraint 约束 hips 到 Driver_Offset ----
            // Humanoid 系统里直接 SetPosition 骨骼会被 muscle/root 重计算覆盖(反系统设计),
            // 故 hips 位置由空对象 Driver_Offset + MultiPositionConstraint 实现;脚本只改
            // Driver_Offset 位置并开约束权重。脚 IK 把脚钉回地面 → 腿自然弯曲。
            // SkeletonPoseConstraint 采集的蹲/趴姿态以完整权重应用。
            float crouchTargetW = !dead && (posture == PlayerPosture.Crouch || sliding) ? 1f : 0f;
            float proneTargetW = dead || posture == PlayerPosture.Prone ? 1f : 0f;
            poseCrouchCur = Mathf.MoveTowards(poseCrouchCur, crouchTargetW, dt * 5f);
            poseProneCur = Mathf.MoveTowards(poseProneCur, proneTargetW, dt * 5f);
            rig.SetPoseWeights(poseCrouchCur, poseProneCur);

            // 跳跃:动画包里没有跳跃剪辑(只有走/跑/Idle)。用一次下蹲-回弹的缓冲动作 +
            // 滞空姿态代替:jumpCount 边沿触发一个 0..1 包络,叠加到 bodyposition 下移上。
            if (jumpCountCache != controller.jumpCount)
            {
                jumpCountCache = controller.jumpCount;   // 服务端权威位置同步
                jumpAnim = 1f;
            }
            jumpAnim = Mathf.MoveTowards(jumpAnim, 0f, dt * jumpCrouchSpeed);
            float jumpDip = Mathf.Sin(jumpAnim * Mathf.PI) * jumpCrouchAmount;   // 0→峰→0

            // bodyposition 下移量喂给 Driver_Offset;约束权重 = 下蹲程度(蹲/趴/跳任一)。
            rig.SetBodyOffset(crouchModelOffset * poseCrouchCur
                            + proneModelOffset * poseProneCur
                            + Vector3.down * jumpDip);

            // ---- 瞄准源/武器状态:必须在模型根旋转+位移**之后**写入 ----
            // AimSource 是胶囊的子物体,会随模型根旋转/位移一起动;若在根变换前写世界
            // 位姿,根一转/一移就把它带偏(枪会跳)。故这里最后写相机世界位姿,保证
            // AimSource 每帧都精确落在相机处(枪与相机射线因此始终一致)。
            float eyeH = EyeHeight(sliding ? PlayerPosture.Crouch : posture);
            float eyeBoost = controller != null ? controller.cameraHeightBoost : 0f;
            if (controller != null && (sliding || posture == PlayerPosture.Crouch))
                eyeBoost += controller.crouchCameraBoost;
            Vector3 eyeOrigin = transform.position + Vector3.up * (eyeH + eyeBoost);
            Quaternion aimDir = Quaternion.Euler(pitchSmoothed, aimYaw, 0f);
            var aimCam = controller != null ? controller.playerCamera : null;
            Vector3 aimOrigin = aimCam != null ? aimCam.transform.position : eyeOrigin;
            Quaternion aimOrient = aimCam != null ? aimCam.transform.rotation : aimDir;
            rig.SetAimPose(aimOrigin, aimOrient);
            rig.SetAimTarget(aimOrigin + aimOrient * Vector3.forward * aimTargetDistance);
            rig.SetWeaponAimEye(aimOrigin);
            float aimAmount = dead ? 0f : (gun != null ? gun.aimAmount : 0f);
            if (gun != null)
                rig.SetWeaponState(aimAmount, gun.sprinting, gun.recoil, gun.shotCount);

            // ---- 权重 ----
            // 有姿态层(Natlan Rig_Pose):趴下时姿态层全权接管,髋部转向让位。
            // 无姿态层(Fatui):趴只用 body offset,髋部转向须恒开,否则趴下无法转身。
            rig.SetHipsWeight(dead ? 0f
                : (rig.HasPoseLayer ? 1f - poseProneCur : 1f));

            float spineTarget = weaponHeld && !dead ? 1f - poseProneCur : 0f;   // 无武器时剪辑接管脊柱
            spineWeightCur = Mathf.MoveTowards(spineWeightCur, spineTarget, dt * 5f);
            rig.SetSpineWeight(spineWeightCur);

            // 脚 IK:逐脚按踝高门控(PHASE14 脚部贴地专项)——剪辑里脚落地帧高度最低,
            // 该脚权重升到上限;抬脚后释放,让剪接自然摆腿。静止/蹲/趴=完全钉地,
            // 移动=软钉(上限<1,保留抬脚自由度);滑铲/滞空/死亡全零;大幅转身压低。
            bool moving = smoothedVelocity.SetY0().magnitude > MovingSpeed;
            bool airborne = Mathf.Abs(smoothedVelocity.y) > 0.6f;
            // 移动程度 -> 权重上限(0=静止满钉, 1=软钉保留抬脚自由度)
            float cap = Mathf.Lerp(FootIKCapIdle, FootIKCapMoving, moving ? 1f : 0f);
            float globalGate = (sliding || airborne || dead) ? 0f : 1f;
            float yRoot = transform.position.y;
            float plantL = FootPlant01(rig.FootTipL, yRoot, dt, ref minAnkleL);
            float plantR = FootPlant01(rig.FootTipR, yRoot, dt, ref minAnkleR);
            // 静止/原地转向时冻结触地脚的世界位置,由 rig 侧按 footYawLag 绕身体轴
            // 公转(滞后挪步);仅真实移动时释放(交给走路剪辑)。
            float freezeGate = moving ? 0f : 1f;
            rig.SetFootPlant(plantL * freezeGate, plantR * freezeGate);
            float targetL = plantL * cap * globalGate;
            float targetR = plantR * cap * globalGate;
            footWeightLCur = Mathf.MoveTowards(footWeightLCur, targetL, dt * 8f);
            footWeightRCur = Mathf.MoveTowards(footWeightRCur, targetR, dt * 8f);
            rig.SetFootIKWeight(footWeightLCur, footWeightRCur);

            // 手部 IK:持枪且存活时恒开(PHASE14:上半身无剪辑,不存在"动作剪辑让位")。
            float handsTarget = (weaponHeld && !dead) ? 1f : 0f;
            handsWeightCur = Mathf.MoveTowards(handsWeightCur, handsTarget, dt * 7f);
            rig.SetHandsWeight(handsWeightCur);
        }

        private bool WeaponHeld => rig != null && rig.BoundWeapon != null && !dead;

        /// <summary>
        /// 单脚地面 IK 门控:以该脚观测到的“最低踝高”(=落地帧)为基准自适应标定,
        /// 低于 最低+plantBand = 完全启用,高于 最低+liftBand = 释放,中间线性过渡;
        /// 权重上限 = cap(移动时 &lt;1,避免锁死抬脚)。基准逐帧缓慢上浮,可跟随坡面,
        /// 又不会被抬脚帧抬高(只降不升的滑动最小值 + 极慢回弹)。
        /// </summary>
        private float FootPlant01(Transform tip, float yRoot, float dt, ref float minAnkle)
        {
            if (tip == null) return 0f;
            float rel = tip.position.y - yRoot;
            if (rel < minAnkle) minAnkle = rel;                          // 迅速记录新的最低点
            else minAnkle = Mathf.MoveTowards(minAnkle, rel, dt * 0.05f); // 极慢回弹,适应坡面
            float plant = minAnkle + FootPlantBand;
            float lift = minAnkle + FootLiftBand;
            float t = (rel - plant) / Mathf.Max(0.01f, lift - plant);
            return Mathf.Clamp01(1f - t);
        }

        private float EyeHeight(PlayerPosture p)
        {
            float h;
            switch (p)
            {
                case PlayerPosture.Crouch: h = controller != null ? controller.crouchHeight : 0.9f; break;
                case PlayerPosture.Prone: h = controller != null ? controller.proneHeight : 0.5f; break;
                default: h = controller != null ? controller.standHeight : 1.8f; break;
            }
            return h - (controller != null ? controller.eyeOffsetFromTop : 0.15f);
        }

        // ---------------------------------------------------------------
        // BASELINE (after model switch / rebind)
        // ---------------------------------------------------------------

        private void ResetDiffCaches()
        {
            dead = health != null && health.IsDead;
            reloading = gun != null && gun.reloading;
            shotCount = gun != null ? gun.shotCount : 0;
            jumpCountCache = controller != null ? controller.jumpCount : 0;
            jumpAnim = 0f;
            smoothedVelocity = Vector3.zero;
            pitchSmoothed = controller != null ? controller.pitch : 0f;
            pitchVel = 0f;
            yawInit = false;

            idleTurnTimer = 0f;
            footLagInit = false;
            footLagVel = 0f;
            layerFullCur = 1f;
            handsWeightCur = WeaponHeld ? 1f : 0f;
            minAnkleL = minAnkleR = float.MaxValue;   // 新模型的踝高基准不同,重新标定
            heldMoveX = 0f;
            heldMoveZ = LocoHoldSpeed;   // 起步即为前向走路混合位(非 Idle 原点)
            animator.SetFloat(MoveX, heldMoveX);
            animator.SetFloat(MoveZ, heldMoveZ);
            animator.SetFloat(LocoSpeed, 0f);   // 静止起始:停帧
        }

        // ---------------------------------------------------------------
        // VELOCITY SOURCE (server: rigidbody; clients: position delta)
        // ---------------------------------------------------------------

        private void LateUpdate()
        {
            Vector3 vel;
            if (isServer && rb != null)
            {
                vel = rb.velocity;
            }
            else
            {
                Vector3 delta = transform.position - lastNetworkPosition;
                float dt = Time.deltaTime;
                vel = dt > 1e-4f ? delta / dt : Vector3.zero;
                if (vel.magnitude > 30f) vel = Vector3.zero;   // teleport (redeploy)
            }
            lastNetworkPosition = transform.position;

            smoothedVelocity = Vector3.Lerp(smoothedVelocity, vel, 0.25f);
        }
    }

    internal static class SoldierAnimatorExt
    {
        public static Vector3 SetY0(this Vector3 v)
        {
            v.y = 0f;
            return v;
        }
    }
}