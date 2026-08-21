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
    /// Revive a downed ally with the defibrillator. While approaching, throws smoke
    /// toward the downed ally if enemies are nearby. On arrival, starts the defibrillator
    /// channel (2s) and waits for it to complete.
    /// </summary>
    public class UseDefibrillatorAction : GoapActionBase<UseDefibrillatorAction.Data>
    {
        private const float SafetyRadius = 15f;
        private const float SmokeThrowRange = 30f;   // throw smoke when within this distance of ally

        public override void Start(IMonoAgent agent, Data data)
        {
            data.Timer = 0f;
            data.SmokeUsed = false;
            data.DefibStarted = false;
        }

        public override IActionRunState Perform(IMonoAgent agent, Data data, IActionContext context)
        {
            var dp = data.DataProvider;
            var ally = dp.GetNearestDownedAlly();
            if (ally == null) return ActionRunState.Completed;

            var self = agent.Transform;
            Vector3 eye = self.position + Vector3.up * 0.8f;
            float distToAlly = Vector3.Distance(self.position, ally.transform.position);

            // Phase 1: while approaching (not yet at ally), throw smoke toward ally if
            // enemies are near the ally and we haven't thrown smoke yet.
            if (!data.DefibStarted)
            {
                if (!data.SmokeUsed && distToAlly <= SmokeThrowRange)
                {
                    bool enemiesNearAlly = dp.IsEnemyNearPosition(ally.transform.position, SafetyRadius);
                    if (enemiesNearAlly)
                    {
                        Vector3 dir = (ally.transform.position - eye).normalized + Vector3.up * 0.3f;
                        dir.Normalize();

                        if (dp.HasEquipmentAmmo(EquipmentType.SmokeGrenade))
                        {
                            dp.UseEquipment(EquipmentType.SmokeGrenade, eye, dir);
                            data.SmokeUsed = true;
                        }
                        else if (dp.HasEquipmentAmmo(EquipmentType.SmokeLauncher))
                        {
                            dp.UseEquipment(EquipmentType.SmokeLauncher, eye,
                                (ally.transform.position - eye).normalized);
                            data.SmokeUsed = true;
                        }
                    }
                }

                // Check if we've arrived at the ally (within stopping distance).
                if (data.NavAgent != null && data.NavAgent.isOnNavMesh)
                {
                    if (data.NavAgent.pathPending)
                        return ActionRunState.Continue;
                    if (data.NavAgent.remainingDistance > data.NavAgent.stoppingDistance + 0.5f)
                        return ActionRunState.Continue;
                }
                else if (distToAlly > 3f)
                {
                    return ActionRunState.Continue;
                }

                // Arrived: start the defibrillator channel.
                dp.UseEquipment(EquipmentType.Defibrillator, eye, ally.transform.position);
                data.DefibStarted = true;
            }

            // Phase 2: wait for the defibrillator channel to complete.
            data.Timer += context.DeltaTime;
            if (data.Timer < 2.5f)
                return ActionRunState.Wait(0.5f, mayResolve: true);
            return ActionRunState.Completed;
        }

        public class Data : IActionData
        {
            public ITarget Target { get; set; }
            [GetComponent] public AIDataProvider DataProvider { get; set; }
            [GetComponent] public UnityEngine.AI.NavMeshAgent NavAgent { get; set; }
            public float Timer;
            public bool SmokeUsed;
            public bool DefibStarted;
        }
    }
}
