using System.Collections.Generic;
using Mirror;
using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// Server-authoritative equipment runtime (PHASE6). A single data-driven
    /// component shared by the player and the AI. Owns the equipment list (test
    /// phase: everything in one wheel list), per-equipment independent supply meters,
    /// ammo, cooldowns, EMP interference, and the channeled-use state machine.
    ///
    /// The owning controller (player or AI) feeds one <see cref="Tick"/> per
    /// simulation step with the selected equipment index and use/alt intent; this
    /// component dispatches to the right handler based on the definition's use style.
    /// </summary>
    [RequireComponent(typeof(NetworkIdentity))]
    public class NetworkEquipment : NetworkBehaviour, IEmpTarget
    {
        [Header("Equipment list (测试阶段全塞入)")]
        public List<EquipmentDefinition> equipmentList = new List<EquipmentDefinition>();

        [Header("References")]
        public NetworkPlayerController controller; // for dash force / shield multiplier

        [Header("Synced state")]
        [SyncVar] public int selection = -1;          // 当前选中装备索引
        [SyncVar] public int selectedAmmo;
        [SyncVar] public float selectedSupply;
        [SyncVar] public float empExposure;           // EMP 干扰剩余时间（>0 = 被干扰）
        [SyncVar(hook = nameof(OnShieldActiveChanged))] public bool shieldActive;
        [SyncVar] public bool channeling;

        // ---- per-equipment server-only state (indexed by equipmentList) ----
        private int[] ammo;
        private float[] supply;
        private float[] nextUseTime;
        private float[] ammoRegenAccum;

        // ---- channeled-use state (server-only) ----
        private float channelRemaining;
        private int channelIndex = -1;
        private EquipmentDefinition channelDef;
        private Vector3 channelEye;

        // ---- remote charges placed by signal/wired charge (server-only) ----
        private readonly List<RemoteChargeThrowable> placedCharges = new List<RemoteChargeThrowable>();

        // ---- edge detection (server-only) ----
        private bool prevUse;
        private bool prevUseAlt;

        // ---- blast shield instances (server collider + owner visual) ----
        private BlastShield serverShield;
        private BlastShield clientShield;

        public int Count => equipmentList != null ? equipmentList.Count : 0;
        public bool IsEmpDisabled => empExposure > 0f;

        public override void OnStartServer()
        {
            int n = Count;
            ammo = new int[n];
            supply = new float[n];
            nextUseTime = new float[n];
            ammoRegenAccum = new float[n];

            if (controller == null)
                controller = GetComponent<NetworkPlayerController>();

            for (int i = 0; i < n; i++)
            {
                var def = equipmentList[i];
                if (def != null)
                    ammo[i] = def.maxCarry;
            }
        }

        // ---------------------------------------------------------------
        // SELECTION / SUPPLY
        // ---------------------------------------------------------------

        [Server]
        public void Select(int index)
        {
            selection = index;
            SyncSelected();
        }

        private void SyncSelected()
        {
            if (selection >= 0 && selection < Count)
            {
                selectedAmmo = ammo[selection];
                selectedSupply = supply[selection];
            }
        }

        /// <summary>Add supply to the currently selected equipment's independent meter.</summary>
        [Server]
        public void GrantSupply(int amount)
        {
            if (selection < 0 || selection >= Count) return;
            var def = equipmentList[selection];
            if (def == null) return;

            supply[selection] += amount;

            // 到达成本 -> +1 并重置（每件装备独立补给度）。
            if (supply[selection] >= def.supplyCost)
            {
                if (ammo[selection] < def.maxCarry)
                    ammo[selection]++;
                supply[selection] = 0f;
            }

            SyncSelected();
        }

        // ---------------------------------------------------------------
        // TICK DISPATCH
        // ---------------------------------------------------------------

        [Server]
        public void Tick(int index, bool use, bool useAlt, Vector3 eye, Vector3 forward,
                         Vector3 move, bool grounded)
        {
            // Edge-triggered path (player): one press = one use/toggle.
            bool useEdge = use && !prevUse;
            bool useAltEdge = useAlt && !prevUseAlt;
            prevUse = use;
            prevUseAlt = useAlt;

            Dispatch(index, useEdge, useAltEdge, eye, forward, move, grounded);
        }

        /// <summary>
        /// Single-use path (AI): every call is treated as one use. The AI already
        /// gates its own cadence via an interval, so no edge latch is applied.
        /// </summary>
        [Server]
        public void Use(int index, bool useAlt, Vector3 eye, Vector3 forward,
                        Vector3 move, bool grounded)
        {
            Dispatch(index, true, useAlt, eye, forward, move, grounded);
        }

        private void Dispatch(int index, bool useEdge, bool useAltEdge, Vector3 eye,
                              Vector3 forward, Vector3 move, bool grounded)
        {
            if (index < 0 || index >= Count) return;
            var def = equipmentList[index];
            if (def == null) return;

            selection = index;
            SyncSelected();

            switch (def.useStyle)
            {
                case EquipmentUseStyle.Throw:
                    UseThrowable(def, index, useEdge, eye, forward);
                    break;
                case EquipmentUseStyle.BoltLauncher:
                    UseBoltLauncher(def, index, useEdge, eye, forward);
                    break;
                case EquipmentUseStyle.RemoteCharge:
                    UseRemoteCharge(def, index, useEdge, useAltEdge, eye, forward);
                    break;
                case EquipmentUseStyle.Deploy:
                    UseDeploy(def, index, useEdge, eye, forward);
                    break;
                case EquipmentUseStyle.SelfInstant:
                    UseSelfInstant(def, index, useEdge, move, grounded);
                    break;
                case EquipmentUseStyle.SelfChannel:
                    UseSelfChannel(def, index, useEdge);
                    break;
                case EquipmentUseStyle.TargetChannel:
                    UseTargetChannel(def, index, useEdge, eye);
                    break;
                case EquipmentUseStyle.ShieldToggle:
                    UseShieldToggle(def, useEdge);
                    break;
            }
        }

        // ---------------------------------------------------------------
        // USE HANDLERS
        // ---------------------------------------------------------------

        private void UseThrowable(EquipmentDefinition def, int i, bool use, Vector3 eye, Vector3 forward)
        {
            if (!use || ammo[i] <= 0) return;
            if (IsEmpDisabled && def.empVulnerable) return;
            if (def.throwablePrefab == null) return;

            ammo[i]--;
            SpawnThrowable(def, eye, forward);
            SyncSelected();
        }

        private void UseBoltLauncher(EquipmentDefinition def, int i, bool use, Vector3 eye, Vector3 forward)
        {
            if (!use || ammo[i] <= 0) return;
            if (Time.time < nextUseTime[i]) return;
            if (def.throwablePrefab == null) return;

            ammo[i]--;
            nextUseTime[i] = Time.time + def.boltTime;
            SpawnThrowable(def, eye, forward);
            SyncSelected();
        }

        private void UseRemoteCharge(EquipmentDefinition def, int i, bool use, bool useAlt,
                                     Vector3 eye, Vector3 forward)
        {
            // 0.5s cooldown shared between throw and detonate.
            if (Time.time < nextUseTime[i]) return;
            if (IsEmpDisabled && def.empVulnerable) return;

            if (useAlt)
            {
                // 右键投出不引爆。
                if (ammo[i] <= 0 || def.throwablePrefab == null) return;
                ammo[i]--;
                nextUseTime[i] = Time.time + 0.5f;

                var t = SpawnThrowable(def, eye, forward);
                var charge = t != null ? t.GetComponent<RemoteChargeThrowable>() : null;
                if (charge != null)
                    placedCharges.Add(charge);
            }
            else if (use)
            {
                // 左键引爆所有已投出的炸药。
                nextUseTime[i] = Time.time + 0.5f;
                for (int k = placedCharges.Count - 1; k >= 0; k--)
                {
                    var c = placedCharges[k];
                    if (c != null) c.Trigger();
                    placedCharges.RemoveAt(k);
                }
            }

            SyncSelected();
        }

        private void UseDeploy(EquipmentDefinition def, int i, bool use, Vector3 eye, Vector3 forward)
        {
            if (!use || ammo[i] <= 0) return;
            if (def.throwablePrefab == null) return;

            ammo[i]--;

            Vector3 pos = eye + forward * 0.8f;
            pos.y = Mathf.Max(pos.y, 0.15f);

            var go = Instantiate(def.throwablePrefab, pos, Quaternion.identity);
            NetworkServer.Spawn(go);
            SyncSelected();
        }

        private void UseSelfInstant(EquipmentDefinition def, int i, bool use, Vector3 move, bool grounded)
        {
            if (!use || Time.time < nextUseTime[i]) return;
            if (IsEmpDisabled && def.empVulnerable) return;
            if (!grounded || move.sqrMagnitude < 0.0001f) return;

            // move is the local WASD vector (x strafe / y forward) — convert to a
            // world-space horizontal direction before applying the dash impulse.
            Vector3 worldDir = transform.forward * move.y + transform.right * move.x;
            worldDir.y = 0f;
            if (worldDir.sqrMagnitude < 0.0001f) worldDir = transform.forward;
            worldDir.Normalize();

            if (controller != null)
                controller.Dash(worldDir);

            nextUseTime[i] = Time.time + def.dashCooldown;
        }

        private void UseSelfChannel(EquipmentDefinition def, int i, bool use)
        {
            if (channeling) return;
            if (!use || ammo[i] <= 0) return;

            ammo[i]--;
            channeling = true;
            channelRemaining = def.channelTime;
            channelIndex = i;
            channelDef = def;
            SyncSelected();
        }

        private void UseTargetChannel(EquipmentDefinition def, int i, bool use, Vector3 eye)
        {
            if (channeling) return;
            if (!use || ammo[i] <= 0) return;

            ammo[i]--;
            channeling = true;
            channelRemaining = def.channelTime;
            channelIndex = i;
            channelDef = def;
            channelEye = eye;
            SyncSelected();
        }

        private void UseShieldToggle(EquipmentDefinition def, bool use)
        {
            if (!use) return;

            shieldActive = !shieldActive;

            if (controller != null)
            {
                // Server-side blocking collider (child of the player, rotates with yaw).
                if (shieldActive && serverShield == null)
                    serverShield = BlastShield.Attach(controller.transform);
                else if (!shieldActive && serverShield != null)
                {
                    Destroy(serverShield.gameObject);
                    serverShield = null;
                }

                var health = controller.GetComponent<NetworkPlayerHealth>();
                if (health != null)
                {
                    health.explosionDamageMultiplier =
                        shieldActive ? (1f - def.shieldExplosionReduction) : 1f;
                }
            }
        }

        /// <summary>Owner-only shield visual: spawn/destroy a panel on the local camera.</summary>
        private void OnShieldActiveChanged(bool oldValue, bool newValue)
        {
            if (!isLocalPlayer) return;
            if (controller == null || controller.playerCamera == null) return;

            if (newValue && clientShield == null)
                clientShield = BlastShield.Attach(controller.playerCamera.transform);
            else if (!newValue && clientShield != null)
            {
                Destroy(clientShield.gameObject);
                clientShield = null;
            }
        }

        // ---------------------------------------------------------------
        // HELPERS
        // ---------------------------------------------------------------

        private NetworkThrowable SpawnThrowable(EquipmentDefinition def, Vector3 eye, Vector3 forward)
        {
            Vector3 dir = forward;
            if (dir.sqrMagnitude < 0.0001f) dir = transform.forward;
            dir.Normalize();

            var go = Instantiate(def.throwablePrefab, eye, Quaternion.LookRotation(dir));
            var t = go.GetComponent<NetworkThrowable>();
            if (t != null)
            {
                t.Launch(dir * def.throwSpeed);
                t.SetOwner(GetComponent<NetworkIdentity>());
            }

            var proj = go.GetComponentsInChildren<Collider>(true);
            foreach (var own in GetComponentsInChildren<Collider>(true))
                foreach (var p in proj)
                    Physics.IgnoreCollision(p, own, true);

            NetworkServer.Spawn(go);
            return t;
        }

        // ---------------------------------------------------------------
        // PER-FRAME UPDATE (server)
        // ---------------------------------------------------------------

        private void Update()
        {
            if (!isServer) return;
            float dt = Time.deltaTime;

            // EMP interference timer decays.
            if (empExposure > 0f)
                empExposure = Mathf.Max(0f, empExposure - dt);

            // Channeled-use completion.
            if (channeling)
            {
                channelRemaining -= dt;
                if (channelRemaining <= 0f)
                    CompleteChannel();
            }

            // Ammo regen (small supply pack / defibrillator / large crate).
            for (int i = 0; i < Count; i++)
            {
                var def = equipmentList[i];
                if (def == null || def.ammoRegenInterval <= 0f) continue;
                if (ammo[i] >= def.maxCarry) continue;

                ammoRegenAccum[i] += dt;
                if (ammoRegenAccum[i] >= def.ammoRegenInterval)
                {
                    ammoRegenAccum[i] -= def.ammoRegenInterval;
                    ammo[i] = Mathf.Min(def.maxCarry, ammo[i] + 1);
                    if (i == selection) SyncSelected();
                }
            }
        }

        private void CompleteChannel()
        {
            channeling = false;
            var def = channelDef;
            if (def == null) return;

            var health = controller != null ? controller.GetComponent<NetworkPlayerHealth>() : null;

            switch (def.type)
            {
                case EquipmentType.ArmorPlate:
                    if (health != null) health.AddArmor(def.armorGrant);
                    break;

                case EquipmentType.HealingSyringe:
                    if (health != null) health.StartBuffRegen(20f);
                    break;

                case EquipmentType.Defibrillator:
                    ReviveNearestFriendly(channelEye);
                    break;
            }

            channelIndex = -1;
            channelDef = null;
        }

        private void ReviveNearestFriendly(Vector3 center)
        {
            // 测试阶段：没有友方概念，除颤仪对所有实体（除使用者自身）生效。
            var self = GetComponent<NetworkPlayerHealth>();
            NetworkPlayerHealth best = null;
            float bestDist = 1f; // 1m radius

            foreach (var h in Object.FindObjectsOfType<NetworkPlayerHealth>())
            {
                if (h == null || !h.IsDead) continue;
                if (self != null && h == self) continue;   // 排除使用者自身

                float d = Vector3.Distance(center, h.transform.position);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = h;
                }
            }

            if (best != null)
                best.Rescue();
        }

        // ---------------------------------------------------------------
        // EMP
        // ---------------------------------------------------------------

        public void ApplyEmp(float duration)
        {
            empExposure = Mathf.Max(empExposure, duration);
        }
    }
}
