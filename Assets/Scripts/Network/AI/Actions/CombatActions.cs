using CrashKonijn.Agent.Core;
using CrashKonijn.Agent.Runtime;
using CrashKonijn.Goap.Runtime;
using UnityEngine;
using UnityEngine.AI;

namespace HagenDa.Networking.AI
{
    // ============================================================
    // Combat actions: engage known enemies with the gun and throwables.
    // ============================================================

    /// <summary>
    /// Attack the nearest known enemy. The AI maintains small lateral movements
    /// (strafing) to avoid being a static target, seeks cover when under fire, and
    /// uses tactical equipment based on the situation.
    /// </summary>
    public class AttackEnemyAction : GoapActionBase<AttackEnemyAction.Data>
    {
        private const float MaxTurnDegPerSec = 360f;
        private const float FireYawThreshold = 25f;

        private const float StrafeInterval = 1.5f;
        private const float StrafeDistance = 3f;
        private const float EquipmentCheckInterval = 2f;
        private const float CoverCheckInterval = 3f;
        private const float CoverSearchDist = 12f;

        public override IActionRunState Perform(IMonoAgent agent, Data data, IActionContext context)
        {
            var enemy = data.DataProvider.GetNearestKnownEnemy();
            if (enemy == null || enemy.IsDead)
                return ActionRunState.Completed;

            var self = agent.Transform;
            float dt = context.DeltaTime;
            Vector3 enemyPos = enemy.transform.position;

            // === Turn toward enemy (rate-limited) ===
            Vector3 toEnemy = enemyPos - self.position;
            toEnemy.y = 0f;
            Quaternion targetRot = toEnemy.sqrMagnitude > 0.0001f
                ? Quaternion.LookRotation(toEnemy)
                : self.rotation;
            float maxStep = MaxTurnDegPerSec * dt;
            self.rotation = Quaternion.RotateTowards(self.rotation, targetRot, maxStep);
            float yawError = Quaternion.Angle(self.rotation, targetRot);
            bool facingTarget = yawError <= FireYawThreshold;

            // === Aim and shoot ===
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

            // === Self-heal (fire-and-forget, can move while channeling) ===
            data.DataProvider.TrySelfHeal();

            // === Tactical equipment usage ===
            data.EquipTimer -= dt;
            if (data.EquipTimer <= 0f)
            {
                data.EquipTimer = EquipmentCheckInterval;
                UseTacticalEquipment(data, self, enemy, eye, dist);
            }

            // === Movement: strafe or seek cover ===
            if (data.DataProvider.IsUnderFire())
                SeekCover(data, self, enemyPos, dt);
            else
                Strafe(data, self, enemyPos, dt);

            return ActionRunState.ContinueOrResolve;
        }

        private void Strafe(Data data, Transform self, Vector3 enemyPos, float dt)
        {
            data.StrafeTimer -= dt;
            if (data.StrafeTimer > 0f) return;
            data.StrafeTimer = StrafeInterval;

            if (data.NavAgent == null || !data.NavAgent.isOnNavMesh) return;

            Vector3 toEnemy = enemyPos - self.position;
            toEnemy.y = 0f;
            if (toEnemy.sqrMagnitude < 0.01f) return;

            Vector3 perp = Vector3.Cross(Vector3.up, toEnemy.normalized).normalized;
            if (Random.value < 0.5f) perp = -perp;

            Vector3 strafePos = self.position + perp * StrafeDistance;
            if (NavMesh.SamplePosition(strafePos, out NavMeshHit hit, 2f, NavMesh.AllAreas))
                data.NavAgent.SetDestination(hit.position);
        }

        private void SeekCover(Data data, Transform self, Vector3 enemyPos, float dt)
        {
            data.CoverTimer -= dt;
            if (data.CoverTimer > 0f) return;
            data.CoverTimer = CoverCheckInterval;

            if (data.NavAgent == null || !data.NavAgent.isOnNavMesh) return;

            var cover = CoverSystem.FindCover(self.position, enemyPos, CoverSearchDist);
            if (cover.hasCover)
            {
                data.NavAgent.SetDestination(cover.coverPosition);
                if (data.AI != null) data.AI.SetAIPosture(AIPosture.Crouch);
            }
            else
            {
                Strafe(data, self, enemyPos, dt);
            }
        }

