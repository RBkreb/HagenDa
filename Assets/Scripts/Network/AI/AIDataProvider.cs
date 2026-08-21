using System.Collections.Generic;
using Mirror;
using UnityEngine;

namespace HagenDa.Networking.AI
{
    /// <summary>
    /// PHASE9 AI data layer. Caches the world state every 0.5s so GOAP sensors do not
    /// call <c>FindObjectsOfType</c> per frame. Also exposes equipment queries, the
    /// squad leader / known-enemy resolution and the per-weapon shooting profile.
    ///
    /// Runs server-side only (the GOAP agents live on the server).
    /// </summary>
    [RequireComponent(typeof(NetworkIdentity))]
    public class AIDataProvider : NetworkBehaviour
    {
        [Header("Perception")]
        [Tooltip("直接视野感知距离（与 VisionSystem 一致）。")]
        public float senseRange = 50f;
        [Tooltip("受击判定窗口（IsUnderFire = 该时间内受过伤害）。")]
        public float underFireWindow = 3f;

        private const float RefreshInterval = 0.5f;
        private float refreshTimer;

        // ---- component refs ----
        private NetworkCombatant self;
        private NetworkPlayerHealth health;
        private NetworkGun gun;
        private NetworkEquipment equipment;

        // ---- cached world state ----
        private readonly List<NetworkCombatant> enemies = new List<NetworkCombatant>();
        private readonly List<NetworkCombatant> friendlies = new List<NetworkCombatant>();
        private NetworkCombatant nearestEnemy;
        private NetworkCombatant nearestMarkedEnemy;
        private NetworkCombatant squadLeader;
        private NetworkCombatant nearestDownedAlly;
        private NetworkCombatant nearestLowAmmoAlly;
        private LargeSupplyCrate nearestSupply;
        private CapturePoint nearestUncapturedPoint;
        private CapturePoint nearestOwnedPoint;
        private GarrisonZone nearestGarrison;
        private SensorProbe nearestFriendlySensor;
        private SensorProbe nearestEnemySensor;

        // ---- situational flags ----
        private bool enemyInSight;
        private bool atCapturePoint;
        private bool inGarrison;

        // ---- known enemies (direct vision + mark + intel broadcast) ----
        private readonly Dictionary<NetworkCombatant, float> knownEnemies = new Dictionary<NetworkCombatant, float>();

        // ---- shooting profile ----
        private AIShootingProfile shooting;

        public NetworkCombatant Combatant => self;
        public NetworkGun Gun => gun;
        public NetworkEquipment Equipment => equipment;
        public NetworkPlayerHealth Health => health;

        public override void OnStartServer()
        {
            self = GetComponent<NetworkCombatant>();
            health = GetComponent<NetworkPlayerHealth>();
            gun = GetComponent<NetworkGun>();
            equipment = GetComponent<NetworkEquipment>();

            shooting = new AIShootingProfile(gun != null ? gun.definition : null, gun != null ? gun.spreadMultiplier : 1f);

            RefreshCache();
        }

        private void Update()
        {
            if (!isServer) return;
            if (dead) return;

            refreshTimer += Time.deltaTime;
            if (refreshTimer >= RefreshInterval)
            {
                refreshTimer = 0f;
                RefreshCache();
            }
        }

        private bool dead => self != null && self.IsDead;

        // ---------------------------------------------------------------
        // CACHE
        // ---------------------------------------------------------------

