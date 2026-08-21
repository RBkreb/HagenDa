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

        [Header("Loadout slots (PHASE8)")]
        [Tooltip("槽位 → 库存索引。0=可选1, 1=可选2, 2=特有, 3=投掷物.")]
        [SyncVar] public int opt1Index = -1;
        [SyncVar] public int opt2Index = -1;
        [SyncVar] public int specialIndex = -1;
        [SyncVar] public int throwableIndex = -1;

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

        // PHASE8 配备互斥：该实体已部署到世界中的配备（按类型限制上限）。
        private readonly List<DeployableSlot> placedDeployables = new List<DeployableSlot>();

        // ---- edge detection (server-only) ----
        private bool prevUse;
        private bool prevUseAlt;

        // ---- blast shield instances (server collider + owner visual) ----
        private BlastShield serverShield;
        private BlastShield clientShield;

        // PHASE8: 5 个配装槽位的剩余次数同步（客户端 HUD 读取）。
        public readonly SyncList<int> slotAmmo = new SyncList<int>();

        // PHASE8: 瞬发型装备（快速机动装置）冷却剩余秒数（客户端 HUD 显示）。
        [SyncVar] public float dashCooldownRemaining;

        // PHASE8 干扰器：免疫标记剩余秒数 + 冷却剩余秒数。
        [SyncVar] public float jammerImmuneRemaining;
        [SyncVar] public float jammerCooldownRemaining;

        // 干扰器免疫时长 / 冷却时长。
        public const float JammerImmuneDuration = 30f;
        public const float JammerCooldownDuration = 30f;

        public int Count => equipmentList != null ? equipmentList.Count : 0;
        public bool IsEmpDisabled => empExposure > 0f;

        /// <summary>槽位剩余使用次数。服务器读权威数组，客户端读同步列表。</summary>
        public int GetSlotAmmo(int slot)
        {
            if (slot < 0 || slot > 4) return 0;
            if (isServer)
            {
                int i = GetSlotIndex(slot);
                if (i < 0 || ammo == null || i >= ammo.Length) return 0;
                return ammo[i];
            }
            if (slotAmmo == null || slot >= slotAmmo.Count) return 0;
            return slotAmmo[slot];
        }

        /// <summary>把 5 个槽位的剩余次数同步给客户端（服务器在弹药变化后调用）。</summary>
        [Server]
        private void SyncSlotAmmo()
        {
            if (slotAmmo == null) return;
            while (slotAmmo.Count < 5) slotAmmo.Add(0);
            for (int s = 0; s < 5; s++)
            {
                int i = GetSlotIndex(s);
                slotAmmo[s] = (i >= 0 && ammo != null && i < ammo.Length) ? ammo[i] : 0;
            }
        }

        /// <summary>槽位是否还有剩余次数（服务器端；剩余为 0 不能切换）。</summary>
        public bool HasAmmoInSlot(int slot)
        {
            return GetSlotAmmo(slot) > 0;
        }

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

            // PHASE8: 默认配装（可选1=快速机动, 可选2=护甲板, 特有=治疗针, 投掷=手雷）。
            opt1Index = IndexOfType(EquipmentType.QuickDash);
            opt2Index = IndexOfType(EquipmentType.ArmorPlate);
            specialIndex = IndexOfType(EquipmentType.HealingSyringe);
            throwableIndex = IndexOfType(EquipmentType.Grenade);

            SyncSlotAmmo();
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

        // ---------------------------------------------------------------
        // PHASE8 LOADOUT SLOTS
        // ---------------------------------------------------------------

        /// <summary>Slot id (0=可选1, 1=可选2, 2=特有, 3=投掷物) → inventory index.</summary>
        public int GetSlotIndex(int slot)
        {
            switch (slot)
            {
                case 0: return opt1Index;
                case 1: return opt2Index;
                case 2: return specialIndex;
                case 3: return throwableIndex;
                default: return -1;
            }
        }

        public EquipmentDefinition GetSlotDefinition(int slot)
        {
            int i = GetSlotIndex(slot);
            return i >= 0 && i < Count ? equipmentList[i] : null;
        }

        /// <summary>
        /// Handle a slot key press (1/3/4/G/Z). Returns true if the slot's item was
        /// an instant-use item (used immediately, no active-slot switch); false if it
        /// is a normal item (the slot becomes the active selection).
        /// </summary>
        [Server]
        public bool HandleSlotKey(int slot, Vector3 eye, Vector3 forward,
                                  Vector3 move, bool grounded)
        {
            var def = GetSlotDefinition(slot);
            if (def == null) return false;

            int idx = GetSlotIndex(slot);
            if (def.instantUse)
            {
                Use(idx, false, eye, forward, move, grounded);
                return true;
            }

            Select(idx);
            return false;
        }

        /// <summary>Server: apply a validated loadout to the synced slot indices.</summary>
        [Server]
        public void ApplyLoadout(LoadoutDefinition loadout)
        {
            if (loadout == null) return;
            opt1Index = ClampIndex(loadout.optional1);
            opt2Index = ClampIndex(loadout.optional2);
            specialIndex = ClampIndex(loadout.special);
            throwableIndex = ClampIndex(loadout.throwable);
            SyncSlotAmmo();
        }

        /// <summary>
        /// PHASE8 重新部署：重置所有配备弹药到携带上限、清空补给度与冷却、
        /// 清除已投出的遥控炸药、关闭防爆盾与引导。
        /// </summary>
        [Server]
        public void ResetForRedeploy()
        {
            for (int i = 0; i < Count; i++)
            {
                var def = equipmentList[i];
                if (def == null) continue;
                ammo[i] = def.maxCarry;
                supply[i] = 0f;
                nextUseTime[i] = 0f;
                ammoRegenAccum[i] = 0f;
            }

            // 清除已投出的信号/线控炸药。
            for (int k = placedCharges.Count - 1; k >= 0; k--)
            {
                var c = placedCharges[k];
                if (c != null && c.gameObject != null)
                    NetworkServer.Destroy(c.gameObject);
                placedCharges.RemoveAt(k);
            }

            // 清除该实体已部署的配备（大型补给箱/拦截/感应器等）。
            for (int k = placedDeployables.Count - 1; k >= 0; k--)
            {
                var d = placedDeployables[k];
                if (d != null && d.gameObject != null)
                    NetworkServer.Destroy(d.gameObject);
                placedDeployables.RemoveAt(k);
            }

            // 关闭防爆盾（服务器碰撞体）。
            if (serverShield != null)
            {
                Destroy(serverShield.gameObject);
                serverShield = null;
            }
            shieldActive = false;
            var health = controller != null ? controller.GetComponent<NetworkPlayerHealth>() : null;
            if (health != null)
                health.explosionDamageMultiplier = 1f;

            channeling = false;
            channelIndex = -1;
            channelDef = null;
            dashCooldownRemaining = 0f;
            jammerImmuneRemaining = 0f;
            jammerCooldownRemaining = 0f;
            var combatant = GetComponent<NetworkCombatant>();
            if (combatant != null) combatant.markImmune = false;

            SyncSelected();
            SyncSlotAmmo();
        }

        /// <summary>Server: validate a loadout against the inventory categories.</summary>
        [Server]
        public bool ValidateLoadout(LoadoutDefinition loadout, out string reason)
        {
            return LoadoutRules.Validate(loadout, equipmentList, out reason);
        }

        private bool IsCategory(int idx, EquipmentCategory cat)
        {
            return LoadoutRules.IsCategory(equipmentList, idx, cat);
        }

        private int ClampIndex(int idx)
        {
            return idx >= 0 && idx < Count ? idx : -1;
        }

        private int IndexOfType(EquipmentType type)
        {
            for (int i = 0; i < Count; i++)
                if (equipmentList[i] != null && equipmentList[i].type == type)
                    return i;
            return -1;
        }

        private void SyncSelected()
        {
            if (selection >= 0 && selection < Count)
            {
                selectedAmmo = ammo[selection];
                selectedSupply = supply[selection];
            }
        }

        // ---------------------------------------------------------------
        // PHASE8 配备互斥（部署上限）
        // ---------------------------------------------------------------

        /// <summary>
        /// 登记一个部署到世界中的配备。若该类型已超出 <see cref="EquipmentDefinition.deployCap"/>，
        /// 摧毁最早部署的同类型配备（先进先出）。
        /// </summary>
        [Server]
        public void RegisterDeployable(GameObject go, EquipmentDefinition def)
        {
            if (go == null || def == null || def.deployCap <= 0) return;

            var slot = go.GetComponent<DeployableSlot>();
            if (slot == null) slot = go.AddComponent<DeployableSlot>();
            slot.Init(GetComponent<NetworkIdentity>(), def.type);

            PrunePlacedDeployables();

            placedDeployables.Add(slot);

            // 超出上限：摧毁最早部署的（同类型）。
            int count = 0;
            foreach (var d in placedDeployables)
                if (d != null && d.type == def.type)
                    count++;

            while (count > def.deployCap)
            {
                DeployableSlot oldest = null;
                foreach (var d in placedDeployables)
                {
                    if (d == null || d.type != def.type) continue;
                    if (oldest == null || d.deployTime < oldest.deployTime)
                        oldest = d;
                }
                if (oldest == null) break;

                placedDeployables.Remove(oldest);
                if (oldest.gameObject != null)
                    NetworkServer.Destroy(oldest.gameObject);
                count--;
            }
        }

        /// <summary>移除已销毁的部署记录。</summary>
        private void PrunePlacedDeployables()
        {
            for (int i = placedDeployables.Count - 1; i >= 0; i--)
            {
                if (placedDeployables[i] == null)
                    placedDeployables.RemoveAt(i);
            }
        }

        /// <summary>对**所有**配备同时增加补给度，每件独立判定是否到达成本。</summary>
        [Server]
        public void GrantSupply(int amount)
        {
            for (int i = 0; i < Count; i++)
            {
                var def = equipmentList[i];
                if (def == null) continue;

                supply[i] += amount;

                // 到达成本 -> +1 并重置（每件装备独立补给度）。
                if (supply[i] >= def.supplyCost)
                {
                    if (ammo[i] < def.maxCarry)
                        ammo[i]++;
                    supply[i] = 0f;
                }
            }

            SyncSelected();
            SyncSlotAmmo();
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
            var t = SpawnThrowable(def, eye, forward);
            // 小型补给包：计入部署上限互斥。
            if (t != null && def.deployCap > 0)
                RegisterDeployable(t.gameObject, def);
            SyncSelected();
            SyncSlotAmmo();
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
            SyncSlotAmmo();
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
                // 信号/线控炸药：计入部署上限互斥。
                if (t != null && def.deployCap > 0)
                    RegisterDeployable(t.gameObject, def);
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
            SyncSlotAmmo();
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

            // 感应器：传入部署者队伍（放置物无 NetworkCombatant）。
            var sensor = go.GetComponent<SensorProbe>();
            if (sensor != null)
            {
                var my = GetComponent<NetworkCombatant>();
                if (my != null) sensor.SetOwnerTeam(my.teamId);
            }

            // 大型补给箱/拦截装置/感应器：计入部署上限互斥。
            if (def.deployCap > 0)
                RegisterDeployable(go, def);

            SyncSelected();
            SyncSlotAmmo();
        }

        private void UseSelfInstant(EquipmentDefinition def, int i, bool use, Vector3 move, bool grounded)
        {
            if (!use || Time.time < nextUseTime[i]) return;
            if (IsEmpDisabled && def.empVulnerable) return;

            // PHASE8 干扰器：清除当前标记 + 30s 免疫，免疫结束后 30s 冷却恢复 1 次。
            if (def.type == EquipmentType.Jammer)
            {
                if (ammo[i] <= 0 || jammerImmuneRemaining > 0f || jammerCooldownRemaining > 0f)
                    return;

                ammo[i]--;
                SyncSelected();
                SyncSlotAmmo();

                // 清除现有标记。
                var combatant = GetComponent<NetworkCombatant>();
                if (combatant != null)
                    combatant.ClearMark();

                // 免疫标记 30s（同步给 NetworkCombatant 供 SetMarked 拦截）。
                jammerImmuneRemaining = JammerImmuneDuration;
                if (combatant != null)
                    combatant.markImmune = true;
                return;
            }

            // move is the local WASD vector (x strafe / y forward) — convert to a
            // world-space horizontal direction. No input defaults to forward.
            Vector3 worldDir = transform.forward * move.y + transform.right * move.x;
            worldDir.y = 0f;
            if (worldDir.sqrMagnitude < 0.0001f) worldDir = transform.forward;
            worldDir.Normalize();

            if (controller != null)
                controller.Dash(worldDir);

            nextUseTime[i] = Time.time + def.dashCooldown;
            dashCooldownRemaining = def.dashCooldown;   // 同步冷却倒计时给 HUD
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
            SyncSlotAmmo();
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
            SyncSlotAmmo();
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

            // PHASE8 配备互斥：清理已销毁的部署记录。
            PrunePlacedDeployables();

            // EMP interference timer decays.
            if (empExposure > 0f)
                empExposure = Mathf.Max(0f, empExposure - dt);

            // 快速机动装置冷却倒计时（HUD 显示）。
            if (dashCooldownRemaining > 0f)
                dashCooldownRemaining = Mathf.Max(0f, dashCooldownRemaining - dt);

            // PHASE8 干扰器状态机：免疫 → 冷却 → 恢复 1 次使用。
            var combatant = GetComponent<NetworkCombatant>();
            if (jammerImmuneRemaining > 0f)
            {
                jammerImmuneRemaining = Mathf.Max(0f, jammerImmuneRemaining - dt);
                if (jammerImmuneRemaining <= 0f)
                {
                    // 免疫结束 → 进入冷却。
                    if (combatant != null)
                        combatant.markImmune = false;
                    jammerCooldownRemaining = JammerCooldownDuration;
                }
            }
            else if (jammerCooldownRemaining > 0f)
            {
                jammerCooldownRemaining = Mathf.Max(0f, jammerCooldownRemaining - dt);
                if (jammerCooldownRemaining <= 0f)
                {
                    // 冷却结束 → 恢复 1 次使用。
                    int j = IndexOfType(EquipmentType.Jammer);
                    if (j >= 0 && ammo != null && j < ammo.Length)
                    {
                        ammo[j] = Mathf.Min(1, ammo[j] + 1);
                        SyncSelected();
                        SyncSlotAmmo();
                    }
                }
            }

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
                    SyncSlotAmmo();
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
                    ReviveNearestFriendly(transform.position);
                    break;
            }

            channelIndex = -1;
            channelDef = null;
        }

        private void ReviveNearestFriendly(Vector3 center)
        {
            // 测试阶段：没有友方概念，除颤仪对所有死亡实体（除使用者自身）生效。
            var self = GetComponent<NetworkPlayerHealth>();
            NetworkPlayerHealth best = null;
            float bestDist = 2f; // 2m radius

            var combatants = HagenDa.Networking.AI.CombatantRegistry.AllCombatants;
            for (int i = 0; i < combatants.Count; i++)
            {
                var c = combatants[i];
                if (c == null) continue;
                var h = c.Health;
                if (h == null || !h.IsDead) continue;
                if (self != null && h == self) continue;   // 排除使用者自身

                float d = Vector3.Distance(center, c.transform.position);
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

            // PHASE8 干扰器：EMP 干扰可提前终止免疫状态 → 进入冷却。
            if (jammerImmuneRemaining > 0f)
            {
                jammerImmuneRemaining = 0f;
                var combatant = GetComponent<NetworkCombatant>();
                if (combatant != null) combatant.markImmune = false;
                jammerCooldownRemaining = JammerCooldownDuration;
            }
        }
    }
}
