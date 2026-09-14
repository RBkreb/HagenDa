using HagenDa.Soldier;
using Mirror;
using UnityEngine;
using UnityEngine.InputSystem;

namespace HagenDa.Networking
{
    public enum PlayerPosture
    {
        Stand,
        Crouch,
        Prone
    }

    /// <summary>
    /// Server-authoritative, force-driven FPS controller implementing PHASE2 "3C" logic:
    /// walk / sprint / jump / slide / dive, and stand / crouch / prone postures.
    ///
    /// Authority model (Mirror "Option A"):
    ///  - The owning client only samples input and sends intent up via an unreliable
    ///    [Command]; it never moves its own body.
    ///  - The server applies movement forces + gravity + jump/slide/dive, maintains the
    ///    posture state machine, switches colliders, and performs the shooting hitscan.
    ///  - Position + yaw sync down via NetworkTransformReliable; pitch / posture /
    ///    sliding sync via SyncVars.
    ///  - The local camera is rendered client-side from raw mouse delta (client
    ///    authoritative aim). Its height lerps between postures and shakes on jump /
    ///    slide / dive events.
    ///
    /// Collider model (server-side physics only):
    ///   * stand collider  = capsule 1.8m x 0.5m (radius 0.25m), upright (direction Y)
    ///   * crouch collider = capsule 0.9m x 0.5m, upright; enabled for crouch & slide
    ///   * prone           = stand collider rotated flat (direction Z), centre 0.25m
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    [RequireComponent(typeof(CapsuleCollider))]
    public class NetworkPlayerController : NetworkBehaviour
    {
        [Header("Movement")]
        [Tooltip("Horizontal driving acceleration, applied in the move direction.")]
        public float moveAcceleration = 15f;

        [Tooltip("Velocity-proportional drag while driving (1/s). Drag < drive at the speed cap so the body can accelerate.")]
        public float friction = 1.5f;

        [Tooltip("Velocity-proportional drag when input is released (1/s). Stops the body within ~0.5s.")]
        public float stoppingFriction = 8f;

        [Tooltip("Velocity-proportional drag during a slide (1/s). No drive/clamp while sliding.")]
        public float slideFriction = 1.5f;

        [Tooltip("Gravity (1g = 9.81 m/s^2), applied downward while airborne.")]
        public float gravity = 15f;

        [Header("Speeds")]
        public float standWalkSpeed = 3.5f;
        public float standSprintSpeed = 7.5f;
        public float crouchWalkSpeed = 2f;
        public float crouchSprintSpeed = 4.5f;
        public float proneSpeed = 0.5f;

        [Header("Posture dimensions")]
        public float standHeight = 1.8f;
        public float crouchHeight = 0.9f;
        [Tooltip("Capsule diameter when lying flat (prone).")]
        public float proneHeight = 0.5f;

        [Tooltip("Offset above the capsule top where the overhead clearance ray starts (avoids grazing the body's own collider).")]
        public float clearanceRayOffset = 0.02f;

        [Header("Posture switch delays (camera lerp duration)")]
        public float standCrouchDelay = 0.1f;
        public float standProneDelay = 0.25f;
        public float crouchProneDelay = 0.15f;

        [Header("Jump")]
        public float jumpHeight = 1f;

        [Header("Slide")]
        public float slideSpeed = 12f;
        public float slideTriggerSpeed = 5f;
        public float slideEndSpeed = 4f;
        public float slideCooldown = 2f;

        [Header("Dash (快速机动装置)")]
        public float dashForce = 200f;
        public float dashDuration = 0.2f;

        //[Tooltip("Seconds after landing during which no horizontal drag/clamp is applied, so the slide check (which runs before forces each tick) sees the full landing speed even if the server's input snapshot lags a tick or two.")]
        //public float landingGrace = 0.15f;

        [Header("Dive")]
        public float diveSpeed = 15f;
        public float diveTriggerSpeed = 4f;

        [Header("Look")]
        public float lookSensitivity = 0.05793f;
        public float minPitch = -80f;
        public float maxPitch = 80f;
        [Tooltip("趴姿俯仰限位（度，本项目约定：负=抬头、正=低头）：\n" +
                 "上限 0=不能瞄地；下限 -60=最多抬头 60°。")]
        public float proneMinPitch = -60f;
        public float proneMaxPitch = 0f;
        [Tooltip("趴姿水平旋转最大角速度（度/秒）——趴下时整体随相机缓慢转身（PHASE14）。")]
        public float proneYawRate = 60f;

        /// <summary>当前姿态下的俯仰下限（趴姿：最多抬头 60°，不能瞄地）。</summary>
        private float PitchMin => posture == PlayerPosture.Prone ? proneMinPitch : minPitch;
        /// <summary>当前姿态下的俯仰上限（趴姿：水平=0，不可低头）。</summary>
        private float PitchMax => posture == PlayerPosture.Prone ? proneMaxPitch : maxPitch;

        [Header("Camera")]
        [Tooltip("Distance below the capsule top for the eye/camera.")]
        public float eyeOffsetFromTop = 0.15f;
        [Tooltip("PHASE14:相机置于胶囊体外表面时,沿水平朝向外推的距离(≈胶囊半径 0.25)。0=回到胶囊中轴。")]
        public float cameraSurfaceOffset = 0.25f;
        [Tooltip("PHASE14:相机在眼高基础上的额外抬升(m)。抬高相机同时抬高枪械基准 → " +
                 "枪在视野中不再过低、蹲下时手也够得到。0=纯眼高(1.65m)。\n" +
                 "注意:相机过高(≈1.8m 以上)会让枪械基准超出双臂可及范围(臂长约 0.63m)。")]
        public float cameraHeightBoost = 0.40f;
        [Tooltip("蹲姿相机额外抬升(m,叠加在 cameraHeightBoost 上);蹲下时身体降低," +
                 "抬高相机可让枪保持在手可及范围内。")]
        public float crouchCameraBoost = 0.15f;

