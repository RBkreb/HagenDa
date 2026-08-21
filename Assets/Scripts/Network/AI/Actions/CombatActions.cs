using CrashKonijn.Agent.Core;
using CrashKonijn.Agent.Runtime;
using CrashKonijn.Goap.Runtime;
using UnityEngine;

namespace HagenDa.Networking.AI
{
    // ============================================================
    // Combat actions: engage known enemies with the gun and throwables.
    // ============================================================

    /// <summary>
    /// Attack the nearest known enemy. Attack range is unlimited — the AI engages as
    /// soon as it knows a target (direct vision, mark, or intel broadcast). The
    /// per-weapon shooting profile decides hip/ADS and burst cadence; the AI ignores
    /// recoil (applyRecoil=false) but still accumulates spread.
    /// </summary>
    public class AttackEnemyAction : GoapActionBase<AttackEnemyAction.Data>
    {
        public override IActionRunState Perform(IMonoAgent agent, Data data, IActionContext context)
        {
            var enemy = data.DataProvider.GetNearestKnownEnemy();
            if (enemy == null || enemy.IsDead)
                return ActionRunState.Completed;

            var self = agent.Transform;

            // Face the target (horizontal only).
            Vector3 to = enemy.transform.position - self.position;
            to.y = 0f;
            if (to.sqrMagnitude > 0.0001f)
                self.rotation = Quaternion.LookRotation(to);

            float dist = to.magnitude;
            var (aim, fire) = data.DataProvider.Shooting.DecideShooting(dist);

            Vector3 eye = self.position + Vector3.up * 0.8f;
            data.Gun.Tick(fire, aim, eye, self.forward, false);

            return ActionRunState.Continue;
        }

        public class Data : IActionData
        {
            public ITarget Target { get; set; }

            [GetComponent] public NetworkGun Gun { get; set; }
            [GetComponent] public AIDataProvider DataProvider { get; set; }
        }
    }

    /// <summary>Reload the gun. Triggers once on Start, waits until reload finishes.</summary>
    public class ReloadAction : GoapActionBase<ReloadAction.Data>
    {
        public override void Start(IMonoAgent agent, Data data)
        {
            if (data.Gun != null && !data.Gun.IsReloading)
                data.Gun.Reload();
        }

        public override IActionRunState Perform(IMonoAgent agent, Data data, IActionContext context)
        {
            // Wait until the reload cycle completes (or no gun).
            if (data.Gun == null || !data.Gun.IsReloading)
                return ActionRunState.Completed;
            return ActionRunState.Continue;
        }

        public class Data : IActionData
        {
            public ITarget Target { get; set; }
            [GetComponent] public NetworkGun Gun { get; set; }
        }
    }

    /// <summary>Throw a fragmentation grenade at the nearest known enemy.</summary>
    public class ThrowGrenadeAction : GoapActionBase<ThrowGrenadeAction.Data>
    {
        public override IActionRunState Perform(IMonoAgent agent, Data data, IActionContext context)
        {
            var enemy = data.DataProvider.GetNearestKnownEnemy();
            if (enemy == null) return ActionRunState.Completed;

            Vector3 eye = agent.Transform.position + Vector3.up * 0.8f;
            Vector3 dir = (enemy.transform.position - eye).normalized + Vector3.up * 0.4f;
            dir.Normalize();

            data.DataProvider.UseEquipment(EquipmentType.Grenade, eye, dir);
            return ActionRunState.Completed;
        }

        public class Data : IActionData
        {
            public ITarget Target { get; set; }
            [GetComponent] public AIDataProvider DataProvider { get; set; }
        }
    }

    /// <summary>Fire the grenade launcher (straight line aim) at the nearest known enemy.</summary>
    public class FireGrenadeLauncherAction : GoapActionBase<FireGrenadeLauncherAction.Data>
    {
        public override IActionRunState Perform(IMonoAgent agent, Data data, IActionContext context)
        {
            var enemy = data.DataProvider.GetNearestKnownEnemy();
            if (enemy == null) return ActionRunState.Completed;

            Vector3 eye = agent.Transform.position + Vector3.up * 0.8f;
            Vector3 dir = (enemy.transform.position - eye).normalized;

            data.DataProvider.UseEquipment(EquipmentType.GrenadeLauncher, eye, dir);
            return ActionRunState.Completed;
        }

        public class Data : IActionData
        {
            public ITarget Target { get; set; }
            [GetComponent] public AIDataProvider DataProvider { get; set; }
        }
    }

    /// <summary>Fire the RPG (straight line aim) at the nearest known enemy.</summary>
    public class FireRpgAction : GoapActionBase<FireRpgAction.Data>
    {
        public override IActionRunState Perform(IMonoAgent agent, Data data, IActionContext context)
        {
            var enemy = data.DataProvider.GetNearestKnownEnemy();
            if (enemy == null) return ActionRunState.Completed;

            Vector3 eye = agent.Transform.position + Vector3.up * 0.8f;
            Vector3 dir = (enemy.transform.position - eye).normalized;

            data.DataProvider.UseEquipment(EquipmentType.Rpg, eye, dir);
            return ActionRunState.Completed;
        }

        public class Data : IActionData
        {
            public ITarget Target { get; set; }
            [GetComponent] public AIDataProvider DataProvider { get; set; }
        }
    }

    /// <summary>Throw an EMP grenade at the nearest enemy deployable (sensor/crate/interceptor).</summary>
    public class ThrowEmpGrenadeAction : GoapActionBase<ThrowEmpGrenadeAction.Data>
    {
        public override IActionRunState Perform(IMonoAgent agent, Data data, IActionContext context)
        {
            var sensor = data.DataProvider.GetNearestEnemySensor();
            if (sensor == null) return ActionRunState.Completed;

            Vector3 eye = agent.Transform.position + Vector3.up * 0.8f;
            Vector3 dir = (sensor.transform.position - eye).normalized + Vector3.up * 0.3f;
            dir.Normalize();

            data.DataProvider.UseEquipment(EquipmentType.EmpGrenade, eye, dir);
            return ActionRunState.Completed;
        }

        public class Data : IActionData
        {
            public ITarget Target { get; set; }
            [GetComponent] public AIDataProvider DataProvider { get; set; }
        }
    }
}
