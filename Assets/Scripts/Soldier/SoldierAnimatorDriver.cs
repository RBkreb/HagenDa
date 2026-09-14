using HagenDa.Networking;
using Mirror;
using UnityEngine;

namespace HagenDa.Soldier
{
    /// <summary>
    /// PHASE14 士兵动画网络驱动（从0重写，替代旧 NetworkSoldierAnimator）。
    ///
    /// 只从**已同步的服务端权威状态**驱动（Mirror "Option A"，无新 RPC/NetworkAnimator）：
    ///  - 速度：服务端 rb.velocity / 客户端位置差分（平滑，传送剔除）
    ///  - pitch/posture/sliding/jumpCount：NetworkPlayerController SyncVar
    ///  - aimAmount/recoil/shotCount/sprinting/reloading：NetworkGun SyncVar
    ///  - IsDead：NetworkPlayerHealth
    ///
    /// Animator（SoldierLoco.controller，5 层）：
    ///   L0 Base（空=绑定姿态；站/蹲静止露出 → 下半身 T pose）
    ///   L1 LocomotionFull（无 mask 八向树，空手移动时权重 1）
    ///   L2 LocomotionLower（下半身 mask 八向树，持枪移动时权重 1）
    ///   L3 UpperActions（上半身 mask，恒 0 —— 上半身全由 rig 接管，禁播八向/动作剪辑）
    ///   L4 Death（无 mask，无出边占位态；复活 = Rebind）
    ///   参数：MoveX/MoveZ=局部水平速度(m/s，喂 2D Freeform 树的走(3.5)/跑(7.5)环)、
    ///         LocoSpeed=状态速度倍率（0=冻结帧：滑铲/趴/滞空 0.1s 后）、Death=触发。
    /// </summary>
    [RequireComponent(typeof(NetworkIdentity))]
    public class SoldierAnimatorDriver : NetworkBehaviour
    {
        [Header("References")]
        [Tooltip("角色模型根（Animator 节点）。留空自动查找子级。")]
        public GameObject model;

        [Header("Locomotion")]
        [Tooltip("静止判定阈值（水平 m/s）。")]
        public float movingSpeed = 0.5f;
        [Tooltip("移动混合死区（低于该速度归零，防抖）。")]
        public float deadZoneSpeed = 0.15f;
        [Tooltip("静止冻结时保持的最小混合幅度（>0 远离树原点）。")]
        public float locoHoldSpeed = 1.2f;
        [Tooltip("速度平滑系数（每帧 lerp 权重）。")]
        public float velocitySmooth = 0.25f;

        [Header("Aim")]
        [Tooltip("pitch 平滑时间（秒）。")]
        public float pitchSmoothTime = 0.06f;

        [Header("Jump (程序化——动画包无跳跃剪辑)")]
        public float jumpCrouchAmount = 0.22f;
        public float jumpCrouchSpeed = 3.5f;
        [Tooltip("滞空后保持播放的时长（秒），随后停帧（PHASE14：停在滞空后 0.1s 的帧）。")]
        public float airFreezeDelay = 0.1f;

        private SoldierRigDriver rig;
        private NetworkPlayerController controller;
        private NetworkPlayerHealth health;
        private NetworkGun gun;
        private Rigidbody rb;

        private Animator animator;

        private static readonly int MoveX = Animator.StringToHash("MoveX");
        private static readonly int MoveZ = Animator.StringToHash("MoveZ");
        private static readonly int LocoSpeed = Animator.StringToHash("LocoSpeed");
        private const int LayerFull = 1, LayerLower = 2, LayerUpper = 3;

