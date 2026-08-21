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
    ///
    /// Aiming: the AI picks a visible aim point (foot / body / head) so it shoots the
    /// exposed part when the target is behind cover, and its horizontal turn is
    /// rate-limited (360°/s) so it cannot snap 180° instantly — it only fires once
    /// it has mostly turned toward the target.
    /// </summary>
    public class AttackEnemyAction : GoapActionBase<AttackEnemyAction.Data>
    {
        /// <summary>Max horizontal turn rate (degrees per second).</summary>
        private const float MaxTurnDegPerSec = 360f;

        /// <summary>Fire only once the horizontal aim error is below this (degrees).</summary>
        private const float FireYawThreshold = 25f;

        public override IActionRunState Perform(IMonoAgent agent, Data data, IActionContext context)
        {
            var enemy = data.DataProvider.GetNearestKnownEnemy();
            if (enemy == null || enemy.IsDead)
                return ActionRunState.Completed;

            var self = agent.Transform;

            // Horizontal turn target (rate-limited).
            Vector3 toEnemy = enemy.transform.position - self.position;
            toEnemy.y = 0f;
            Quaternion targetRot = toEnemy.sqrMagnitude > 0.0001f
                ? Quaternion.LookRotation(toEnemy)
                : self.rotation;

            float maxStep = MaxTurnDegPerSec * context.DeltaTime;
            self.rotation = Quaternion.RotateTowards(self.rotation, targetRot, maxStep);

            float yawError = Quaternion.Angle(self.rotation, targetRot);
            bool facingTarget = yawError <= FireYawThreshold;

            // Pick a visible aim point (foot / body / head) and build the fire
            // direction from the current horizontal facing + vertical elevation to
            // that point. While turning, the horizontal part lags → shots miss.
            Vector3 aimPoint = VisionSystem.GetVisibleAimPoint(self, enemy.transform);
            Vector3 eye = self.position + Vector3.up * 0.8f;

            Vector3 forwardFlat = self.forward;
            forwardFlat.y = 0f;
            if (forwardFlat.sqrMagnitude < 0.0001f) forwardFlat = Vector3.forward;
            forwardFlat.Normalize();

            Vector3 toAim = aimPoint - eye;
            float hDist = new Vector3(toAim.x, 0f, toAim.z).magnitude;
            float pitch = Mathf.Atan2(toAim.y, Mathf.Max(0.01f, hDist)) * Mathf.Rad2Deg;
            Vector3 right = Vector3.Cross(Vector3.up, forwardFlat).normalized;
            Vector3 fireDir = Quaternion.AngleAxis(pitch, right) * forwardFlat;

            float dist = hDist;
            var (aim, fire) = data.DataProvider.Shooting.DecideShooting(dist);
            fire = fire && facingTarget;

            data.Gun.Tick(fire, aim, eye, fireDir, false);

            // ContinueOrResolve (not Continue): let a higher-priority goal (e.g.
            // Survive when health drops) interrupt mid-combat.
            return ActionRunState.ContinueOrResolve;
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
            return ActionRunState.ContinueOrResolve;
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
