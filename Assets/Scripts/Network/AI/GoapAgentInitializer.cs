using CrashKonijn.Goap.Runtime;
using Mirror;
using UnityEngine;

namespace HagenDa.Networking.AI
{
    /// <summary>
    /// PHASE9 GOAP agent initializer. Wires the code-configured AgentType into the
    /// agent's GoapActionProvider and enables the equipment actions the AI actually
    /// carries (all other equipment actions are disabled to shrink the planner's
    /// search space — the conditions still enforce ammo availability).
    ///
    /// Also assigns one of three fixed loadouts per AI entity (round-robin by
    /// instance ID) so the AI team has a balanced mix of assault / support / recon.
    ///
    /// Server-side only (GOAP runs on the server).
    /// </summary>
    public class GoapAgentInitializer : MonoBehaviour
    {
        public const string AgentTypeId = "Combatant";

        private GoapActionProvider provider;
        private NetworkEquipment equipment;

        // ---- Three fixed AI loadouts (indices into NetworkEquipment.equipmentList) ----
        //  Loadout A (Assault):  GrenadeLauncher, QuickDash, HealingSyringe, Grenade
        //  Loadout B (Support):  LargeSupplyCrate, SmokeLauncher, Defibrillator, SmokeGrenade
        //  Loadout C (Recon):     Jammer, SmallSupplyPack, Sensor, EmpGrenade
        private static readonly EquipmentType[][] Loadouts =
        {
            new[] { EquipmentType.GrenadeLauncher, EquipmentType.QuickDash, EquipmentType.HealingSyringe, EquipmentType.Grenade },
            new[] { EquipmentType.LargeSupplyCrate, EquipmentType.SmokeLauncher, EquipmentType.Defibrillator, EquipmentType.SmokeGrenade },
            new[] { EquipmentType.Jammer, EquipmentType.SmallSupplyPack, EquipmentType.Sensor, EquipmentType.EmpGrenade },
        };

        private void Awake()
        {
            provider = GetComponent<GoapActionProvider>();
            equipment = GetComponent<NetworkEquipment>();

            // Code-based AgentType: resolve from the global GoapBehaviour and assign.
            var goap = Object.FindObjectOfType<GoapBehaviour>();
            if (goap != null && provider != null)
            {
                var agentType = goap.GetAgentType(AgentTypeId);
                if (agentType != null)
                    provider.AgentType = agentType;
            }
        }

        private void Start()
        {
            // Defer until the loadout slots are assigned (server spawn).
            Invoke(nameof(Configure), 0.5f);
        }

        private void Configure()
        {
            // GOAP runs server-side only — skip on clients.
            var netId = GetComponent<NetworkIdentity>();
            if (netId != null && !netId.isServer) return;

            if (provider == null || equipment == null) return;

            AssignLoadout();

            // Enable only the actions for equipment the AI carries.
            for (int slot = 0; slot < 4; slot++)
            {
                var def = equipment.GetSlotDefinition(slot);
                if (def == null) continue;
                EnableFor(def.type);
            }
        }

        /// <summary>
        /// Assign one of three fixed loadouts (round-robin by instance ID).
        /// Ensures a balanced mix of assault / support / recon across the team.
        /// </summary>
        private void AssignLoadout()
        {
            int id = GetInstanceID();
            int index = Mathf.Abs(id) % Loadouts.Length;
            var types = Loadouts[index];

            var loadout = new LoadoutDefinition
            {
                optional1 = IndexOf(types[0]),
                optional2 = IndexOf(types[1]),
                special   = IndexOf(types[2]),
                throwable = IndexOf(types[3]),
            };

            equipment.ApplyLoadout(loadout);
        }

        private int IndexOf(EquipmentType type)
        {
            if (equipment == null || equipment.equipmentList == null) return -1;
            for (int i = 0; i < equipment.equipmentList.Count; i++)
            {
                if (equipment.equipmentList[i] != null && equipment.equipmentList[i].type == type)
                    return i;
            }
            return -1;
        }

        private void EnableFor(EquipmentType type)
        {
            switch (type)
            {
                case EquipmentType.Grenade: provider.Enable<ThrowGrenadeAction>(); break;
                case EquipmentType.SmokeGrenade: provider.Enable<ThrowSmokeAction>(); break;
                case EquipmentType.EmpGrenade: provider.Enable<ThrowEmpGrenadeAction>(); break;
                case EquipmentType.GrenadeLauncher: provider.Enable<FireGrenadeLauncherAction>(); break;
                case EquipmentType.SmokeLauncher: provider.Enable<FireSmokeLauncherAction>(); break;
                case EquipmentType.Rpg: provider.Enable<FireRpgAction>(); break;
                case EquipmentType.SmallSupplyPack: provider.Enable<ThrowSupplyPackAction>(); break;
                case EquipmentType.LargeSupplyCrate: provider.Enable<DeploySupplyCrateAction>(); break;
                case EquipmentType.Interceptor: provider.Enable<DeployInterceptorAction>(); break;
                case EquipmentType.Sensor: provider.Enable<DeploySensorAction>(); break;
                case EquipmentType.QuickDash: provider.Enable<QuickDashAction>(); break;
                case EquipmentType.Jammer: provider.Enable<UseJammerAction>(); break;
                case EquipmentType.ArmorPlate: provider.Enable<ApplyArmorPlateAction>(); break;
                case EquipmentType.HealingSyringe: provider.Enable<UseHealingSyringeAction>(); break;
                case EquipmentType.Defibrillator: provider.Enable<UseDefibrillatorAction>(); break;
            }
        }
    }
}