        [Header("Camera eye anchor (PHASE14)")]
        [Tooltip("可选:头部锚(Fatui 头部骨骼 DEF-spine.005 的子对象 headcollider)。\n" +
                 "赋值后站立相机高度直接取自该锚的世界高度,自动跟随头部(含蹲/趴低头),\n" +
                 "不再用 standHeight 推算;cameraHeightBoost 只在无锚时生效。\n" +
                 "由 NetworkSoldierAnimator 运行时自动填充。")]
        public Transform eyeAnchor;
        [Tooltip("相对 eyeAnchor 的高度微调(m)。")]
        public float eyeAnchorOffset = 0f;
        [Tooltip("相机跟随锚点高度的平滑速度(消除走路点头抖动)。")]
        public float eyeAnchorSmooth = 14f;
        private float eyeAnchorHeightCur;
        private bool eyeAnchorHeightInit;

        [Header("Camera shake")]
        public float jumpShakeIntensity = 0.2f;
        public float jumpShakeDuration = 0.15f;
        public float slideShakeIntensity = 0.2f;
        public float slideShakeDuration = 0.5f;
        public float diveShakeIntensity = 0.4f;
        public float diveShakeDuration = 0.3f;

        [Header("Combat")]
        public NetworkCombat combat;
        public NetworkGun gun;
        public NetworkEquipment equipment;

        [Header("References")]
        public Camera playerCamera;
        public CapsuleCollider standCollider;
        public CapsuleCollider crouchCollider;
        public GameObject visual; // remote body (hidden for owner)

        // ---- Server-authoritative synced state ----
        [SyncVar] public float pitch;
        [SyncVar] public PlayerPosture posture = PlayerPosture.Stand;
        [SyncVar] public bool sliding;
        [SyncVar] public int activeSlot = -1;   // -1 = 主武器(gun)，0..N-1 = 装备索引
        [SyncVar] public uint jumpCount;        // PHASE12: 跳跃计数（NetworkSoldierAnimator 播 Jump）

        private float yaw;
        private float slideCooldownEnd;
        //private float landingGraceEnd;

        // Server-side posture/movement state.
        private bool sprintActive;   // sticky sprint (exits only when forward is released)
        private float sprintFireHold; // seconds fire held while sprinting (sprint->fire cooldown)
        private bool crouchByHold;   // true when crouch was entered by holding left ctrl
        private bool diving;         // dive in progress (until landing)
        private bool jumpedOrDived;  // was airborne from a jump (for landing shake)

        // Dash state (快速机动装置): continuous horizontal force over dashDuration.
        private bool dashing;
        private float dashRemaining;
        private Vector3 dashDir;

        // Client-side look state (client-authoritative aim).
        private float localYaw;
        private float localPitch;

        // PHASE8 受击反馈：FOV 脉冲（2 渲染帧）。
        private float baseFov = 90f;
        private float fovPulseAmount;
        private int fovPulseFrames;
        private const int fovPulseTotal = 2;

        private Rigidbody rb;
        private bool grounded;
        private bool wasGrounded;
        private NetworkInputState serverInput;        // 本 tick 消费的工作副本
        private NetworkInputState pendingServerInput; // 最近注入的输入（边沿只生效一 tick）
        private SoldierAnimatorDriver soldierAnim;   // PHASE14: 相机球(头部 headcollider)来源

        private bool dead;               // server: 3C + combat disabled, forced prone
        private NetworkPlayerHealth health;
        private PhysicMaterial aliveStandMat;   // cached no-friction material (stand)
        private PhysicMaterial aliveCrouchMat;   // cached no-friction material (crouch)

        /// <summary>ML 训练观测：当前是否接地（服务端）。</summary>
        public bool Grounded => grounded;

        /// <summary>ML 训练观测：粘性冲刺是否激活。</summary>
        public bool Sprinting => sprintActive;

        /// <summary>PHASE14:本地瞄准意图（右键）——拥有者客户端枪械视觉直驱用，不经服务端回环。</summary>
        public bool AimHeld => clientAim;

        /// <summary>ML 训练观测：飞扑进行中（空中）。</summary>
        public bool Diving => diving;

        // Client-side input cache (sampled every rendered frame).
        private Vector2 clientMove;
        private bool clientFire;
        private bool clientAim;
        private bool clientSprint;
        private bool clientCrouchHold;
        private bool jumpRequested;
        private bool crouchToggleRequested;
        private bool proneToggleRequested;
        private bool reloadRequested;
        private bool switchFireModeRequested;
        private bool markRequested;          // q key (PHASE7)
        private int deployChoiceRequested;    // 1/2/3 keys (PHASE7)

        // PHASE8 配装槽位键（edge-triggered）。
        private bool slotPrimaryRequested;
        private bool slotOpt1Requested;
        private bool slotOpt2Requested;
        private bool slotSpecialRequested;
        private bool slotThrowableRequested;

        // Client-side camera transition + shake state.
        private float cameraEyeHeight;
        private float cameraTransitionStart;
        private float cameraTransitionTime;
        private float cameraTransitionDuration;
        private PlayerPosture prevCameraPosture = PlayerPosture.Stand;
        private Vector3 shakeOffset;
        private float shakeIntensity;
        private float shakeDuration;
        private float shakeTime;
        private bool shakeVertical;

        private void Awake()
        {
            rb = GetComponent<Rigidbody>();
            if (standCollider == null)
                standCollider = GetComponent<CapsuleCollider>();
            health = GetComponent<NetworkPlayerHealth>();
            soldierAnim = GetComponent<SoldierAnimatorDriver>();
        }

        public override void OnStartServer()
        {
            rb.useGravity = false;   // gravity applied manually
            rb.freezeRotation = true;
            rb.isKinematic = false;

            // Cache the no-friction materials so SetDead can restore them on revive.
            aliveStandMat = standCollider.sharedMaterial;
            if (crouchCollider != null)
                aliveCrouchMat = crouchCollider.sharedMaterial;

            wasGrounded = grounded;
            ApplyActiveCollider();

            if (isServerOnly && playerCamera != null)
                playerCamera.enabled = false;
        }

        /// <summary>
        /// Called by NetworkPlayerHealth on death/rescue. Death forces prone and
        /// disables all 3C/combat; rescue re-enables them while keeping prone.
        /// </summary>
        public void SetDead(bool value)
        {
            dead = value;
            if (value)
            {
                sliding = false;
                diving = false;
                SetPosture(PlayerPosture.Prone);

                // Kill residual momentum so a corpse doesn't keep sliding on the
                // zero-friction capsule.
                if (rb != null)
                {
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                }

                // Dead bodies use default friction so they stay put when pushed;
                // alive players use zero-friction (all movement friction is manual).
                if (standCollider != null) standCollider.sharedMaterial = null;
                if (crouchCollider != null) crouchCollider.sharedMaterial = null;
            }
            else
            {
                if (standCollider != null) standCollider.sharedMaterial = aliveStandMat;
                if (crouchCollider != null) crouchCollider.sharedMaterial = aliveCrouchMat;
            }
            // On revive the posture stays prone (PHASE4 spec).
        }

