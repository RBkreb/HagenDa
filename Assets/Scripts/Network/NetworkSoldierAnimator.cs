using HagenDa.Animation.Rigging;
using Mirror;
using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// Server-authoritative soldier animation driver (PHASE13).
    ///
    /// Animator: "NetworkSoldierLayers" (4 clip layers)
    ///   L0 LocomotionFull  — 8-dir blend tree, no mask      (weight 1 when NO weapon)
    ///   L1 LocomotionLower — same tree, lower-body mask     (weight 1 when weapon held)
    ///   L2 UpperActions    — upper mask, Shoot/Reload clips (exit-time exits)
    ///   L3 Death           — Death1-3, no exits (revive = Rebind)
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

        [Header("Differential turn (lower body lag)")]
        [Tooltip("指数趋近时间常数(秒)——等效 SmoothDamp 手感。")]
        public float turnSmoothTime = 0.18f;
        [Tooltip("滞后超过 60° 后每度每秒的额外追赶角速度(度/秒/度)。")]
        public float turnCatchupBoost = 6f;
        [Tooltip("超过该滞后角后开始压低脚部 IK 杄重(度)。")]
        public float turnFootDipStart = 35f;

        [Header("Posture model offset (自姿态采集测量,模型根位移)")]
        public Vector3 crouchModelOffset = new Vector3(0f, -0.5f, 0f);
        public Vector3 proneModelOffset = new Vector3(0f, -0.9f, 0.25f);

        [Header("Aim")]
        public float aimTargetDistance = 30f;
        public float pitchSmoothTime = 0.06f;

        private NetworkPlayerController controller;
        private NetworkPlayerHealth health;
        private NetworkGun gun;
        private NetworkCombatant combatant;
        private Rigidbody rb;

        private Animator animator;              // animator of the currently shown model
        private SoldierRigSetup rig;            // baked rig companion of the shown model
        private GameObject activeModel;
        private int activeTeam = int.MinValue;
        private bool ownerHidden;               // first-person: this client's own player

        private static readonly int MoveX = Animator.StringToHash("MoveX");
        private static readonly int MoveZ = Animator.StringToHash("MoveZ");
        private static readonly int IdleUpHash = Animator.StringToHash("IdleUp");
        private const int LayerFull = 1, LayerLower = 2, LayerUpper = 3;   // L0 = 哑基础层(权重不可驱动)

        // Diff caches (re-baselined after model switch / rebind).
        private bool cachesValid;
        private bool dead;
        private bool reloading;
        private uint shotCount;
        private Vector3 smoothedVelocity;
        private Vector3 lastNetworkPosition;

        // Differential turn state.
        private bool yawInit;
        private float dampedYaw;

        // Smoothed procedural values.
        private float pitchSmoothed;
        private float pitchVel;
        private float layerFullCur = 1f;
        private float handsWeightCur = 1f;
        private float spineWeightCur;
        private float footWeightCur = 1f;
        private float poseCrouchCur;
        private float poseProneCur;

        private const float DeadZoneSpeed = 0.15f;   // planar speed → idle blend
        private const float MovingSpeed = 0.5f;      // feet IK off when moving
        private const float SpineMaxTwist = 60f;     // 与 SpineAimConstraint.data 一致(仅用于 dip)

        private void Awake()
        {
            controller = GetComponent<NetworkPlayerController>();
            health = GetComponent<NetworkPlayerHealth>();
            gun = GetComponent<NetworkGun>();
            combatant = GetComponent<NetworkCombatant>();
            rb = GetComponent<Rigidbody>();
        }

        public override void OnStartClient()
        {
            if (isLocalPlayer)
            {
                // First-person: the camera sits inside the head — hide both models.
                ownerHidden = true;
                if (redModel != null) redModel.SetActive(false);
                if (blueModel != null) blueModel.SetActive(false);
                activeModel = null;
                animator = null;
                rig = null;
            }
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
            if (team == activeTeam && (activeModel != null || ownerHidden)) return;
            activeTeam = team;

            if (ownerHidden) { activeModel = null; animator = null; rig = null; return; }

            var next = team == (int)MatchTeam.Blue
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
            }
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
            bool weaponHeld = WeaponHeld;
            layerFullCur = Mathf.MoveTowards(layerFullCur, weaponHeld ? 0f : 1f, dt * 5f);
            animator.SetLayerWeight(LayerFull, layerFullCur);
            animator.SetLayerWeight(LayerLower, 1f - layerFullCur);   // 腿部权重恒 1,交接平滑
        }

        private void UpdateMovementParams()
        {
            Vector3 v = dead ? Vector3.zero : smoothedVelocity;
            v.y = 0f;
            if (v.magnitude < DeadZoneSpeed) v = Vector3.zero;
            Vector3 local = transform.InverseTransformDirection(v);
            animator.SetFloat(MoveX, local.x);
            animator.SetFloat(MoveZ, local.z);
        }

        // ---------------------------------------------------------------
        // ACTION EDGES (Shoot / Reload)
        // ---------------------------------------------------------------

        private void UpdateActionEdges()
        {
            if (dead) return;
            bool weaponHeld = WeaponHeld;

            bool r = gun != null && gun.reloading;
            if (r != reloading)
            {
                reloading = r;
                if (r && weaponHeld) animator.SetTrigger("Reload");
            }

            if (gun != null && gun.shotCount != shotCount)
            {
                shotCount = gun.shotCount;
                if (weaponHeld) animator.SetTrigger("Shoot");
            }
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

            // ---- aim pose + target(眼睛 + pitch/yaw) ----
            float eyeH = EyeHeight(sliding ? PlayerPosture.Crouch : posture);
            Vector3 eyeOrigin = transform.position + Vector3.up * eyeH;
            float targetPitch = controller != null ? controller.pitch : 0f;
            pitchSmoothed = Mathf.SmoothDamp(pitchSmoothed, targetPitch, ref pitchVel, pitchSmoothTime);
            float aimYaw = transform.eulerAngles.y;
            Quaternion aimDir = Quaternion.Euler(pitchSmoothed, aimYaw, 0f);
            rig.SetAimPose(eyeOrigin, aimDir);
            rig.SetAimTarget(eyeOrigin + aimDir * Vector3.forward * aimTargetDistance);

            // ---- ADS:武器锚点 = 胸部骨骼实际位置(随 bodyposition 下移)→ 眼高混合 ----
            // 枪平时贴在胸口,蹲/趴时随身体下移;ADS 才 lerp 到绝对眼高。chest 骨骼
            // 位置是上一帧 rig 应用后的值(滞后一帧,无感)。
            float aimAmount = dead ? 0f : (gun != null ? gun.aimAmount : 0f);
            var chestBone = animator.GetBoneTransform(HumanBodyBones.Chest);
            Vector3 chestAnchor = chestBone != null ? chestBone.position
                : transform.position + Vector3.up * (eyeH * 0.72f);
            rig.SetWeaponAnchorPosition(Vector3.Lerp(chestAnchor, eyeOrigin, aimAmount));

            // ---- 下半身差速转身(指数趋近 + 超限加速追赶) ----
            if (!yawInit) { dampedYaw = aimYaw; yawInit = true; }
            float lag = Mathf.DeltaAngle(dampedYaw, aimYaw);
            float absLag = Mathf.Abs(lag);
            float rate = absLag / Mathf.Max(0.01f, turnSmoothTime);
            float excess = absLag - SpineMaxTwist;
            if (excess > 0f) rate += excess * turnCatchupBoost;
            float step = Mathf.Min(absLag, rate * dt);
            dampedYaw += Mathf.Sign(lag) * step;
            rig.SetHipsYaw(dampedYaw);

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

            // bodyposition 下移量喂给 Driver_Offset;约束权重 = 下蹲程度(蹲/趴任一)。
            rig.SetBodyOffset(crouchModelOffset * poseCrouchCur + proneModelOffset * poseProneCur);
            rig.SetHipsPosWeight(Mathf.Max(poseCrouchCur, poseProneCur));

            // ---- 权重 ----
            // 趴:姿态层完全接管,髋部转向/脊柱瞄准关闭( hips/ spine 权重 → 0)
            rig.SetHipsWeight(dead ? 0f : 1f - poseProneCur);

            float spineTarget = weaponHeld && !dead ? 1f - poseProneCur : 0f;   // 无武器时剪辑接管脊柱
            spineWeightCur = Mathf.MoveTowards(spineWeightCur, spineTarget, dt * 5f);
            rig.SetSpineWeight(spineWeightCur);

            // 脚 IK:静止/蹲/趴=1,移动/滑铲/滞空=0,大幅转身压低(Q6a/Q12)
            bool moving = smoothedVelocity.SetY0().magnitude > MovingSpeed;
            bool airborne = Mathf.Abs(smoothedVelocity.y) > 0.6f;
            float footTarget = (moving || sliding || airborne || dead) ? 0f : 1f;
            float turnDip = Mathf.Clamp01(1f - (absLag - turnFootDipStart) / (SpineMaxTwist - turnFootDipStart));
            footTarget *= turnDip;
            footWeightCur = Mathf.MoveTowards(footWeightCur, footTarget, dt * 6f);
            rig.SetFootIKWeight(footWeightCur, footWeightCur);

            // 手部:换弹/射击剪辑让位(Q4),死亡全零
            bool actionPlaying = false;
            if (weaponHeld && !dead)
            {
                var st2 = animator.GetCurrentAnimatorStateInfo(LayerUpper);
                if (st2.shortNameHash != IdleUpHash) actionPlaying = true;
                else if (animator.IsInTransition(LayerUpper))
                {
                    var next = animator.GetNextAnimatorStateInfo(LayerUpper);
                    if (next.shortNameHash != 0 && next.shortNameHash != IdleUpHash) actionPlaying = true;
                }
            }
            float handsTarget = (weaponHeld && !dead && !actionPlaying) ? 1f : 0f;
            handsWeightCur = Mathf.MoveTowards(handsWeightCur, handsTarget, dt * 7f);
            rig.SetHandsWeight(handsWeightCur);
        }

        private bool WeaponHeld => rig != null && rig.BoundWeapon != null && !dead;

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
            smoothedVelocity = Vector3.zero;
            pitchSmoothed = controller != null ? controller.pitch : 0f;
            pitchVel = 0f;
            yawInit = false;
            layerFullCur = 1f;
            handsWeightCur = WeaponHeld ? 1f : 0f;
            animator.SetFloat(MoveX, 0f);
            animator.SetFloat(MoveZ, 0f);
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