        // diff caches（模型重绑/复活后重置）
        private bool cachesValid;
        private bool dead;
        private uint jumpCountCache;
        private float jumpAnim;
        private float airTimer;                 // 滞空已播放时长
        private bool wasAirborne;
        private float pitchSmoothed, pitchVel;
        private float ownerAimCur;   // 拥有者客户端本地瞄准平滑量（直驱枪械视觉，不经服务端回环）
        private Vector3 smoothedVelocity;
        private Vector3 lastNetworkPosition;
        private float layerFullCur = 1f, layerLowerCur;
        private float heldMoveX, heldMoveZ = 1.2f;

        private void Awake()
        {
            controller = GetComponent<NetworkPlayerController>();
            health = GetComponent<NetworkPlayerHealth>();
            gun = GetComponent<NetworkGun>();
            rb = GetComponent<Rigidbody>();
        }

        /// <summary>当前角色 rig（NetworkGun 绑枪用）。</summary>
        public SoldierRigDriver ActiveRig => rig;
        /// <summary>当前显示模型根（MirrorView 镜像依赖）。</summary>
        public GameObject ActiveModel => model;

        private void Update()
        {
            if (model == null)
            {
                var anim = GetComponentInChildren<Animator>(true);
                model = anim != null ? anim.transform.gameObject : null;
            }
            if (rig == null && model != null)
            {
                rig = model.GetComponent<SoldierRigDriver>();
                animator = rig != null ? rig.ModelAnimator
                    : model.GetComponentInChildren<Animator>(true);
            }
            // rig 的 ModelAnimator 由其 EnsureCache 填充（可能晚于本驱动首次解析，
            // 如 NetworkGun.BindWeapon 先行）—— animator 仍空时每帧重试，防永久 return。
            if (rig != null && animator == null)
                animator = rig.ModelAnimator != null
                    ? rig.ModelAnimator
                    : model.GetComponentInChildren<Animator>(true);
            if (rig == null || animator == null) return;

            if (!cachesValid)
            {
                ResetDiffCaches();
                cachesValid = true;
            }

            float dt = Time.deltaTime;
            UpdateDeath();
            if (!cachesValid) return;   // revive rebind —— 下帧重定基线

            var s = BuildFrameState(dt);
            DriveAnimator(s, dt);
            rig.SetFrameState(in s);
        }

        // ---------------------------------------------------------------
        // FRAME STATE（全部来自服务端权威状态）
        // ---------------------------------------------------------------