        public override void OnStartClient()
        {
            // Cap client render rate at 180 Hz. Requires vsync off.
            Application.targetFrameRate = 180;
            QualitySettings.vSyncCount = 0;

            if (!isServer)
            {
                // Only the server simulates physics; clients are kinematic.
                rb.isKinematic = true;
                rb.useGravity = false;
            }

            if (isLocalPlayer)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
                if (playerCamera != null)
                {
                    playerCamera.enabled = true;
                    baseFov = playerCamera.fieldOfView;
                    // PHASE8: 主相机不渲染实体地图球体（MapIndicator）。
                    // ML-branch: 主视角也不渲染据点/安全区填充盘（MapZone）——
                    // 世界内只显示区域描边（MapZoneOutline，默认可见）。
                    if (MapLayers.Indicator >= 0)
                        playerCamera.cullingMask &= ~(1 << MapLayers.Indicator);
                    if (MapLayers.Zone >= 0)
                        playerCamera.cullingMask &= ~(1 << MapLayers.Zone);
                }
                if (visual != null) visual.SetActive(false);
                cameraEyeHeight = GetEyeHeight(PlayerPosture.Stand);
            }
            else
            {
                if (playerCamera != null) playerCamera.enabled = false;
                if (visual != null) visual.SetActive(true);
            }
        }

        private void Update()
        {
            if (!isLocalPlayer) return;

            // 对局结束后仅保留相机控制，冻结所有输入采样与射击。
            if (NetworkMatchManager.Instance != null && NetworkMatchManager.Instance.matchOver)
            {
                UpdateLocalLook();
                return;
            }

            SampleInput();
            UpdateLocalLook();
        }

        // ---------------------------------------------------------------
        // CLIENT: input sampling
        // ---------------------------------------------------------------
        private void SampleInput()
        {
            var k = Keyboard.current;
            var m = Mouse.current;

            clientMove = ReadMove();
            clientFire = m != null && m.leftButton.isPressed;
            clientAim = m != null && m.rightButton.isPressed;
            clientSprint = k != null && k.leftShiftKey.isPressed;
            clientCrouchHold = k != null && k.leftCtrlKey.isPressed;

            if (k == null) return;

            if (k.spaceKey.wasPressedThisFrame) jumpRequested = true;
            if (k.xKey.wasPressedThisFrame) crouchToggleRequested = true;
            if (k.cKey.wasPressedThisFrame) proneToggleRequested = true;
            if (k.rKey.wasPressedThisFrame) reloadRequested = true;
            if (k.vKey.wasPressedThisFrame) switchFireModeRequested = true;

            // PHASE7: Q 标记敌人；1/2/3 选择重新部署点。
            if (k.qKey.wasPressedThisFrame) markRequested = true;
            if (k.digit1Key.wasPressedThisFrame) deployChoiceRequested = 1;
            else if (k.digit2Key.wasPressedThisFrame) deployChoiceRequested = 2;
            else if (k.digit3Key.wasPressedThisFrame) deployChoiceRequested = 3;

            // PHASE8: 配装槽位键（1 主武器 / 3 可选1 / 4 可选2 / g 特有 / z 投掷物）。
            if (k.digit1Key.wasPressedThisFrame) slotPrimaryRequested = true;
            if (k.digit3Key.wasPressedThisFrame) slotOpt1Requested = true;
            if (k.digit4Key.wasPressedThisFrame) slotOpt2Requested = true;
            if (k.gKey.wasPressedThisFrame) slotSpecialRequested = true;
            if (k.zKey.wasPressedThisFrame) slotThrowableRequested = true;
        }

        private Vector2 ReadMove()
        {
            Vector2 m = Vector2.zero;
            var k = Keyboard.current;
            if (k == null) return m;

            if (k.wKey.isPressed) m.y += 1f;
            if (k.sKey.isPressed) m.y -= 1f;
            if (k.dKey.isPressed) m.x += 1f;
            if (k.aKey.isPressed) m.x -= 1f;
            return Vector2.ClampMagnitude(m, 1f);
        }