        private void RefreshCache()
        {
            if (self == null) self = GetComponent<NetworkCombatant>();
            if (self == null) return;

            enemies.Clear();
            friendlies.Clear();
            nearestEnemy = null;
            nearestMarkedEnemy = null;
            squadLeader = null;
            nearestDownedAlly = null;
            nearestLowAmmoAlly = null;
            nearestSupply = null;
            nearestUncapturedPoint = null;
            nearestOwnedPoint = null;
            nearestGarrison = null;
            nearestFriendlySensor = null;
            nearestEnemySensor = null;
            enemyInSight = false;
            atCapturePoint = false;
            inGarrison = false;

            int myTeam = self.teamId;
            int mySquad = self.squadId;
            Vector3 myPos = transform.position;

            // Squad leader = same-squad combatant with the smallest instance ID.
            int leaderId = int.MaxValue;

            var combatants = Object.FindObjectsOfType<NetworkCombatant>();
            for (int i = 0; i < combatants.Length; i++)
            {
                var c = combatants[i];
                if (c == null || c == self) continue;

                bool hostile = myTeam >= 0 && c.teamId >= 0 && c.teamId != myTeam;
                bool ally = myTeam >= 0 && c.teamId == myTeam;

                if (hostile)
                {
                    if (c.IsDead) continue;
                    enemies.Add(c);

                    float d = (c.transform.position - myPos).sqrMagnitude;
                    if (nearestEnemy == null || d < (nearestEnemy.transform.position - myPos).sqrMagnitude)
                        nearestEnemy = c;
                    if (c.IsMarked && (nearestMarkedEnemy == null || d < (nearestMarkedEnemy.transform.position - myPos).sqrMagnitude))
                        nearestMarkedEnemy = c;

                    // Direct vision → known enemy + broadcast to squad.
                    if (VisionSystem.CanSee(transform, c.transform.position + Vector3.up * 0.8f))
                    {
                        enemyInSight = true;
                        knownEnemies[c] = Time.time;
                        IntelBroadcast.BroadcastSeenEnemy(self, c);
                    }
                }
                else if (ally)
                {
                    friendlies.Add(c);

                    if (mySquad >= 0 && c.squadId == mySquad)
                    {
                        int id = c.GetInstanceID();
                        if (id < leaderId)
                        {
                            leaderId = id;
                            squadLeader = c;
                        }
                    }

                    if (c.IsDead)
                    {
                        float d = (c.transform.position - myPos).sqrMagnitude;
                        if (nearestDownedAlly == null || d < (nearestDownedAlly.transform.position - myPos).sqrMagnitude)
                            nearestDownedAlly = c;
                    }
                    else if (c.GetComponent<NetworkGun>() != null && IsLowAmmo(c))
                    {
                        float d = (c.transform.position - myPos).sqrMagnitude;
                        if (nearestLowAmmoAlly == null || d < (nearestLowAmmoAlly.transform.position - myPos).sqrMagnitude)
                            nearestLowAmmoAlly = c;
                    }
                }
            }

            // Marked enemies are always "known" (even beyond direct vision).
            for (int i = 0; i < combatants.Length; i++)
            {
                var c = combatants[i];
                if (c == null || c == self || c.IsDead) continue;
                if (myTeam < 0 || c.teamId < 0 || c.teamId == myTeam) continue;
                if (c.IsMarked) knownEnemies[c] = Time.time;
            }

            // Scene targets.
            var supplies = Object.FindObjectsOfType<LargeSupplyCrate>();
            for (int i = 0; i < supplies.Length; i++)
            {
                if (supplies[i] == null) continue;
                if (nearestSupply == null || (supplies[i].transform.position - myPos).sqrMagnitude < (nearestSupply.transform.position - myPos).sqrMagnitude)
                    nearestSupply = supplies[i];
            }

            var sensors = Object.FindObjectsOfType<SensorProbe>();
            for (int i = 0; i < sensors.Length; i++)
            {
                var s = sensors[i];
                if (s == null) continue;
                if (s.OwnerTeam == myTeam && myTeam >= 0)
                {
                    if (nearestFriendlySensor == null) nearestFriendlySensor = s;
                }
                else if (s.OwnerTeam >= 0 && myTeam >= 0 && s.OwnerTeam != myTeam)
                {
                    if (nearestEnemySensor == null || (s.transform.position - myPos).sqrMagnitude < (nearestEnemySensor.transform.position - myPos).sqrMagnitude)
                        nearestEnemySensor = s;
                }
            }

            var mm = NetworkMatchManager.Instance;
            if (mm != null)
            {
                if (mm.capturePoints != null)
                {
                    for (int i = 0; i < mm.capturePoints.Count; i++)
                    {
                        var p = mm.capturePoints[i];
                        if (p == null) continue;

                        if (Vector3.Distance(p.transform.position, myPos) <= p.radius)
                            atCapturePoint = true;

                        bool mine = p.ownerTeam == myTeam;
                        bool contested = p.ownerTeam < 0 && Mathf.Abs(p.contention) > 0.1f;
                        if (!mine && !contested) continue;

                        if (mine && (nearestOwnedPoint == null || Closer(p.transform.position, nearestOwnedPoint.transform.position, myPos)))
                            nearestOwnedPoint = p;
                        if (contested && (nearestUncapturedPoint == null || Closer(p.transform.position, nearestUncapturedPoint.transform.position, myPos)))
                            nearestUncapturedPoint = p;
                    }
                }
                if (mm.garrisons != null)
                {
                    for (int i = 0; i < mm.garrisons.Count; i++)
                    {
                        var g = mm.garrisons[i];
                        if (g == null) continue;
                        if (g.teamId == myTeam)
                        {
                            if (Vector3.Distance(g.transform.position, myPos) <= g.radius)
                                inGarrison = true;
                            if (nearestGarrison == null || Closer(g.transform.position, nearestGarrison.transform.position, myPos))
                                nearestGarrison = g;
                        }
                    }
                }
            }

            PruneKnownEnemies();
        }