        private SoldierFrameState BuildFrameState(float dt)
        {
            bool isDead = health != null && health.IsDead;
            var posture = isDead ? PlayerPosture.Prone
                : (controller != null ? controller.posture : PlayerPosture.Stand);
            bool sliding = !isDead && controller != null && controller.sliding;
            bool airborne = !isDead && Mathf.Abs(smoothedVelocity.y) > 0.6f;
            Vector3 planar = new Vector3(smoothedVelocity.x, 0f, smoothedVelocity.z);
            bool moving = !isDead && planar.magnitude > movingSpeed;

            // ---- 瞄准位姿 ----
            // 拥有者：**直接跟随本地相机**（180fps 平滑 + 含后座上抬的 renderPitch）——
            // 枪械水平/垂直瞄准因此零延迟零抖动，开火后座时枪随相机一起抬
            //（用户实测：跟随实体网络 yaw 会 60Hz 阶梯颤动、后座不跟枪）。
            // 远端：pitch(SyncVar)+recoil 与实体 yaw 推导（服务端权威状态）。
            Quaternion eyeRot;
            Vector3 eyePos;
            var cam = isLocalPlayer && controller != null ? controller.playerCamera : null;
            float targetPitch = controller != null ? controller.pitch : 0f;
            pitchSmoothed = Mathf.SmoothDamp(pitchSmoothed, targetPitch, ref pitchVel,
                Mathf.Max(0.001f, pitchSmoothTime));
            float remoteRecoil = !isLocalPlayer && gun != null ? gun.recoil : 0f;
            eyeRot = Quaternion.Euler(pitchSmoothed + remoteRecoil, transform.eulerAngles.y, 0f);
            if (cam != null)
            {
                eyePos = cam.transform.position;
                eyeRot = cam.transform.rotation;   // 拥有者：相机位姿直驱（水平+垂直+后座）
            }
            else if (rig.HeadAnchor != null)
                eyePos = rig.HeadAnchor.position;
            else
                eyePos = transform.position + Vector3.up * FallbackEyeHeight(posture);

            // ---- 跳跃下蹲包络（jumpCount 边沿触发，0→峰→0）----
            uint jc = controller != null ? controller.jumpCount : 0;
            if (jc != jumpCountCache)
            {
                jumpCountCache = jc;
                jumpAnim = 1f;
            }
            jumpAnim = Mathf.MoveTowards(jumpAnim, 0f, dt * jumpCrouchSpeed);
            float jumpDip = Mathf.Sin(jumpAnim * Mathf.PI) * jumpCrouchAmount;

            bool weaponHeld = gun != null && !isDead && controller != null
                && controller.activeSlot < 0 && rig.BoundGun != null;

            // ---- 枪械视觉状态 ----
            // PHASE14:拥有者客户端用**本地意图**直驱瞄准/冲刺视觉（aimAmount 以
            // aimTime 速率本地平滑）——不经 客户端→服务端→SyncVar 回环，零延迟；
            // 射击方向仍服务端权威（SyncVar aimAmount/sprinting 只供远端实体表现）。
            float aimState;
            bool sprintState;
            if (isLocalPlayer && !isDead && controller != null && gun != null && gun.Definition != null)
            {
                float aimTarget = controller.AimHeld ? 1f : 0f;
                float rate = gun.Definition.aimTime > 0f ? 1f / gun.Definition.aimTime : 100f;
                ownerAimCur = Mathf.MoveTowards(ownerAimCur, aimTarget, rate * dt);
                aimState = ownerAimCur;
                sprintState = controller.Sprinting;
            }
            else
            {
                aimState = gun != null ? gun.aimAmount : 0f;
                sprintState = gun != null && gun.sprinting;
            }

            return new SoldierFrameState
            {
                dead = isDead,
                posture = posture,
                sliding = sliding,
                airborne = airborne,
                moving = moving,
                eyePos = eyePos,
                eyeRot = eyeRot,
                weaponHeld = weaponHeld,
                aimAmount = isDead ? 0f : aimState,
                sprinting = sprintState && !isDead,
                recoil = gun != null ? gun.recoil : 0f,
                shotCount = gun != null ? gun.shotCount : 0,
                bodyOffsetExtra = Vector3.down * jumpDip,
            };
        }

        private static float FallbackEyeHeight(PlayerPosture p)
        {
            switch (p)
            {
                case PlayerPosture.Crouch: return 0.75f;
                case PlayerPosture.Prone: return 0.35f;
                default: return 1.65f;
            }
        }

        // ---------------------------------------------------------------
        // ANIMATOR
        // ---------------------------------------------------------------

