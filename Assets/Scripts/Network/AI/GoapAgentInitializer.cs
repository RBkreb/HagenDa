using CrashKonijn.Goap.Runtime;
using UnityEngine;

namespace HagenDa.Networking.AI
{
    /// <summary>
    /// PHASE9 GOAP agent initializer. Wires the code-configured AgentType into the
    /// agent's GoapActionProvider and enables the equipment actions the AI actually
    /// carries (all other equipment actions are disabled to shrink the planner's
    /// search space — the conditions still enforce ammo availability).
    ///
    /// Server-side only (GOAP runs on the server).
    /// </summary>
    public class GoapAgentInitializer : MonoBehaviour
    {
        public const string AgentTypeId = "Combatant";

        private GoapActionProvider provider;
        private NetworkEquipment equipment;

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
            Invoke(nameof(ConfigureActions), 0.5f);
        }

        private void ConfigureActions()
        {
            if (provider == null || equipment == null) return;

            // Enable only the actions for equipment the AI carries.
            for (int slot = 0; slot < 4; slot++)
            {
                var def = equipment.GetSlotDefinition(slot);
                if (def == null) continue;
                EnableFor(def.type);
            }
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