        // ---------------------------------------------------------------
        // CLIENT: local camera (aim + height lerp + shake)
        // ---------------------------------------------------------------
        private void UpdateLocalLook()
        {
            var ms = Mouse.current;
            if (ms == null) return;

            Vector2 d = ms.delta.ReadValue();
            d.y = -d.y;
            d *= lookSensitivity;

            // 趴姿：整体随相机缓慢转身（≤proneYawRate°/s，与视觉/瞄准一致）；
            // 其余姿态：直接采纳鼠标 yaw。
            if (posture == PlayerPosture.Prone)
            {
                float maxStep = proneYawRate * Time.deltaTime;
                localYaw += Mathf.Clamp(d.x, -maxStep, maxStep);
            }
            else
            {
                localYaw += d.x;
            }
            localPitch = Mathf.Clamp(localPitch + d.y, PitchMin, PitchMax);

            if (playerCamera == null) return;

            UpdateCameraShake();

            // Apply the server's screen recoil (pitch kick up) as a temporary offset
            // on top of the player's clamped aim pitch. The final rendered pitch is
            // clamped to the fixed view limits so accumulated recoil can never push
            // the camera past minPitch/maxPitch.
            float recoilPitch = gun != null ? gun.recoil : 0f;
            float renderPitch = Mathf.Clamp(localPitch - recoilPitch, PitchMin, PitchMax);
            Quaternion view = Quaternion.Euler(renderPitch, localYaw, 0f);

            // PHASE14 相机:置于头部骨骼子对象 headcollider(球体)表面——不是球心。
            // 位置 = 球心 + 视线方向·半径（表面点），朝向 = 视线方向 = 该点球面法线
            //（相机 forward 恒与球面法线平行，模拟长在头上的眼睛）。跟随头部动画
            //（含蹲/趴低头、走路点头）。无模型/无头部球时回落胶囊体路径。
            if (GetHeadSphere(out Vector3 center, out float radius))
            {
                Vector3 normal = view * Vector3.forward;
                playerCamera.transform.position = center + normal * radius + shakeOffset;
                playerCamera.transform.rotation = view;
            }
            else
            {
                UpdateCameraHeight();

                // 回落路径:置于胶囊体外表面的眼高。水平方向沿相机水平朝向外推一个
                // 胶囊半径(跟随胶囊外表),垂直旋转只转相机自身。
                PlayerPosture camPosture = sliding ? PlayerPosture.Crouch : posture;
                float height;
                if (eyeAnchor != null)
                {
                    // 头部锚路径:相机高度直接取自锚点世界高度,自动跟随头部(含蹲/趴低头)。
                    float anchorH = eyeAnchor.position.y - transform.position.y + eyeAnchorOffset;
                    if (!eyeAnchorHeightInit) { eyeAnchorHeightCur = anchorH; eyeAnchorHeightInit = true; }
                    else eyeAnchorHeightCur = Mathf.Lerp(eyeAnchorHeightCur, anchorH,
                                                         Mathf.Clamp01(Time.deltaTime * eyeAnchorSmooth));
                    height = eyeAnchorHeightCur;
                }
                else
                {
                    float boost = cameraHeightBoost
                                + (camPosture == PlayerPosture.Crouch ? crouchCameraBoost : 0f);
                    height = cameraEyeHeight + boost;
                }
                Vector3 surface = Quaternion.Euler(0f, localYaw, 0f) * Vector3.forward * cameraSurfaceOffset;
                playerCamera.transform.position =
                    transform.position + surface + Vector3.up * height + shakeOffset;
                playerCamera.transform.rotation = view;
            }

            // PHASE8 受击反馈：FOV 增大 2% 并在 2 渲染帧内快速回到正常值。
            if (fovPulseFrames > 0)
            {
                playerCamera.fieldOfView = baseFov + fovPulseAmount * (fovPulseFrames / (float)fovPulseTotal);
                fovPulseFrames--;
                if (fovPulseFrames <= 0) fovPulseAmount = 0f;
            }
            else
            {
                playerCamera.fieldOfView = baseFov;
            }
        }

        /// <summary>
        /// PHASE14:解析头部相机球（headcollider）。优先动画 rig 的 HeadAnchor
        ///（装配时亦写入 eyeAnchor）。世界半径取 SphereCollider.radius×最大缩放。
        /// </summary>
        private bool GetHeadSphere(out Vector3 center, out float radius)
        {
            center = Vector3.zero;
            radius = 0f;
            Transform head = null;
            if (soldierAnim != null && soldierAnim.ActiveRig != null)
                head = soldierAnim.ActiveRig.HeadAnchor;
            if (head == null) head = eyeAnchor;
            if (head == null) return false;

            var sphere = head.GetComponent<SphereCollider>();
            center = head.position;
            radius = sphere != null
                ? sphere.radius * Mathf.Max(Mathf.Abs(head.lossyScale.x),
                    Mathf.Max(Mathf.Abs(head.lossyScale.y), Mathf.Abs(head.lossyScale.z)))
                : 0.08f;
            return true;
        }

        /// <summary>
        /// PHASE14:服务端命中射线起点 = 头部球面沿视线方向的表面点（与本地相机
        /// 同式）。服务端同样运行动画，headcollider 位姿有效；无模型回落胶囊眼高。
        /// </summary>
        private Vector3 ComputeEye(PlayerPosture eff)
        {
            if (GetHeadSphere(out Vector3 center, out float radius))
            {
                Vector3 dir = Quaternion.Euler(pitch, yaw, 0f) * Vector3.forward;
                return center + dir * radius;
            }
            return transform.position + Vector3.up * GetEyeHeight(eff);
        }

        private void UpdateCameraHeight()
        {
            PlayerPosture eff = sliding ? PlayerPosture.Crouch : posture;
            float target = GetEyeHeight(eff);

            if (eff != prevCameraPosture)
            {
                cameraTransitionStart = cameraEyeHeight;
                cameraTransitionDuration = ComputeSwitchDelay(prevCameraPosture, eff);
                cameraTransitionTime = 0f;
                prevCameraPosture = eff;
            }

            if (cameraTransitionTime < cameraTransitionDuration)
            {
                cameraTransitionTime += Time.deltaTime;
                float t = Mathf.Clamp01(cameraTransitionTime / cameraTransitionDuration);
                cameraEyeHeight = Mathf.Lerp(cameraTransitionStart, target, t);
            }
            else
            {
                cameraEyeHeight = target;
            }
        }

        private void UpdateCameraShake()
        {
            if (shakeTime < shakeDuration)
            {
                shakeTime += Time.deltaTime;
                float decay = 1f - Mathf.Clamp01(shakeTime / shakeDuration);

                if (shakeVertical)
                    shakeOffset = Vector3.up * (Random.value * 2f - 1f) * shakeIntensity * decay;
                else
                    shakeOffset = Random.insideUnitSphere * shakeIntensity * decay;
            }
            else
            {
                shakeOffset = Vector3.zero; // camera returns to the correct posture position
            }
        }

        private float GetEyeHeight(PlayerPosture p)
        {
            switch (p)
            {
                case PlayerPosture.Crouch: return crouchHeight - eyeOffsetFromTop;
                case PlayerPosture.Prone: return proneHeight - eyeOffsetFromTop;
                default: return standHeight - eyeOffsetFromTop;
            }
        }

        private float ComputeSwitchDelay(PlayerPosture a, PlayerPosture b)
        {
            if (a == b) return 0.05f;
            if ((a == PlayerPosture.Stand && b == PlayerPosture.Crouch) ||
                (a == PlayerPosture.Crouch && b == PlayerPosture.Stand))
                return standCrouchDelay;
            if ((a == PlayerPosture.Stand && b == PlayerPosture.Prone) ||
                (a == PlayerPosture.Prone && b == PlayerPosture.Stand))
                return standProneDelay;
            return crouchProneDelay; // Crouch <-> Prone
        }

        // ---------------------------------------------------------------
        // CLIENT -> SERVER (input uplink)
        // ---------------------------------------------------------------
        private void FixedUpdate()
        {
            // 外部输入接管(自动化/AI 验证):跳过本地键鼠采样,直接采用 SetServerInput
            // 写入的 intent。与 AI 实体走的是同一条 server-input 通路。
            if (isLocalPlayer && !externalInputOverride) SendInput();
            if (isServer) SimulateServer();
        }