        private void DriveAnimator(in SoldierFrameState s, float dt)
        {
            // ---- 八向混合输入：局部水平速度；低于死区归零；移动时保底幅度 ----
            Vector3 planar = new Vector3(smoothedVelocity.x, 0f, smoothedVelocity.z);
            Vector3 local = transform.InverseTransformDirection(planar);
            if (local.magnitude < deadZoneSpeed) local = Vector3.zero;
            bool hasMove = local.sqrMagnitude > 0.0001f;
            if (hasMove)
            {
                Vector2 hv = new Vector2(local.x, local.z);
                if (hv.magnitude < locoHoldSpeed) hv = hv.normalized * locoHoldSpeed;
                heldMoveX = hv.x;
                heldMoveZ = hv.y;
            }
            if (!s.moving && !s.sliding) { heldMoveX = 0f; heldMoveZ = 0f; }
            animator.SetFloat(MoveX, heldMoveX);
            animator.SetFloat(MoveZ, heldMoveZ);

            // ---- LocoSpeed（状态速度倍率）：0=冻结帧 ----
            // 滑铲=冻结；趴=冻结；滞空=播放 airFreezeDelay 后停帧（跳的下蹲播完伸直）；
            // 死亡层接管后无所谓。
            bool freeze = s.dead || s.sliding || s.posture == PlayerPosture.Prone;
            if (s.airborne)
            {
                if (!wasAirborne) airTimer = 0f;
                airTimer += dt;
                freeze |= airTimer >= airFreezeDelay;
            }
            wasAirborne = s.airborne;
            animator.SetFloat(LocoSpeed, freeze ? 0f : 1f);

            // ---- 层权重：静止=双零（露 L0 绑定姿态 → 下半身 T pose + 脚 IK）；----
            //      移动=按持枪互补（L1 全身 / L2 下半身）；L3 UpperActions 恒 0。
            float fullTarget = 0f, lowerTarget = 0f;
            if (s.moving && !s.dead)
            {
                if (s.weaponHeld) lowerTarget = 1f;
                else fullTarget = 1f;
            }
            layerFullCur = Mathf.MoveTowards(layerFullCur, fullTarget, dt * 5f);
            layerLowerCur = Mathf.MoveTowards(layerLowerCur, lowerTarget, dt * 5f);
            animator.SetLayerWeight(LayerFull, layerFullCur);
            animator.SetLayerWeight(LayerLower, layerLowerCur);
            animator.SetLayerWeight(LayerUpper, 0f);
        }

        // ---------------------------------------------------------------
        // DEATH / REVIVE
        // ---------------------------------------------------------------

        private void UpdateDeath()
        {
            bool isDead = health != null && health.IsDead;
            if (isDead == dead) return;
            dead = isDead;

            if (isDead)
            {
                // 占位死亡态（无剪辑、无出边）；ragdoll 后续接入。
                animator.SetTrigger("Death");
                if (rig != null) rig.ApplyDeath();
            }
            else
            {
                // 死亡态无出边 → 干净 Rebind 复活。
                animator.Rebind();
                if (rig != null) rig.ApplyRevive();
                cachesValid = false;   // 下帧重定基线
            }
        }

        private void ResetDiffCaches()
        {
            dead = health != null && health.IsDead;
            jumpCountCache = controller != null ? controller.jumpCount : 0;
            jumpAnim = 0f;
            airTimer = 0f;
            wasAirborne = false;
            pitchSmoothed = controller != null ? controller.pitch : 0f;
            pitchVel = 0f;
            ownerAimCur = 0f;
            smoothedVelocity = Vector3.zero;
            lastNetworkPosition = transform.position;
            layerFullCur = 1f;
            layerLowerCur = 0f;
            heldMoveX = 0f;
            heldMoveZ = 0f;
            animator.SetFloat(MoveX, 0f);
            animator.SetFloat(MoveZ, 0f);
            animator.SetFloat(LocoSpeed, 1f);
            animator.SetLayerWeight(LayerFull, 1f);
            animator.SetLayerWeight(LayerLower, 0f);
            animator.SetLayerWeight(LayerUpper, 0f);
        }

        // ---------------------------------------------------------------
        // VELOCITY SOURCE（服务端刚体 / 客户端位置差分）
        // ---------------------------------------------------------------

        private void LateUpdate()
        {
            Vector3 vel;
            if (isServer && rb != null && !rb.isKinematic)
            {
                vel = rb.velocity;
            }
            else
            {
                Vector3 delta = transform.position - lastNetworkPosition;
                float dt = Time.deltaTime;
                vel = dt > 1e-4f ? delta / dt : Vector3.zero;
                if (vel.magnitude > 30f) vel = Vector3.zero;   // 传送（重新部署）
            }
            lastNetworkPosition = transform.position;
            smoothedVelocity = Vector3.Lerp(smoothedVelocity, vel, velocitySmooth);
        }
    }
}