        private static bool Closer(Vector3 a, Vector3 b, Vector3 from)
        {
            return (a - from).sqrMagnitude < (b - from).sqrMagnitude;
        }

        private bool IsLowAmmo(NetworkCombatant c)
        {
            var g = c.GetComponent<NetworkGun>();
            if (g == null || g.definition == null) return false;
            int total = g.magAmmo + g.reserveAmmo;
            int max = g.definition.magazineCapacity + g.definition.reserveCapacity;
            return max > 0 && (float)total / max < 0.4f;
        }

        private void PruneKnownEnemies()
        {
            var toRemove = new List<NetworkCombatant>();
            foreach (var kv in knownEnemies)
            {
                if (kv.Key == null || kv.Key.IsDead || Time.time - kv.Value > IntelBroadcast.Expiry)
                    toRemove.Add(kv.Key);
            }
            for (int i = 0; i < toRemove.Count; i++)
                knownEnemies.Remove(toRemove[i]);
        }

        // ---------------------------------------------------------------
        // INTEL
        // ---------------------------------------------------------------

        public void ReceiveIntel(NetworkCombatant enemy)
        {
            if (enemy == null || enemy.IsDead) return;
            knownEnemies[enemy] = Time.time;
        }

        public bool HasKnownEnemy()
        {
            PruneKnownEnemies();
            return knownEnemies.Count > 0;
        }

        public NetworkCombatant GetNearestKnownEnemy()
        {
            PruneKnownEnemies();
            NetworkCombatant best = null;
            float bestDist = float.MaxValue;
            foreach (var kv in knownEnemies)
            {
                if (kv.Key == null) continue;
                float d = (kv.Key.transform.position - transform.position).sqrMagnitude;
                if (d < bestDist)
                {
                    bestDist = d;
                    best = kv.Key;
                }
            }
            return best;
        }

        // ---------------------------------------------------------------
        // ACCESSORS
        // ---------------------------------------------------------------

        public NetworkCombatant GetNearestEnemy() => nearestEnemy;
        public NetworkCombatant GetNearestMarkedEnemy() => nearestMarkedEnemy;
        public NetworkCombatant GetSquadLeader() => squadLeader;
        public NetworkCombatant GetNearestDownedAlly() => nearestDownedAlly;
        public NetworkCombatant GetNearestLowAmmoAlly() => nearestLowAmmoAlly;
        public LargeSupplyCrate GetNearestSupply() => nearestSupply;
        public CapturePoint GetNearestUncapturedPoint() => nearestUncapturedPoint;
        public CapturePoint GetNearestOwnedPoint() => nearestOwnedPoint;
        public GarrisonZone GetNearestGarrison() => nearestGarrison;
        public SensorProbe GetNearestFriendlySensor() => nearestFriendlySensor;
        public SensorProbe GetNearestEnemySensor() => nearestEnemySensor;

        public bool HasFriendlySensor() => nearestFriendlySensor != null;

        // ---- status ----

        public int GetHealthLevel()
        {
            if (health == null) return 0;
            return Mathf.RoundToInt(health.health / health.maxHealth * 100f);
        }

