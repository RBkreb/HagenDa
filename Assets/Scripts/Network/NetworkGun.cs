using HagenDa.Animation.Rigging;
using Mirror;
using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// Server-authoritative firearm (PHASE5). A single data-driven gun runtime that
    /// both the player and the AI drive, sharing one <see cref="WeaponDefinition"/>.
    ///
    /// Owns: magazine/reserve ammo, fire-mode selection, fire-rate gating, spread
    /// bloom, screen recoil, reload (fast/slow/paused) and the per-shooter bullet
    /// pool. The owning controller (player or AI) feeds one <see cref="Tick"/> per
    /// simulation step with the current fire/aim intent; the gun computes the shot.
    ///
    /// Bullets are pooled (60 per shooter) and simulated server-side only; clients
    /// see the tracer via RPC. Hits keep PHASE4 penetration: a living entity takes
    /// damage and the bullet flies on, a non-entity stops it.
    /// </summary>
    [RequireComponent(typeof(NetworkIdentity))]
    public class NetworkGun : NetworkBehaviour
    {
        [Header("Configuration")]
        public WeaponDefinition definition;
        public GameObject bulletPrefab;
        public int bulletPoolCapacity = 60;

        [Header("References")]
        public NetworkPlayerController controller; // for the activeSlot weapon-slot check

        [Header("Third-person presentation (PHASE13)")]
        [Tooltip("第三人称枪模(挂到士兵 rig 的 WeaponAnchor;双手 IK + Aim 约束由 SoldierRigSetup 接管)")]
        public GameObject thirdPersonModelPrefab;

        private GameObject tpModel;
        private SoldierRigSetup boundRig;
        private Transform weaponBasis;      // 实体根下、与模型同级的枪械基准
        private NetworkSoldierAnimator soldierAnim;
        private NetworkPlayerHealth healthComp;

        private void Awake()
        {
            soldierAnim = GetComponent<NetworkSoldierAnimator>();
            healthComp = GetComponent<NetworkPlayerHealth>();
        }

        /// <summary>
        /// 第三人称枪模(PHASE14):胶囊根 -> [角色模型 + 枪械基准(同级)] -> 枪模。
        /// 枪械基准在动画层级之外,故双手 IK 目标/握把胶囊读到的是实时 Transform ——
        /// 手-枪可持续自主同步,且不随模型(含可能的自装配枪)进入 AnimationStream。
        /// 这里:确保基准与枪存在、剔除模型内自装配的枪、把枪绑到当前 rig、死亡/切槽显隐。
        /// </summary>
        private void Update()
        {
            if (soldierAnim == null) soldierAnim = GetComponent<NetworkSoldierAnimator>();
            var rig = soldierAnim != null ? soldierAnim.ActiveRig : null;

            bool desired = rig != null
                && (healthComp == null || !healthComp.IsDead)
                && (controller == null || controller.activeSlot == -1);

            if (rig == null || !desired)
            {
                if (tpModel != null && tpModel.activeSelf) tpModel.SetActive(false);
                return;
            }

            EnsureBasisAndGun();
            if (tpModel == null) return;

            // 先绑到实体层的枪,再剔除模型内的残留枪(避免剔除瞬间 IK 目标悬空)
            if (boundRig != rig)
            {
                boundRig = rig;
                rig.BindWeapon(tpModel);
            }
            StripModelGuns();

            if (!tpModel.activeSelf) tpModel.SetActive(true);
        }

        /// <summary>确保实体根下有 WeaponBasis + 一把枪(与角色模型同级)。</summary>
        private void EnsureBasisAndGun()
        {
            if (weaponBasis == null) weaponBasis = transform.Find("WeaponBasis");
            if (weaponBasis == null)
            {
                var go = new GameObject("WeaponBasis");
                go.transform.SetParent(transform, false);
                weaponBasis = go.transform;
            }

            if (tpModel == null && weaponBasis.childCount > 0)
            {
                // 已在基准下摆好枪(预制体装配)
                for (int i = 0; i < weaponBasis.childCount; i++)
                {
                    var c = weaponBasis.GetChild(i);
                    if (c.GetComponentInChildren<MeshRenderer>(true) != null) { tpModel = c.gameObject; break; }
                }
            }
            if (tpModel == null && thirdPersonModelPrefab != null)
            {
                tpModel = Instantiate(thirdPersonModelPrefab, weaponBasis, false);
                tpModel.transform.localPosition = Vector3.zero;
                tpModel.transform.localRotation = Quaternion.identity;
                tpModel.transform.localScale = Vector3.one;
            }
        }

        /// <summary>
        /// 剔除角色模型内的枪(PHASE14:枪只能挂在实体层的 WeaponBasis 下)。
        /// 网络胶囊体预设可能自装配一把枪,遗留会变成动画层级内的第二把枪。
        /// </summary>
        private void StripModelGuns()
        {
            var soldier = soldierAnim != null ? soldierAnim.ActiveModel : null;
            if (soldier == null) return;
            var doomed = new System.Collections.Generic.List<GameObject>();
            foreach (var t in soldier.GetComponentsInChildren<Transform>(true))
            {
                if (t == soldier.transform || t == transform) continue;
                if (t.name == "M4_8" && t.parent != null && t.parent != weaponBasis)
                    doomed.Add(t.gameObject);
            }
            foreach (var g in doomed)
            {
                if (g == tpModel) continue;
                g.SetActive(false);      // 保留引用安全:隐藏而非销毁(仍可能是 rig 目标源)
            }
        }

        // ---- Server-authoritative synced state ----
        [SyncVar] public int magAmmo;
        [SyncVar] public int reserveAmmo;
        [SyncVar] public int fireModeIndex;
        [SyncVar] public float recoil;             // current screen recoil (degrees)
        [SyncVar] public float bloom;               // current spread half-angle (degrees)
        [SyncVar] public float aimAmount;           // 0 = hip, 1 = fully aimed
        [SyncVar] public bool reloading;
        [SyncVar] public bool reloadPaused;
        [SyncVar] public uint shotCount;   // PHASE12: 开枪计数（NetworkSoldierAnimator 检测增量播 Shoot01）
        [SyncVar] public bool sprinting;   // PHASE14: 冲刺持枪（枪械表现切 SprintAim + 摆枪）

        private enum ReloadState { Idle, Reloading, Paused }

        // ---- Per-tick intent (fed by the owning controller) ----
        private bool fireHeld;
        private bool aimHeld;
        private Vector3 aimOrigin;
        private Vector3 aimForward;

        // ---- Server runtime ----
        private BulletPool pool;
        private float nextFireTime;
        private bool prevFireHeld;
        private int burstRemaining;
        private float boltCooldownEnd;
        private float currentMinSpread;
        private float currentMaxSpread;
        private ReloadState reloadState = ReloadState.Idle;
        private float reloadRemaining;
        private float reloadDuration;

        public WeaponDefinition Definition => definition;

        public override void OnStartServer()
        {
            magAmmo = definition != null ? definition.magazineCapacity : 0;
            reserveAmmo = definition != null ? definition.reserveCapacity : 0;
            fireModeIndex = 0;
            bloom = 0f;
            recoil = 0f;
            aimAmount = 0f;

            if (bulletPrefab != null)
                pool = new BulletPool(bulletPrefab, bulletPoolCapacity, transform);
        }

        /// <summary>
        /// Advance the gun one simulation step. Called by the player controller in
        /// its FixedUpdate (60 Hz) and by the AI controller in its Update.
        /// </summary>
        [Server]
        public void Tick(bool fire, bool aim, Vector3 origin, Vector3 forward, bool sprint)
        {            fireHeld = fire;
            aimHeld = aim;
            aimOrigin = origin;
            aimForward = forward;
            sprinting = sprint;

            if (definition == null) return;

            float dt = Time.deltaTime;

            UpdateAim(dt);
            UpdateBloom(dt);
            UpdateRecoil(dt);
            UpdateReload(dt);
            UpdateFire();

            // Empty magazine with reserve remaining: auto-trigger a (slow) reload.
            if (reloadState == ReloadState.Idle && magAmmo <= 0 && reserveAmmo > 0)
                Reload();
        }

        /// <summary>
        /// PHASE8 重新部署：重置弹匣/备弹到满、清空散布/后座/瞄准/换弹状态。
        /// </summary>
        [Server]
        public void ResetForRedeploy()
        {
            if (definition != null)
            {
                magAmmo = definition.magazineCapacity;
                reserveAmmo = definition.reserveCapacity;
            }
            bloom = 0f;
            recoil = 0f;
            aimAmount = 0f;
            reloadState = ReloadState.Idle;
            reloading = false;
            reloadPaused = false;
            reloadRemaining = 0f;
            nextFireTime = 0f;
            burstRemaining = 0;
            boltCooldownEnd = 0f;
        }

        // ---------------------------------------------------------------
        // FIRE MODES / AMMO / RELOAD
        // ---------------------------------------------------------------

        [Server]
        public void SwitchFireMode()
        {
            var modes = definition != null ? definition.fireModes : null;
            if (modes == null || modes.Count == 0) return;
            fireModeIndex = (fireModeIndex + 1) % modes.Count;
        }

        [Server]
        public void Reload()
        {
            if (reloadState != ReloadState.Idle) return;      // already reloading / paused
            if (definition == null) return;
            if (magAmmo >= definition.magazineCapacity) return; // magazine full
            if (reserveAmmo <= 0) return;                       // no reserve -> reload disabled

            reloadDuration = magAmmo > 0 ? definition.reloadFastTime : definition.reloadSlowTime;
            reloadRemaining = reloadDuration;
            reloadState = ReloadState.Reloading;
            reloading = true;
            reloadPaused = false;
        }

        /// <summary>Add reserve ammo (补给箱/补给包). Capped at reserveCapacity.</summary>
        [Server]
        public void AddReserveAmmo(int amount)
        {
            if (definition == null) return;
            reserveAmmo = Mathf.Min(definition.reserveCapacity, reserveAmmo + amount);
        }

        /// <summary>Freeze the reload timer (weapon switch / death). Keeps the gun usable later.</summary>
        [Server]
        public void PauseReload()
        {
            if (reloadState != ReloadState.Reloading) return;
            reloadState = ReloadState.Paused;
            reloadPaused = true;
        }

        /// <summary>Resume a paused reload (weapon re-equipped / rescue).</summary>
        [Server]
        public void ResumeReload()
        {
            if (reloadState != ReloadState.Paused) return;
            reloadState = ReloadState.Reloading;
            reloadPaused = false;
        }

        public bool IsReloading => reloadState != ReloadState.Idle;

        private FireMode CurrentFireMode()
        {
            var modes = definition.fireModes;
            if (modes == null || modes.Count == 0) return FireMode.Auto;
            int idx = ((fireModeIndex % modes.Count) + modes.Count) % modes.Count;
            return modes[idx];
        }

        // ---------------------------------------------------------------
        // PER-TICK UPDATES
        // ---------------------------------------------------------------

        private void UpdateAim(float dt)
        {
            float target = aimHeld ? 1f : 0f;
            if (definition.aimTime <= 0f)
                aimAmount = target;
            else
                aimAmount = Mathf.MoveTowards(aimAmount, target, (1f / definition.aimTime) * dt);
        }

        private void UpdateBloom(float dt)
        {
            currentMinSpread = Mathf.Lerp(definition.hipSpreadMin, definition.adsSpreadMin, aimAmount);
            currentMaxSpread = Mathf.Lerp(definition.hipSpreadMax, definition.adsSpreadMax, aimAmount);

            // Continuous recovery down to the current state's minimum spread.
            bloom = Mathf.Max(currentMinSpread, bloom - definition.spreadRecovery * dt);
        }

        private void UpdateRecoil(float dt)
        {
            recoil = Mathf.Max(0f, recoil - definition.recoilRecovery * dt);
        }

        private void UpdateReload(float dt)
        {
            if (reloadState != ReloadState.Reloading) return;
            reloadRemaining -= dt;
            if (reloadRemaining <= 0f)
                CompleteReload();
        }

        private void CompleteReload()
        {
            int need = definition.magazineCapacity - magAmmo;
            int take = Mathf.Min(need, reserveAmmo);
            magAmmo += take;
            reserveAmmo -= take;

            reloadState = ReloadState.Idle;
            reloading = false;
            reloadPaused = false;
        }

        private void UpdateFire()
        {
            if (reloadState != ReloadState.Idle)
            {
                prevFireHeld = fireHeld;
                return;
            }

            bool blocked = sprinting; // cannot fire while sprinting (sprint-fire cooldown)
            bool edge = fireHeld && !prevFireHeld;
            prevFireHeld = fireHeld;

            if (blocked)
            {
                // Re-arm the trigger while blocked so a held press fires once the
                // sprint cooldown (handled by the controller) lifts.
                prevFireHeld = false;
                return;
            }

            FireMode mode = CurrentFireMode();

            switch (mode)
            {
                case FireMode.Auto:
                    if (fireHeld && Time.time >= nextFireTime)
                        TryShoot();
                    break;

                case FireMode.Semi:
                    if (edge && Time.time >= nextFireTime)
                        TryShoot();
                    break;

                case FireMode.Burst:
                    if (edge) burstRemaining = definition.burstCount;
                    if (burstRemaining > 0 && Time.time >= nextFireTime)
                    {
                        TryShoot();
                        burstRemaining--;
                    }
                    break;

                case FireMode.Bolt:
                    if (edge && Time.time >= boltCooldownEnd)
                    {
                        TryShoot();
                        boltCooldownEnd = Time.time + definition.boltTime;
                    }
                    break;
            }
        }

        private void TryShoot()
        {
            if (magAmmo <= 0) return;
            if (reloadState != ReloadState.Idle) return;

            magAmmo--;
            nextFireTime = Time.time + definition.FireInterval;

            // Spread grows per shot (clamped to the current max), recoil accumulates.
            bloom = Mathf.Min(currentMaxSpread, bloom + definition.spreadPerShot);
            recoil += definition.screenRecoilPerShot;

            Vector3 dir = ComputeFireDirection(aimForward, recoil, bloom);
            SpawnBullet(aimOrigin, dir);
            shotCount++;   // PHASE12: 动画同步（服务端权威计数）

            // S1 评估统计（服务器端，评估场景中才有）。
            if (NetworkServer.active)
            {
                var eval = Object.FindObjectOfType<S1EvaluationStats>();
                if (eval != null) eval.RecordShot();
            }
        }

        private void SpawnBullet(Vector3 origin, Vector3 dir)
        {
            if (pool == null) return;
            var bullet = pool.Get();
            if (bullet == null) return; // pool exhausted
            bullet.Fire(origin, dir, definition, this);
        }

        private Vector3 ComputeFireDirection(Vector3 forward, float recoilAmount, float bloomDeg)
        {
            Vector3 dir = forward.normalized;
            if (dir.sqrMagnitude < 0.0001f) dir = transform.forward;

            // Screen recoil: rotate in the shooter's view plane. 0° = right, 90° = up.
            if (recoilAmount > 0f)
            {
                float angleRad = definition.screenRecoilAngle * Mathf.Deg2Rad;
                float recoilRight = recoilAmount * Mathf.Cos(angleRad); // yaw right
                float recoilUp    = recoilAmount * Mathf.Sin(angleRad); // pitch up

                if (Mathf.Abs(recoilRight) > 0.0001f)
                    dir = Quaternion.AngleAxis(recoilRight, Vector3.up) * dir;

                if (Mathf.Abs(recoilUp) > 0.0001f)
                {
                    Vector3 right = Vector3.Cross(Vector3.up, dir);
                    if (right.sqrMagnitude < 0.0001f) right = Vector3.right;
                    right.Normalize();
                    dir = Quaternion.AngleAxis(-recoilUp, right) * dir;
                }
            }

            if (bloomDeg > 0.0001f)
                dir = ApplySpread(dir, bloomDeg);

            return dir.normalized;
        }

        private static Vector3 ApplySpread(Vector3 forward, float bloomDeg)
        {
            Vector3 f = forward.normalized;
            float maxRad = bloomDeg * Mathf.Deg2Rad;
            float r = Random.value;
            float angle = Mathf.Acos(1f - r * (1f - Mathf.Cos(maxRad))); // uniform solid angle
            float theta = Random.Range(0f, Mathf.PI * 2f);

            Vector3 right = Vector3.Cross(f, Vector3.up);
            if (right.sqrMagnitude < 0.0001f) right = Vector3.right;
            right.Normalize();
            Vector3 up = Vector3.Cross(right, f);

            return f * Mathf.Cos(angle) + (right * Mathf.Cos(theta) + up * Mathf.Sin(theta)) * Mathf.Sin(angle);
        }

        // ---------------------------------------------------------------
        // HIT FEEDBACK (moved from NetworkCombat)
        // ---------------------------------------------------------------

        [Server]
        public void NotifyHit(bool headshot, bool kill)
        {
            if (connectionToClient == null) return;
            TargetRpcHitmarker(headshot, kill);
        }

        [TargetRpc]
        private void TargetRpcHitmarker(bool headshot, bool kill)
        {
            HitKind kind = kill ? HitKind.Kill : (headshot ? HitKind.Headshot : HitKind.Normal);
            GameHud.Instance?.ShowHitmarker(kind);
        }

        [Server]
        public void NotifyImpact(Vector3 pos)
        {
            RpcImpactSmoke(pos);
        }

        [ClientRpc]
        private void RpcImpactSmoke(Vector3 pos)
        {
            NetworkSmoke.Spawn(pos, 0.6f, 0.3f, 0.4f);
        }
    }
}
