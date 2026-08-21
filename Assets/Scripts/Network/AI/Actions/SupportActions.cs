using CrashKonijn.Agent.Core;
using CrashKonijn.Agent.Runtime;
using CrashKonijn.Goap.Runtime;
using UnityEngine;

namespace HagenDa.Networking.AI
{
    // ============================================================
    // Support actions: smoke (self cover) and supply-pack sharing.
    // ============================================================

    /// <summary>Throw a smoke grenade forward to break enemy line of sight.</summary>
    public class ThrowSmokeAction : GoapActionBase<ThrowSmokeAction.Data>
    {
        public override IActionRunState Perform(IMonoAgent agent, Data data, IActionContext context)
        {
            var self = agent.Transform;
            Vector3 eye = self.position + Vector3.up * 0.8f;

            // Throw a short distance ahead (between self and the threat).
            var enemy = data.DataProvider.GetNearestEnemy();
            Vector3 dir = enemy != null
                ? (enemy.transform.position - eye).normalized + Vector3.up * 0.3f
                : self.forward + Vector3.up * 0.3f;
            dir.Normalize();

            data.DataProvider.UseEquipment(EquipmentType.SmokeGrenade, eye, dir);
            return ActionRunState.Completed;
        }

        public class Data : IActionData
        {
            public ITarget Target { get; set; }
            [GetComponent] public AIDataProvider DataProvider { get; set; }
        }
    }

    /// <summary>Fire a smoke round ahead (bolt launcher) to break enemy line of sight.</summary>
    public class FireSmokeLauncherAction : GoapActionBase<FireSmokeLauncherAction.Data>
    {
        public override IActionRunState Perform(IMonoAgent agent, Data data, IActionContext context)
        {
            var self = agent.Transform;
            Vector3 eye = self.position + Vector3.up * 0.8f;

            var enemy = data.DataProvider.GetNearestEnemy();
            Vector3 dir = enemy != null
                ? (enemy.transform.position - eye).normalized
                : self.forward;

            data.DataProvider.UseEquipment(EquipmentType.SmokeLauncher, eye, dir);
            return ActionRunState.Completed;
        }

        public class Data : IActionData
        {
            public ITarget Target { get; set; }
            [GetComponent] public AIDataProvider DataProvider { get; set; }
        }
    }

    /// <summary>Throw a supply pack to the nearest low-ammo ally.</summary>
    public class ThrowSupplyPackAction : GoapActionBase<ThrowSupplyPackAction.Data>
    {
        public override IActionRunState Perform(IMonoAgent agent, Data data, IActionContext context)
        {
            var ally = data.DataProvider.GetNearestLowAmmoAlly();
            if (ally == null) return ActionRunState.Completed;

            Vector3 eye = agent.Transform.position + Vector3.up * 0.8f;
            Vector3 dir = (ally.transform.position - eye).normalized + Vector3.up * 0.3f;
            dir.Normalize();

            data.DataProvider.UseEquipment(EquipmentType.SmallSupplyPack, eye, dir);
            return ActionRunState.Completed;
        }

        public class Data : IActionData
        {
            public ITarget Target { get; set; }
            [GetComponent] public AIDataProvider DataProvider { get; set; }
        }
    }
}
