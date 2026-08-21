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
            // move=(0,1,0) = local forward (the dash direction).
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
        public override IActionRunState Perform(IMonoAgent agent, Data data, IActionContext context)
        {
            var self = agent.Transform;
            Vector3 eye = self.position + Vector3.up * 0.8f;
            data.DataProvider.UseEquipment(EquipmentType.ArmorPlate, eye, self.forward);
            return ActionRunState.Completed;
        }

        public class Data : IActionData
        {
            public ITarget Target { get; set; }
            [GetComponent] public AIDataProvider DataProvider { get; set; }
        }
    }

    /// <summary>Use a healing syringe (channeled, grants regen buff).</summary>
    public class UseHealingSyringeAction : GoapActionBase<UseHealingSyringeAction.Data>
    {
        public override IActionRunState Perform(IMonoAgent agent, Data data, IActionContext context)
        {
            var self = agent.Transform;
            Vector3 eye = self.position + Vector3.up * 0.8f;
            data.DataProvider.UseEquipment(EquipmentType.HealingSyringe, eye, self.forward);
            return ActionRunState.Completed;
        }

        public class Data : IActionData
        {
            public ITarget Target { get; set; }
            [GetComponent] public AIDataProvider DataProvider { get; set; }
        }
    }

    /// <summary>Revive a downed ally (only when safe: no enemy in sight).</summary>
    public class UseDefibrillatorAction : GoapActionBase<UseDefibrillatorAction.Data>
    {
        public override IActionRunState Perform(IMonoAgent agent, Data data, IActionContext context)
        {
            var ally = data.DataProvider.GetNearestDownedAlly();
            if (ally == null) return ActionRunState.Completed;

            var self = agent.Transform;
            Vector3 eye = self.position + Vector3.up * 0.8f;
            data.DataProvider.UseEquipment(EquipmentType.Defibrillator, eye, ally.transform.position);
            return ActionRunState.Completed;
        }

        public class Data : IActionData
        {
            public ITarget Target { get; set; }
            [GetComponent] public AIDataProvider DataProvider { get; set; }
        }
    }
}