        /// <summary>true 时忽略本地键鼠,改用 SetServerInput 注入的输入(自动化验证/AI)。</summary>
        [System.NonSerialized] public bool externalInputOverride;

        private void SendInput()
        {
            NetworkInputState s = default;
            s.move = clientMove;
            s.yaw = localYaw;
            s.pitch = localPitch;

            s.jump = jumpRequested; jumpRequested = false;
            s.sprint = clientSprint;
            s.crouchToggle = crouchToggleRequested; crouchToggleRequested = false;
            s.proneToggle = proneToggleRequested; proneToggleRequested = false;
            s.crouchHold = clientCrouchHold;
            s.fire = clientFire;
            s.aim = clientAim;
            s.reload = reloadRequested; reloadRequested = false;
            s.switchFireMode = switchFireModeRequested; switchFireModeRequested = false;
            s.mark = markRequested; markRequested = false;
            s.deployChoice = deployChoiceRequested; deployChoiceRequested = 0;

            // PHASE8 配装槽位键。
            s.slotPrimary = slotPrimaryRequested; slotPrimaryRequested = false;
            s.slotOpt1 = slotOpt1Requested; slotOpt1Requested = false;
            s.slotOpt2 = slotOpt2Requested; slotOpt2Requested = false;
            s.slotSpecial = slotSpecialRequested; slotSpecialRequested = false;
            s.slotThrowable = slotThrowableRequested; slotThrowableRequested = false;

            CmdInput(s);
        }

        [Command(channel = Channels.Unreliable)]
        private void CmdInput(NetworkInputState s)
        {
            pendingServerInput = s;
        }

        /// <summary>
        /// Server-side input injection (AI / ML policy). Identical semantics to
        /// CmdInput: the struct is stored and consumed by the next SimulateServer.
        /// </summary>
        [Server]
        public void SetServerInput(NetworkInputState s)
        {
            pendingServerInput = s;
        }

        // ---------------------------------------------------------------
        // SERVER SIMULATION (authoritative)
        // ---------------------------------------------------------------
        private void SimulateServer()
        {
            // 边沿自动清除契约（AGENTS.md: Edge actions auto-clear）：取走本次 tick
            // 输入后清空 pending —— jump/crouchToggle 等 edge 字段只生效一 tick，
            // 注入方（AI/自动化/真人 SendInput）每 tick 重推全量，持有字段不受影响。
            serverInput = pendingServerInput;
            pendingServerInput = default;

            // 对局结束：冻结战斗（不再处理输入/移动/开火），仅让空中身体落地。
            if (NetworkMatchManager.Instance != null && NetworkMatchManager.Instance.matchOver)
            {
                if (!grounded)
                    rb.AddForce(Vector3.down * gravity, ForceMode.Acceleration);
                return;
            }

            // PHASE10 开局门控：指挥官部署期间玩家与 AI 身体一并冻结（Q16: 全体冻结）。
            // 注意：全场为零摩擦材质（PHASE2），必须显式刹停水平速度，
            // 否则出生落体的微小分离速度会让实体无摩擦滑行到四角（实测踩坑）。
            if (NetworkCommanderState.GateActive)
            {
                Vector3 ghVel = new Vector3(rb.velocity.x, 0f, rb.velocity.z);
                if (ghVel.sqrMagnitude > 0.0001f)
                    rb.AddForce(-ghVel * stoppingFriction, ForceMode.Acceleration);
                if (!grounded)
                    rb.AddForce(Vector3.down * gravity, ForceMode.Acceleration);
                return;
            }

            // PHASE8: 首次部署前（观战/大厅）冻结 3C 与战斗，仅让身体落地。
            if (health != null && health.awaitingInitialDeploy)
            {
                if (!grounded)
                    rb.AddForce(Vector3.down * gravity, ForceMode.Acceleration);
                return;
            }

            // Look: adopt the client's absolute view (client-authoritative aim).
            // 俯仰按当前姿态限位（趴姿 0..60，不允许瞄地）。
            yaw = serverInput.yaw;
            pitch = Mathf.Clamp(serverInput.pitch, PitchMin, PitchMax);
            transform.rotation = Quaternion.Euler(0f, yaw, 0f);

            // Dead: stay prone, no 3C / combat. Apply horizontal drag so a corpse
            // that was moving at the moment of death bleeds off its momentum and
            // stops (velocity-proportional, same stop rate as releasing input), and
            // gravity so an airborne corpse settles onto the ground.
            if (dead)
            {
                // PHASE7: 死亡状态下处理重新部署选择（1/2/3 键）。
                if (health != null && serverInput.deployChoice != 0)
                    health.RequestDeploy(serverInput.deployChoice);

                Vector3 hVel = new Vector3(rb.velocity.x, 0f, rb.velocity.z);
                if (hVel.sqrMagnitude > 0.0001f)
                    rb.AddForce(-hVel * stoppingFriction, ForceMode.Acceleration);

                if (!grounded)
                    rb.AddForce(Vector3.down * gravity, ForceMode.Acceleration);
                return;
            }

            bool justLanded = grounded && !wasGrounded;

            // Landing detection (jump landing / dive completion camera shake).
            if (justLanded)
            {
                // Landing grace: preserve the landing speed briefly so the slide
                // check (which runs before all forces below) wins over the instant
                // drag/clamp that would otherwise kill the speed before the server's
                // ctrl-hold input snapshot arrives (unreliable uplink lags 1-2 ticks).
                //landingGraceEnd = Time.time + landingGrace;

                if (diving)
                {
                    diving = false;
                    RpcCameraShake(diveShakeIntensity, diveShakeDuration, false);
                }
                else if (jumpedOrDived)
                {
                    RpcCameraShake(jumpShakeIntensity, jumpShakeDuration, true);
                }
                jumpedOrDived = false;
            }
            wasGrounded = grounded;

            // Sprint (sticky: enter on forward + shift, exit only on forward release).
            UpdateSprint();

            bool jumpIntent = serverInput.jump;

            // Slide lifecycle (end on jump / dive / slow-down).
            if (sliding)
            {
                bool diveReplaces = serverInput.proneToggle &&
                                    rb.velocity.magnitude > diveTriggerSpeed;

                if (jumpIntent)
                {
                    // Jump out of a slide: stand collider + a full normal jump impulse.
                    EndSlide(endToStand: true);
                    PerformJump();
                }
                else if (diveReplaces)
                    EndSlide(endToStand: true); // dive trigger below takes over this tick
                else if (HorizontalSpeed() < slideEndSpeed)
                    EndSlide();
            }

            // Dive trigger (prone key + fast + grounded).
            bool dived = false;
            if (!sliding && grounded && serverInput.proneToggle &&
                rb.velocity.magnitude > diveTriggerSpeed)
            {
                StartDive();
                dived = true;
            }

            // Slide trigger (ctrl + fast + grounded + off cooldown). Runs BEFORE
            // ApplyMovement so the drag/clamp of the same tick can never destroy the
            // speed the check is looking at.
            if (!dived && !sliding && serverInput.crouchHold &&
                Time.time >= slideCooldownEnd && grounded &&
                CanStartSlide())
            {
                StartSlide();
            }

            // Posture state machine. Returns true when the jump key was consumed to
            // stand up (so it must NOT also produce a jump impulse). Skipped while
            // sliding — the slide owns its collider/posture and resolves the final
            // posture in EndSlide().
            bool jumpConsumed = false;
            if (!sliding)
                jumpConsumed = ProcessPosture(dived, jumpIntent);

            // Movement forces + jump impulse + gravity.
            ApplyMovement(jumpIntent && !jumpConsumed);

            // Dash (快速机动装置): continuous horizontal force over dashDuration.
            if (dashing)
            {
                float dt = Time.fixedDeltaTime;
                rb.AddForce(dashDir * dashForce * dt, ForceMode.Impulse);
                dashRemaining -= dt;
                if (dashRemaining <= 0f)
                    dashing = false;
            }

            // Combat: shooting (gun) + throwing (combat). Fire/aim are fed every tick;
            // reload and fire-mode switch are edge-triggered commands.
            PlayerPosture eff2 = sliding ? PlayerPosture.Crouch : posture;
            Vector3 eye = ComputeEye(eff2);   // PHASE14: 头部球面沿视线方向的表面点
            Vector3 forward = Quaternion.Euler(pitch, yaw, 0f) * Vector3.forward;

            // PHASE7: Q 标记敌人。
            if (serverInput.mark)
                TryMark(eye, forward);

            // PHASE8: 配装槽位键（1 主武器 / 3 可选1 / 4 可选2 / g 特有 / z 投掷物）。
            ProcessSlotKeys(eye, forward);

            bool gunActive = activeSlot < 0;
            int equipIndex = gunActive ? -1 : equipment.GetSlotIndex(activeSlot);

            if (gun != null && gunActive)
            {
                if (serverInput.reload) gun.Reload();
                if (serverInput.switchFireMode) gun.SwitchFireMode();
                gun.Tick(serverInput.fire, serverInput.aim, eye, forward, sprintActive);
            }
            else if (equipment != null && equipIndex >= 0)
            {
                equipment.Tick(equipIndex, serverInput.fire, serverInput.aim,
                               eye, forward, serverInput.move, grounded);
            }
        }