        public int GetArmorLevel()
        {
            if (health == null) return 0;
            return health.armor > 0f ? 100 : 0;   // armor is flat; treat any as full for decisions
        }

        public int GetAmmoLevel()
        {
            if (gun == null || gun.definition == null) return 0;
            int max = gun.definition.magazineCapacity + gun.definition.reserveCapacity;
            if (max <= 0) return 0;
            return Mathf.RoundToInt((gun.magAmmo + gun.reserveAmmo) / (float)max * 100f);
        }

        public bool IsReloading() => gun != null && gun.IsReloading;

        public bool IsUnderFire()
        {
            return health != null && Time.time - health.LastDamageTime <= underFireWindow;
        }

        public bool IsSelfMarked() => self != null && self.IsMarked;
        public bool IsMarkImmune() => self != null && self.markImmune;
        public bool IsEnemyInSight() => enemyInSight;
        public bool IsAtCapturePoint() => atCapturePoint;
        public bool IsInGarrison() => inGarrison;

        // ---- equipment ----

        /// <summary>Whether any loadout slot currently carries (and has ammo for) the given type.</summary>
        public bool HasEquipmentAmmo(EquipmentType type)
        {
            if (equipment == null) return false;
            for (int slot = 0; slot < 4; slot++)
            {
                var def = equipment.GetSlotDefinition(slot);
                if (def != null && def.type == type && equipment.GetSlotAmmo(slot) > 0)
                    return true;
            }
            return false;
        }

        /// <summary>Loadout slot index (0-3) carrying the given type, or -1.</summary>
        public int GetEquipmentSlot(EquipmentType type)
        {
            if (equipment == null) return -1;
            for (int slot = 0; slot < 4; slot++)
            {
                var def = equipment.GetSlotDefinition(slot);
                if (def != null && def.type == type)
                    return slot;
            }
            return -1;
        }

        /// <summary>Use an equipment item by type (single-use path, like the AI's old gating).</summary>
        public void UseEquipment(EquipmentType type, Vector3 eye, Vector3 forward)
        {
            if (equipment == null) return;
            int slot = GetEquipmentSlot(type);
            if (slot < 0) return;
            int index = equipment.GetSlotIndex(slot);
            equipment.Use(index, false, eye, forward, Vector3.zero, true);
        }

        // ---- shooting ----

        public AIShootingProfile Shooting => shooting;

        /// <summary>
        /// PHASE9 shooting rules derived from the equipped primary weapon's spread.
        /// Effective range = 10 / average-spread(degrees). AI ignores recoil (applyRecoil
        /// is set false on the AI gun) but still accumulates spread.
        /// </summary>
        public class AIShootingProfile
        {
            public readonly float hipSpreadAvg;
            public readonly float adsSpreadAvg;
            public readonly float hipEffectiveRange;
            public readonly float adsEffectiveRange;
            public readonly float spreadMultiplier;

            public AIShootingProfile(WeaponDefinition def, float spreadMult)
            {
                spreadMultiplier = spreadMult;
                if (def == null)
                {
                    hipSpreadAvg = 3f;
                    adsSpreadAvg = 0.5f;
                }
                else
                {
                    hipSpreadAvg = (def.hipSpreadMin + def.hipSpreadMax) * 0.5f * spreadMult;
                    adsSpreadAvg = (def.adsSpreadMin + def.adsSpreadMax) * 0.5f * spreadMult;
                }
                hipEffectiveRange = 10f / Mathf.Max(0.01f, hipSpreadAvg);
                adsEffectiveRange = 10f / Mathf.Max(0.01f, adsSpreadAvg);
            }

            /// <summary>Decide aim + fire intent for a target at the given distance.</summary>
            public (bool aim, bool fire) DecideShooting(float dist)
            {
                if (dist < hipEffectiveRange)
                    return (false, true);   // hip full-auto

                if (dist < adsEffectiveRange)
                    return (true, true);    // ADS full-auto

                return (true, ShouldBurst()); // ADS burst (fire in short windows)
            }

            /// <summary>Long-range burst: fire 0.2s, pause 0.4s, repeat (lets bloom recover).</summary>
            private bool ShouldBurst()
            {
                float cycle = Time.time % 0.6f;
                return cycle < 0.2f;
            }
        }
    }
}
