using CrashKonijn.Agent.Core;
using CrashKonijn.Agent.Runtime;
using CrashKonijn.Goap.Runtime;
using UnityEngine;

namespace HagenDa.Networking.AI
{
    // ============================================================
    // Self actions: jammer, dash, armor plate, syringe, defibrillator.
    // ============================================================

    /// <summary>Use the jammer to clear self mark + gain mark immunity.</summary>
    public class UseJammerAction : GoapActionBase<UseJammerAction.Data>
    {
        public override IActionRunState Perform(IMonoAgent agent, Data data, IActionContext context)
        {
            var self = agent.Transform;
            Vector3 eye = self.position + Vector3.up * 0.8f;
            data.DataProvider.UseEquipment(EquipmentType.Jammer, eye, self.forward);
            return ActionRunState.Completed;
        }

        public class Data : IActionData
        {
            public ITarget Target { get; set; }
            [GetComponent] public AIDataProvider DataProvider { get; set; }
        }
    }

    /// <summary>Quick-dash forward to evade fire (self-instant, uses local move dir).</summary>
    public class QuickDashAction : GoapActionBase<QuickDashAction.Data>
    {
        public override IActionRunState Perform(IMonoAgent agent, Data data, IActionContext context)
        {
            int slot = data.DataProvider.GetEquipmentSlot(EquipmentType.QuickDash);
            if (slot < 0) return ActionRunState.Completed;

            int index = data.Equipment.GetSlotIndex(slot);
            Vector3 eye = agent.Transform.position + Vector3.up * 0.8f;
            data.Equipment.Use(index, false, eye, agent.Transform.forward, Vector3.up, true);
            return ActionRunState.Completed;
        }

        public class Data : IActionData
        {
            public ITarget Target { get; set; }
            [GetComponent] public AIDataProvider DataProvider { get; set; }
            [GetComponent] public NetworkEquipment Equipment { get; set; }
        }
    }

    /// <summary>Apply an armor plate (channeled, restores armor).</summary>
    public class ApplyArmorPlateAction : GoapActionBase<ApplyArmorPlateAction.Data>
    {
        public override void Start(IMonoAgent agent, Data data)
        {
            data.Timer = 0f;
            var self = agent.Transform;
            Vector3 eye = self.position + Vector3.up * 0.8f;
            data.DataProvider.UseEquipment(EquipmentType.ArmorPlate, eye, self.forward);
        }

        public override IActionRunState Perform(IMonoAgent agent, Data data, IActionContext context)
        {
            data.Timer += context.DeltaTime;
            if (data.Timer < 2f)
                return ActionRunState.Wait(0.5f, mayResolve: true);
            return ActionRunState.Completed;
        }

        public class Data : IActionData
        {
            public ITarget Target { get; set; }
            [GetComponent] public AIDataProvider DataProvider { get; set; }
            public float Timer;
        }
    }

    /// <summary>
    /// Use a healing syringe. The syringe is a SelfChannel item — the equipment system
    /// handles the channel in the background, so the AI can keep moving and shooting
    /// while the regen buff activates. The action completes immediately after triggering.
    /// </summary>
    public class UseHealingSyringeAction : GoapActionBase<UseHealingSyringeAction.Data>
    {
        public override void Start(IMonoAgent agent, Data data)
        {
            var self = agent.Transform;
            Vector3 eye = self.position + Vector3.up * 0.8f;
            data.DataProvider.UseEquipment(EquipmentType.HealingSyringe, eye, self.forward);
        }

        public override IActionRunState Perform(IMonoAgent agent, Data data, IActionContext context)
        {
            // Syringe channels in the background — no need to wait.
            return ActionRunState.Completed;
        }

        public class Data : IActionData
        {
            public ITarget Target { get; set; }
            [GetComponent] public AIDataProvider DataProvider { get; set; }
        }
    }

    /// <summary>
    /// Revive a downed ally with the defibrillator. On arrival, evaluates safety near
    /// the ally — if enemies are nearby, throws smoke for cover before reviving.
    /// The channel takes 2s; the action waits for it to complete.
    /// </summary>
    public class UseDefibrillatorAction : GoapActionBase<UseDefibrillatorAction.Data>
    {
        private const float SafetyRadius = 15f;

        public override void Start(IMonoAgent agent, Data data)
        {
            data.Timer = 0f;
            data.SmokeUsed = false;
            var self = agent.Transform;
            Vector3 eye = self.position + Vector3.up * 0.8f;

            // Evaluate safety near the downed ally.
            var ally = data.DataProvider.GetNearestDownedAlly();
            if (ally == null) return;

            bool enemiesNearAlly = data.DataProvider.IsEnemyNearPosition(
                ally.transform.position, SafetyRadius);

            if (enemiesNearAlly)
            {
                // Throw smoke at the ally's position for cover.
                if (data.DataProvider.HasEquipmentAmmo(EquipmentType.SmokeGrenade))
                {
                    Vector3 dir = (ally.transform.position - eye).normalized + Vector3.up * 0.3f;
                    dir.Normalize();
                    data.DataProvider.UseEquipment(EquipmentType.SmokeGrenade, eye, dir);
                    data.SmokeUsed = true;
                }
                else if (data.DataProvider.HasEquipmentAmmo(EquipmentType.SmokeLauncher))
                {
                    Vector3 dir = (ally.transform.position - eye).normalized;
                    data.DataProvider.UseEquipment(EquipmentType.SmokeLauncher, eye, dir);
                    data.SmokeUsed = true;
                }
            }

            // Start the defibrillator channel.
            data.DataProvider.UseEquipment(EquipmentType.Defibrillator, eye, ally.transform.position);
        }

        public override IActionRunState Perform(IMonoAgent agent, Data data, IActionContext context)
        {
            data.Timer += context.DeltaTime;
            // Defibrillator channel = 2s; wait a bit longer for safety.
            if (data.Timer < 2.5f)
                return ActionRunState.Wait(0.5f, mayResolve: true);
            return ActionRunState.Completed;
        }

        public class Data : IActionData
        {
            public ITarget Target { get; set; }
            [GetComponent] public AIDataProvider DataProvider { get; set; }
            public float Timer;
            public bool SmokeUsed;
        }
    }
}