        /// <summary>
        /// PHASE8: 处理配装槽位键。瞬发型直接使用（不切换），普通型切换 activeSlot。
        /// </summary>
        private void ProcessSlotKeys(Vector3 eye, Vector3 forward)
        {
            if (equipment == null) return;

            if (serverInput.slotPrimary)
            {
                activeSlot = -1;   // 主武器
            }

            TrySlotKey(0, serverInput.slotOpt1, eye, forward);
            TrySlotKey(1, serverInput.slotOpt2, eye, forward);
            TrySlotKey(2, serverInput.slotSpecial, eye, forward);
            TrySlotKey(3, serverInput.slotThrowable, eye, forward);
        }

        private void TrySlotKey(int slot, bool pressed, Vector3 eye, Vector3 forward)
        {
            if (!pressed) return;

            var def = equipment.GetSlotDefinition(slot);
            if (def == null) return;

            // 普通型：剩余使用次数为 0 的不能切换；全部为 0 时自然保持手持物。
            if (!def.instantUse && !equipment.HasAmmoInSlot(slot))
                return;

            bool instant = equipment.HandleSlotKey(slot, eye, forward,
                                                   serverInput.move, grounded);
            if (!instant)
                activeSlot = slot;   // 普通型：切换为该槽位
        }

        private void UpdateSprint()
        {
            bool forward = serverInput.move.y > 0.1f;

            if (sprintActive)
            {
                if (!forward)
                {
                    // Releasing the forward key exits sprint (sticky sprint).
                    sprintActive = false;
                    sprintFireHold = 0f;
                }
                else if (serverInput.fire)
                {
                    // Holding fire while sprinting: exit sprint only after the
                    // sprint->fire cooldown (the gun blocks firing while sprinting,
                    // so the cooldown and the sprint exit coincide).
                    sprintFireHold += Time.fixedDeltaTime;
                    float cooldown = (gun != null && gun.Definition != null)
                        ? gun.Definition.sprintFireCooldown
                        : 0.1f;
                    if (sprintFireHold >= cooldown)
                    {
                        sprintActive = false;
                        sprintFireHold = 0f;
                    }
                }
                else if (serverInput.aim)
                {
                    // Aiming and sprinting are mutually exclusive.
                    sprintActive = false;
                    sprintFireHold = 0f;
                }
                else
                {
                    sprintFireHold = 0f;
                }
            }
            else
            {
                // Enter sprint only when not firing / aiming.
                if (serverInput.sprint && forward && !serverInput.fire && !serverInput.aim)
                    sprintActive = true;
            }
        }

