using Mirror;
using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// Server-authoritative soldier animation driver (PHASE12). Sits on the entity
    /// root and maps ALREADY-SYNCED server state onto the Kevin Iglesias soldier
    /// Animator (HumanM@SoldierAnimations — every parameter is a trigger), so no
    /// extra RPCs / NetworkAnimator are needed:
    ///
    ///  - movement   : planar speed (server: rb.velocity, clients: networked
    ///                 position delta) -> NoMovement / Walk / Run / Sprint, with
    ///                 lateral-velocity strafing -> StrafeL / StrafeR
    ///  - posture    : NetworkPlayerController.posture (SyncVar) -> Crouch/Prone/StandUp
    ///  - weapon     : M4 -> AssaultRifle
    ///  - slide      : controller.sliding (SyncVar) edge -> RunSlide
    ///  - death      : health.IsDead (SyncVar) -> Death01..05 / ProneDeath (re-armed
    ///                 while dead); revive -> animator.Rebind
    ///  - reload     : gun.reloading (SyncVar) edge -> Reload
    ///  - aim        : gun.aimAmount (SyncVar) edge -> Aim
    ///  - shots      : gun.shotCount (SyncVar counter) edge -> Shoot01
    ///  - jump       : controller.jumpCount (SyncVar counter) edge -> Jump
    ///  - hit react  : NetworkPlayerHealth.RpcDamageReaction -> Damage01..03
    ///
    /// IMPORTANT (vendor mechanic): most transitions in HumanM@SoldierAnimations
    /// combine the weapon/posture trigger with the action trigger (e.g.
    /// "AssaultRifle && Jump"), and triggers are consumed by a single state
    /// machine evaluation. The vendor demo (HumanSoldierController) therefore
    /// re-fires weapon + posture + movement EVERY FRAME — this driver replicates
    /// that pattern; actions fire on their edges on top of it.
    ///
    /// Also switches the team body model: red team -> redModel (Natlan Soldier),
    /// blue team -> blueModel (Fatui Bodyguard). teamId is a SyncVar on
    /// <see cref="NetworkCombatant"/>; unassigned (-1) falls back to the red model.
    /// </summary>
    [RequireComponent(typeof(NetworkIdentity))]
    public class NetworkSoldierAnimator : NetworkBehaviour
    {
        [Header("Team models (children of the entity root)")]
        public GameObject redModel;
        public GameObject blueModel;

        private NetworkPlayerController controller;
        private NetworkPlayerHealth health;
        private NetworkGun gun;
        private NetworkCombatant combatant;
        private Rigidbody rb;

        private Animator animator;          // animator of the currently shown model
        private GameObject activeModel;
        private int activeTeam = int.MinValue;
        private bool ownerHidden;           // first-person: this client's own player

        // Edge-diff state (actions fire once per change; weapon/posture/movement
        // are re-fired every frame like the vendor demo).
        private bool cachesValid;
        private bool dead;
        private string deathClip;
        private bool sliding;
        private bool reloading;
        private bool aiming;
        private uint shotCount;
        private uint jumpCount;
        private float nextDamageTime;

        // Locomotion.
        private enum MoveState { None, Walk, Run, Sprint, StrafeL, StrafeR }
        private Vector3 smoothedVelocity;
        private const float WalkSpeed = 0.25f;
        private const float RunSpeed = 4.2f;     // stand walk 3.5 -> run
        private const float SprintSpeed = 6.8f;  // sprint 7.5 -> sprint clip
        private const float StrafeBias = 1.15f;  // |vx| must exceed |vz| * bias

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
            }
        }

        private void Update()
        {
            UpdateTeamModel();

            if (activeModel == null || animator == null) return;

            if (!cachesValid)
            {
                ResetDiffCaches();
                cachesValid = true;
            }

            UpdateDeathState();
            UpdateActionEdges();

            // Vendor pattern: re-arm the combined-condition triggers every frame.
            // While dead the corpse stays prone and motionless.
            animator.SetTrigger("AssaultRifle");
            SetPostureTrigger(dead ? PlayerPosture.Prone
                                   : (controller != null ? controller.posture : PlayerPosture.Stand));
            var mv = dead ? MoveState.None : ComputeMoveState();
            animator.SetTrigger(mv.ToString());

            // Keep re-arming the death action so the combined condition can fire
            // even if the first evaluation happened mid-transition.
            if (dead && deathClip != null)
                animator.SetTrigger(deathClip);
        }

        /// <summary>Hit-reaction entry point (NetworkPlayerHealth.RpcDamageReaction).</summary>
        public void PlayDamage()
        {
            if (dead || animator == null || !cachesValid) return;
            if (Time.time < nextDamageTime) return;
            nextDamageTime = Time.time + 0.4f;   // full-auto must not spam restarts
            animator.SetTrigger("Damage0" + Random.Range(1, 4));   // Damage01..03
        }

        // ---------------------------------------------------------------
        // TEAM MODEL SWITCH
        // ---------------------------------------------------------------

        private void UpdateTeamModel()
        {
            int team = combatant != null ? combatant.teamId : -1;
            if (team == activeTeam && (activeModel != null || ownerHidden)) return;
            activeTeam = team;

            if (ownerHidden) { activeModel = null; animator = null; return; }

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
                cachesValid = false;   // the new model's animator starts from scratch
            }
        }

        // ---------------------------------------------------------------
        // STATE EDGES
        // ---------------------------------------------------------------

        private void ResetDiffCaches()
        {
            dead = health != null && health.IsDead;
            sliding = controller != null && controller.sliding;
            reloading = gun != null && gun.reloading;
            aiming = gun != null && gun.aimAmount > 0.5f;
            shotCount = gun != null ? gun.shotCount : 0;
            jumpCount = controller != null ? controller.jumpCount : 0;
            smoothedVelocity = Vector3.zero;
            deathClip = null;
        }

        private void UpdateDeathState()
        {
            bool isDead = health != null && health.IsDead;
            if (isDead == dead) return;

            dead = isDead;
            if (dead)
            {
                bool prone = controller != null && controller.posture == PlayerPosture.Prone;
                deathClip = prone ? "ProneDeath" : "Death0" + Random.Range(1, 6);   // Death01..05
            }
            else
            {
                // Revive / redeploy: death clips park the state machine far from
                // locomotion; rebind cleanly and re-baseline the edge caches.
                animator.Rebind();
                cachesValid = false;
            }
        }

        private void UpdateActionEdges()
        {
            bool s = controller != null && controller.sliding;
            if (s != sliding)
            {
                sliding = s;
                if (s) animator.SetTrigger("RunSlide");
            }

            bool r = gun != null && gun.reloading;
            if (r != reloading)
            {
                reloading = r;
                if (r) animator.SetTrigger("Reload");
            }

            bool a = gun != null && gun.aimAmount > 0.5f;
            if (a != aiming)
            {
                aiming = a;
                if (a) animator.SetTrigger("Aim");
            }

            if (gun != null && gun.shotCount != shotCount)
            {
                shotCount = gun.shotCount;
                animator.SetTrigger("Shoot01");
            }

            if (controller != null && controller.jumpCount != jumpCount)
            {
                jumpCount = controller.jumpCount;
                animator.SetTrigger("Jump");
            }
        }

        private void SetPostureTrigger(PlayerPosture p)
        {
            switch (p)
            {
                case PlayerPosture.Crouch: animator.SetTrigger("Crouch"); break;
                case PlayerPosture.Prone: animator.SetTrigger("Prone"); break;
                default: animator.SetTrigger("StandUp"); break;
            }
        }

        private MoveState ComputeMoveState()
        {
            if (dead || sliding) return MoveState.None;               // RunSlide / death own the body

            Vector3 v = smoothedVelocity;
            v.y = 0f;
            float speed = v.magnitude;
            if (speed < WalkSpeed) return MoveState.None;

            var posture = controller != null ? controller.posture : PlayerPosture.Stand;
            if (posture == PlayerPosture.Prone) return MoveState.None;   // no prone-move clips

            // Strafing: lateral input dominates (relative to body yaw).
            Vector3 local = transform.InverseTransformDirection(v);
            if (Mathf.Abs(local.x) > Mathf.Abs(local.z) * StrafeBias)
                return local.x > 0f ? MoveState.StrafeR : MoveState.StrafeL;

            if (speed >= SprintSpeed) return MoveState.Sprint;
            if (speed >= RunSpeed) return MoveState.Run;
            return MoveState.Walk;
        }

        private void LateUpdate()
        {
            // Speed source: server reads the rigidbody directly; clients derive it
            // from the networked (interpolated) position so no extra sync is needed.
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

        private Vector3 lastNetworkPosition;
    }
}
