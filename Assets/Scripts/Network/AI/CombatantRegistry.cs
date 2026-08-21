using System.Collections.Generic;
using UnityEngine;

namespace HagenDa.Networking.AI
{
    /// <summary>
    /// Global O(1) registry for all combat-related entities. Replaces per-AI
    /// <c>FindObjectsOfType</c> calls (59 AI × 2/s = 118 calls/s → 0 calls/s).
    ///
    /// Entities self-register in OnEnable / OnStartServer and unregister in
    /// OnDisable / OnDestroy. The registry prunes null entries lazily on read.
    /// </summary>
    public static class CombatantRegistry
    {
        private static readonly List<NetworkCombatant> combatants = new List<NetworkCombatant>(128);
        private static readonly List<LargeSupplyCrate> supplyCrates = new List<LargeSupplyCrate>(32);
        private static readonly List<SensorProbe> sensors = new List<SensorProbe>(32);
        private static readonly List<AIDataProvider> providers = new List<AIDataProvider>(128);

        // Dirty flags — set when the list changes; readers clear them after pruning.
        private static bool combatantsDirty = true;
        private static bool suppliesDirty = true;
        private static bool sensorsDirty = true;
        private static bool providersDirty = true;

        // ================================================================
        // REGISTER / UNREGISTER
        // ================================================================

        public static void Register(NetworkCombatant c)
        {
            if (c != null) { combatants.Add(c); combatantsDirty = true; }
        }

        public static void Unregister(NetworkCombatant c)
        {
            combatants.Remove(c);   // O(n) but rare (death/destroy)
        }

        public static void Register(LargeSupplyCrate s)
        {
            if (s != null) { supplyCrates.Add(s); suppliesDirty = true; }
        }

        public static void Unregister(LargeSupplyCrate s)
        {
            supplyCrates.Remove(s);
        }

        public static void Register(SensorProbe s)
        {
            if (s != null) { sensors.Add(s); sensorsDirty = true; }
        }

        public static void Unregister(SensorProbe s)
        {
            sensors.Remove(s);
        }

        public static void Register(AIDataProvider p)
        {
            if (p != null) { providers.Add(p); providersDirty = true; }
        }

        public static void Unregister(AIDataProvider p)
        {
            providers.Remove(p);
        }

        // ================================================================
        // READ (prune nulls lazily)
        // ================================================================

        /// <summary>Live list of all registered combatants (pruned on access).</summary>
        public static List<NetworkCombatant> AllCombatants
        {
            get
            {
                if (combatantsDirty) { PruneNulls(combatants); combatantsDirty = false; }
                return combatants;
            }
        }

        /// <summary>Live list of all registered supply crates (pruned on access).</summary>
        public static List<LargeSupplyCrate> AllSupplyCrates
        {
            get
            {
                if (suppliesDirty) { PruneNulls(supplyCrates); suppliesDirty = false; }
                return supplyCrates;
            }
        }

        /// <summary>Live list of all registered sensor probes (pruned on access).</summary>
        public static List<SensorProbe> AllSensors
        {
            get
            {
                if (sensorsDirty) { PruneNulls(sensors); sensorsDirty = false; }
                return sensors;
            }
        }

        /// <summary>Live list of all registered AI data providers (pruned on access).</summary>
        public static List<AIDataProvider> AllProviders
        {
            get
            {
                if (providersDirty) { PruneNulls(providers); providersDirty = false; }
                return providers;
            }
        }

        /// <summary>Clear everything (call on domain reload / play-mode exit).</summary>
        public static void Clear()
        {
            combatants.Clear();
            supplyCrates.Clear();
            sensors.Clear();
            providers.Clear();
            combatantsDirty = true;
            suppliesDirty = true;
            sensorsDirty = true;
            providersDirty = true;
        }

        private static void PruneNulls<T>(List<T> list) where T : class
        {
            for (int i = list.Count - 1; i >= 0; i--)
                if (list[i] == null || (list[i] is Object obj && obj == null))
                    list.RemoveAt(i);
        }
    }
}