        private bool ProcessPosture(bool dived, bool jump)
        {
            bool jumpConsumed = false;
            bool crouchToggle = serverInput.crouchToggle;
            bool proneToggle = serverInput.proneToggle && !dived; // dive consumed the prone key

            // Holding ctrl only enters crouch while grounded. While airborne, a held
            // ctrl is reserved for the slide check (which runs BEFORE this method), so
            // a sprint that lands with ctrl held triggers a slide instead of getting
            // swallowed by the crouch-run speed clamp on the first grounded frame.
            bool crouchHold = serverInput.crouchHold && grounded;
            bool sprint = serverInput.sprint;

            switch (posture)
            {
                case PlayerPosture.Stand:
                    if (crouchToggle) { if (SetPosture(PlayerPosture.Crouch)) crouchByHold = false; }
                    else if (crouchHold && !sliding) { if (SetPosture(PlayerPosture.Crouch)) crouchByHold = true; }
                    else if (proneToggle) SetPosture(PlayerPosture.Prone);
                    break;

                case PlayerPosture.Crouch:
                    if (crouchToggle) { if (SetPosture(PlayerPosture.Stand)) crouchByHold = false; }
                    else if (jump) { SetPosture(PlayerPosture.Stand); crouchByHold = false; jumpConsumed = true; }
                    else if (crouchByHold && !serverInput.crouchHold) { if (SetPosture(PlayerPosture.Stand)) crouchByHold = false; }
                    else if (proneToggle) SetPosture(PlayerPosture.Prone);
                    break;

                case PlayerPosture.Prone:
                    if (proneToggle) SetPosture(PlayerPosture.Stand);
                    else if (crouchToggle) { if (SetPosture(PlayerPosture.Crouch)) crouchByHold = false; }
                    else if (crouchHold) { if (SetPosture(PlayerPosture.Crouch)) crouchByHold = true; }
                    else if (jump) { SetPosture(PlayerPosture.Stand); jumpConsumed = true; }
                    else if (sprint) SetPosture(PlayerPosture.Stand);
                    break;
            }

            return jumpConsumed;
        }

        private void ApplyMovement(bool jump)
        {
            if (grounded)
            {
                Vector3 dir = transform.forward * serverInput.move.y +
                              transform.right * serverInput.move.x;
                if (dir.sqrMagnitude > 1f) dir = dir.normalized;
                bool hasInput = dir.sqrMagnitude > 0.0001f;

                Vector3 hVel = new Vector3(rb.velocity.x, 0f, rb.velocity.z);

                if (sliding)
                {
                    // Slide: light drag only (no drive, no speed clamp).
                    rb.AddForce(-hVel * slideFriction, ForceMode.Acceleration);
                }
                //else if (Time.time < landingGraceEnd)
                //{
                    // Landing grace: pure momentum preservation. No drive, no drag,
                    // no clamp — the slide check (evaluated before forces) gets the
                    // full landing speed instead of an instantly clamped/dragged one.
                //}
                else
                {
                    if (hasInput)
                        rb.AddForce(dir * moveAcceleration, ForceMode.Acceleration);

                    float drag = hasInput ? friction : stoppingFriction;
                    rb.AddForce(-hVel * drag, ForceMode.Acceleration);

                    ClampHorizontalSpeed(CurrentMaxSpeed());
                }

                if (jump)
                {
                    PerformJump();
                }
            }
            else
            {
                // Airborne: gravity only.
                rb.AddForce(Vector3.down * gravity, ForceMode.Acceleration);
            }
        }

        // Normal jump: height-based impulse + takeoff feedback. Also used to jump
        // out of a slide. Sets grounded=false so the same tick's posture logic and
        // ApplyMovement cannot double-apply it.
        private void PerformJump()
        {
            float jumpVelocity = Mathf.Sqrt(2f * gravity * jumpHeight);
            rb.AddForce(Vector3.up * jumpVelocity, ForceMode.Impulse);
            grounded = false;
            jumpedOrDived = true;
            jumpCount++;   // PHASE12: 动画同步（服务端权威计数）
            RpcCameraShake(jumpShakeIntensity, jumpShakeDuration, true);
        }

        /// <summary>
        /// PHASE6 快速机动装置: start a continuous horizontal force over
        /// <see cref="dashDuration"/> seconds in the input direction. No vertical
        /// force. Can be used in the air.
        /// </summary>
        public void Dash(Vector3 dir)
        {
            if (rb == null) return;

            if (dir.sqrMagnitude < 0.0001f) dir = transform.forward;
            dir.y = 0f;
            dir.Normalize();

            dashing = true;
            dashRemaining = dashDuration;
            dashDir = dir;
        }

        /// <summary>
        /// PHASE7: 重新部署落地后重置状态为站姿、清除滑铲/飞扑/冲刺。
        /// </summary>
        public void OnRedeploy()
        {
            dead = false;
            sliding = false;
            diving = false;
            jumpedOrDived = false;
            sprintActive = false;

            if (rb != null)
            {
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }

            SetPosture(PlayerPosture.Stand);
        }

        /// <summary>
        /// PHASE7 标记：从相机射线标记第一个敌方实体（穿过友军，遇墙停止）。
        /// </summary>
        [Server]
        private void TryMark(Vector3 eye, Vector3 forward)
        {
            const float range = 200f;
            var hits = Physics.RaycastAll(eye, forward, range);
            if (hits.Length == 0) return;

            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

            var self = GetComponent<NetworkCombatant>();

            foreach (var h in hits)
            {
                var c = h.collider.GetComponentInParent<NetworkCombatant>();
                if (c != null)
                {
                    // 敌方：标记；友军/自身：穿过继续。
                    if (c.teamId >= 0 && (self == null || c.teamId != self.teamId))
                    {
                        c.SetMarked(NetworkTime.time + 10.0,
                                    self != null ? self.teamId : -1, self);
                        return;
                    }
                    continue;
                }

                // 非实体几何：遮挡，停止。
                break;
            }
        }

        private float CurrentMaxSpeed()
        {
            if (sliding) return float.PositiveInfinity;

            bool sprinting = sprintActive;
            switch (posture)
            {
                case PlayerPosture.Stand:
                    return sprinting ? standSprintSpeed : standWalkSpeed;
                case PlayerPosture.Crouch:
                    return sprinting ? crouchSprintSpeed : crouchWalkSpeed;
                case PlayerPosture.Prone:
                    return proneSpeed;
            }
            return standWalkSpeed;
        }

        private void ClampHorizontalSpeed(float max)
        {
            if (float.IsPositiveInfinity(max)) return;

            Vector3 v = rb.velocity;
            Vector3 h = new Vector3(v.x, 0f, v.z);
            if (h.magnitude > max)
            {
                h = h.normalized * max;
                rb.velocity = new Vector3(h.x, v.y, h.z);
            }
        }

        private float HorizontalSpeed()
        {
            Vector3 v = rb.velocity;
            return new Vector3(v.x, 0f, v.z).magnitude;
        }

