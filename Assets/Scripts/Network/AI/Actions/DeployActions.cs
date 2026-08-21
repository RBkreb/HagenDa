using CrashKonijn.Agent.Core;
using CrashKonijn.Agent.Runtime;
using CrashKonijn.Goap.Runtime;
using UnityEngine;

namespace HagenDa.Networking.AI
{
    // ============================================================
    // Deploy actions: place supply crates, interceptors and sensors.
    // ============================================================

    /// <summary>Deploy a large supply crate at the AI's position (self/team resupply).</summary>
    public class DeploySupplyCrateAction : GoapActionBase<DeploySupplyCrateAction.Data>
    {
        public override IActionRunState Perform(IMonoAgent agent, Data data, IActionContext context)
        {
            var self = agent.Transform;
            Vector3 eye = self.position + Vector3.up * 0.8f;
            data.DataProvider.UseEquipment(EquipmentType.LargeSupplyCrate, eye, self.forward);
            return ActionRunState.Completed;
        }

        public class Data : IActionData
        {
            public ITarget Target { get; set; }
            [GetComponent] public AIDataProvider DataProvider { get; set; }
        }
    }

    /// <summary>Deploy an interceptor system (defensive).</summary>
    public class DeployInterceptorAction : GoapActionBase<DeployInterceptorAction.Data>
    {
        public override IActionRunState Perform(IMonoAgent agent, Data data, IActionContext context)
        {
            var self = agent.Transform;
            Vector3 eye = self.position + Vector3.up * 0.8f;
            data.DataProvider.UseEquipment(EquipmentType.Interceptor, eye, self.forward);
            return ActionRunState.Completed;
        }

        public class Data : IActionData
        {
            public ITarget Target { get; set; }
            [GetComponent] public AIDataProvider DataProvider { get; set; }
        }
    }

    /// <summary>Deploy a sensor probe to mark nearby enemies.</summary>
    public class DeploySensorAction : GoapActionBase<DeploySensorAction.Data>
    {
        public override IActionRunState Perform(IMonoAgent agent, Data data, IActionContext context)
        {
            var self = agent.Transform;
            Vector3 eye = self.position + Vector3.up * 0.8f;
            data.DataProvider.UseEquipment(EquipmentType.Sensor, eye, self.forward);
            return ActionRunState.Completed;
        }

        public class Data : IActionData
        {
            public ITarget Target { get; set; }
            [GetComponent] public AIDataProvider DataProvider { get; set; }
        }
    }
}