        // ---------------------------------------------------------------
        // Tactical equipment: smart situational usage.
        // ---------------------------------------------------------------
        private void UseTacticalEquipment(Data data, Transform self,
            NetworkCombatant enemy, Vector3 eye, float dist)
        {
            var dp = data.DataProvider;

            // 1. Jammer — clear mark if self is marked.
            if (dp.IsSelfMarked() && dp.HasEquipmentAmmo(EquipmentType.Jammer))
            {
                dp.UseEquipment(EquipmentType.Jammer, eye, self.forward);
                return;
            }

            // 2. Smoke — when under fire: throw at feet (retreat cover).
            if (dp.IsUnderFire())
            {
                if (dp.HasEquipmentAmmo(EquipmentType.SmokeGrenade))
                {
                    // Throw at feet: straight down.
                    dp.UseEquipment(EquipmentType.SmokeGrenade, eye, Vector3.down);
                    return;
                }
                if (dp.HasEquipmentAmmo(EquipmentType.SmokeLauncher))
                {
                    // Fire at feet: straight down.
                    dp.UseEquipment(EquipmentType.SmokeLauncher, eye, Vector3.down);
                    return;
                }
            }

            // 3. Grenade launcher — fire when enemy is retreating OR enemies clustered (5m).
            if (dp.HasEquipmentAmmo(EquipmentType.GrenadeLauncher) &&
                (dp.IsEnemyRetreating() || dp.IsEnemyClustered()))
            {
                Vector3 dir = (enemy.transform.position - eye).normalized;
                dp.UseEquipment(EquipmentType.GrenadeLauncher, eye, dir);
                return;
            }

            // 4. Frag grenade — throw at medium-range enemies (15-35m).
            if (dist > 15f && dist < 35f && dp.HasEquipmentAmmo(EquipmentType.Grenade))
            {
                Vector3 dir = (enemy.transform.position - eye).normalized + Vector3.up * 0.4f;
                dir.Normalize();
                dp.UseEquipment(EquipmentType.Grenade, eye, dir);
                return;
            }

            // 5. EMP grenade — destroy enemy deployables (sensors).
            var sensor = dp.GetNearestEnemySensor();
            if (sensor != null && dp.HasEquipmentAmmo(EquipmentType.EmpGrenade))
            {
                float sensorDist = Vector3.Distance(self.position, sensor.transform.position);
                if (sensorDist < 30f)
                {
                    Vector3 dir = (sensor.transform.position - eye).normalized + Vector3.up * 0.3f;
                    dir.Normalize();
                    dp.UseEquipment(EquipmentType.EmpGrenade, eye, dir);
                    return;
                }
            }

            // 6. Quick dash — evade when under fire (perpendicular to enemy).
            if (dp.IsUnderFire() && dp.HasEquipmentAmmo(EquipmentType.QuickDash))
            {
                int slot = dp.GetEquipmentSlot(EquipmentType.QuickDash);
                if (slot >= 0 && data.Equipment != null)
                {
                    int index = data.Equipment.GetSlotIndex(slot);
                    Vector3 dashDir = Vector3.Cross(Vector3.up,
                        (enemy.transform.position - self.position).normalized).normalized;
                    if (Random.value < 0.5f) dashDir = -dashDir;
                    data.Equipment.Use(index, false, eye, dashDir, Vector3.up, true);
                    return;
                }
            }

            // 7. Deploy supply crate when low health/ammo and safe (not under fire).
            if ((dp.GetAmmoLevel() < 20 || dp.GetHealthLevel() < 35) &&
                !dp.IsUnderFire() &&
                dp.HasEquipmentAmmo(EquipmentType.LargeSupplyCrate))
            {
                dp.UseEquipment(EquipmentType.LargeSupplyCrate, eye, self.forward);
                return;
            }

            // 8. Deploy sensor: at capture point with no friendly sensor OR enemy within 20m.
            if (dp.HasEquipmentAmmo(EquipmentType.Sensor) &&
                (dp.ShouldDeploySensorAtCapturePoint() || dp.IsEnemyWithinRange(20f)))
            {
                dp.UseEquipment(EquipmentType.Sensor, eye, self.forward);
                return;
            }

            // 9. Throw supply pack to low-ammo ally.
            var ally = dp.GetNearestLowAmmoAlly();
            if (ally != null && dp.HasEquipmentAmmo(EquipmentType.SmallSupplyPack))
            {
                Vector3 dir = (ally.transform.position - eye).normalized + Vector3.up * 0.3f;
                dir.Normalize();
                dp.UseEquipment(EquipmentType.SmallSupplyPack, eye, dir);
                return;
            }

            // 10. Smoke for ally rescue: throw at rescue target if ally is downed nearby.
            var rescueTarget = dp.GetNearestRescueRequest();
            if (rescueTarget != null && dp.HasEquipmentAmmo(EquipmentType.SmokeGrenade))
            {
                float rescueDist = Vector3.Distance(self.position, rescueTarget.transform.position);
                if (rescueDist < 25f)
                {
                    Vector3 dir = (rescueTarget.transform.position - eye).normalized + Vector3.up * 0.3f;
                    dir.Normalize();
                    dp.UseEquipment(EquipmentType.SmokeGrenade, eye, dir);
                    return;
                }
            }
        }

        public class Data : IActionData
        {
            public ITarget Target { get; set; }

            [GetComponent] public NetworkGun Gun { get; set; }
            [GetComponent] public AIDataProvider DataProvider { get; set; }
            [GetComponent] public NetworkEquipment Equipment { get; set; }
            [GetComponent] public NavMeshAgent NavAgent { get; set; }
            [GetComponent] public NetworkAIController AI { get; set; }

            public float StrafeTimer;
            public float EquipTimer;
            public float CoverTimer;
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

    /// <summary>Throw an EMP grenade at the nearest enemy deployable.</summary>
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