        private bool CanStartSlide()
        {
            return HorizontalSpeed() > slideTriggerSpeed;
            //return rb.velocity.magnitude > slideTriggerSpeed;
        }

        private void StartSlide()
        {
            sliding = true;
            SetPosture(PlayerPosture.Crouch); // crouch collider

            Vector3 h = new Vector3(rb.velocity.x, 0f, rb.velocity.z);
            if (h.magnitude > 0.01f)
            {
                float delta = slideSpeed - h.magnitude;
                if (delta > 0f)
                    rb.AddForce(h.normalized * delta, ForceMode.Impulse);
            }

            RpcCameraShake(slideShakeIntensity, slideShakeDuration, false);
        }

        private void EndSlide(bool endToStand = false)
        {
            sliding = false;
            slideCooldownEnd = Time.time + slideCooldown;

            if (endToStand)
            {
                // Ceiling too low to stand: settle for crouch instead of clipping.
                if (!SetPosture(PlayerPosture.Stand))
                    SetPosture(PlayerPosture.Crouch);
            }
            else if (serverInput.crouchHold)
            {
                SetPosture(PlayerPosture.Crouch);
                crouchByHold = true;
            }
            else
            {
                if (!SetPosture(PlayerPosture.Stand))
                {
                    SetPosture(PlayerPosture.Crouch);
                    crouchByHold = false;
                }
            }
        }

        private void StartDive()
        {
            diving = true;
            SetPosture(PlayerPosture.Prone); // flat collider

            // Switching to prone rotates the capsule to lie flat, which momentarily
            // lifts the body off the ground (centre rotation) — lift it, then slam down.
            float lift = standHeight * 0.5f - proneHeight * 0.5f;
            rb.position += Vector3.up * lift;

            rb.AddForce(Vector3.down * diveSpeed, ForceMode.Impulse);
            grounded = false;
        }

        private bool SetPosture(PlayerPosture p)
        {
            if (!HasOverheadClearance(p)) return false;

            posture = p;
            ApplyActiveCollider();
            return true;
        }

        // Vertical extent of the CURRENT active collider above transform.position.
        private float EffectiveHeight()
        {
            PlayerPosture eff = sliding ? PlayerPosture.Crouch : posture;
            switch (eff)
            {
                case PlayerPosture.Crouch: return crouchHeight;
                case PlayerPosture.Prone: return proneHeight;
                default: return standHeight;
            }
        }

        /// <summary>
        /// Overhead clearance probe: a ray from the capsule TOP straight up.
        /// Entering a TALLER posture (crouch / stand) is blocked when an obstacle sits
        /// lower than that posture's height (e.g. standing up under a low ledge would
        /// clip the head into it). Entering prone is never blocked (lowest posture).
        /// </summary>
        private bool HasOverheadClearance(PlayerPosture target)
        {
            float targetHeight;
            switch (target)
            {
                case PlayerPosture.Crouch: targetHeight = crouchHeight; break;
                case PlayerPosture.Stand: targetHeight = standHeight; break;
                default: return true; // prone: always allowed
            }

            float currentTop = EffectiveHeight();
            float distance = targetHeight - currentTop - clearanceRayOffset;
            if (distance <= 0f) return true; // already as tall as the target

            Vector3 origin = transform.position + Vector3.up * (currentTop + clearanceRayOffset);
            if (Physics.Raycast(origin, Vector3.up, out RaycastHit hit, distance,
                                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            {
                // Ignore our own colliders (the origin is already above them, but a
                // sloped contact or interpolation jitter could still graze them).
                if (hit.collider == standCollider || hit.collider == crouchCollider)
                    return true;
                return false;
            }
            return true;
        }

        private void ApplyActiveCollider()
        {
            PlayerPosture eff = sliding ? PlayerPosture.Crouch : posture;

            switch (eff)
            {
                case PlayerPosture.Stand:
                    standCollider.enabled = true;
                    crouchCollider.enabled = false;
                    standCollider.direction = 1; // Y
                    standCollider.center = new Vector3(0f, standHeight * 0.5f, 0f);
                    break;

                case PlayerPosture.Crouch:
                    standCollider.enabled = false;
                    crouchCollider.enabled = true;
                    crouchCollider.direction = 1; // Y
                    crouchCollider.center = new Vector3(0f, crouchHeight * 0.5f, 0f);
                    break;

                case PlayerPosture.Prone:
                    standCollider.enabled = true;
                    crouchCollider.enabled = false;
                    standCollider.direction = 2; // Z (lying forward)
                    standCollider.center = new Vector3(0f, proneHeight * 0.5f, 0f);
                    break;
            }
        }

        // ---------------------------------------------------------------
        // CAMERA SHAKE (server -> owning client)
        // ---------------------------------------------------------------

        /// <summary>
        /// Client-side camera shake entry point. Called from an RPC (server-driven
        /// jump/slide/dive) or directly by the local <see cref="NetworkGun"/> visual
        /// recoil (already running on the owning client).
        /// </summary>
        public void ApplyCameraShake(float intensity, float duration, bool vertical)
        {
            if (!isLocalPlayer || playerCamera == null) return;

            shakeIntensity = intensity;
            shakeDuration = duration;
            shakeTime = 0f;
            shakeVertical = vertical;
        }

        /// <summary>
        /// PHASE8 受击反馈：轻微相机震动 + FOV 增大 2%，在 2 渲染帧内快速回到正常值。
        /// </summary>
        public void ApplyDamageFeedback(float damage)
        {
            if (!isLocalPlayer || playerCamera == null) return;

            fovPulseFrames = fovPulseTotal;
            fovPulseAmount = baseFov * 0.02f;
            ApplyCameraShake(0.06f, 0.06f, false);
        }

        [ClientRpc]
        private void RpcCameraShake(float intensity, float duration, bool vertical)
        {
            ApplyCameraShake(intensity, duration, vertical);
        }

        // ---------------------------------------------------------------
        // GROUND DETECTION (server-side)
        // ---------------------------------------------------------------
        private void OnCollisionStay(Collision collision)
        {
            if (!isServer) return;

            foreach (var contact in collision.contacts)
            {
                if (Vector3.Dot(contact.normal, Vector3.up) > 0.5f)
                {
                    grounded = true;
                    return;
                }
            }
        }

        private void OnCollisionExit(Collision collision)
        {
            if (isServer) grounded = false;
        }
    }
}
