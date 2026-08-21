using CrashKonijn.Agent.Core;
using CrashKonijn.Goap.Runtime;
using UnityEngine;
using UnityEngine.AI;

namespace HagenDa.Networking.AI
{
    // ============================================================
    // Local target sensors: each maps one TargetKey to a world
    // position (TransformTarget for living/zone targets, PositionTarget
    // for static points) read from the AI's AIDataProvider cache.
    // ============================================================

    public class NearestEnemySensor : LocalTargetSensorBase
    {
        public override void Created() { }
        public override void Update() { }
        public override ITarget Sense(IActionReceiver agent, IComponentReference references, ITarget existingTarget)
        {
            var data = references.GetCachedComponent<AIDataProvider>();
            if (data == null) return null;

            // Prefer a marked enemy (known location), else any known enemy.
            var target = data.GetNearestMarkedEnemy() ?? data.GetNearestKnownEnemy();
            if (target == null) return null;

            if (existingTarget is TransformTarget t)
                return t.SetTransform(target.transform);
            return new TransformTarget(target.transform);
        }
    }

    public class NearestMarkedEnemySensor : LocalTargetSensorBase
    {
        public override void Created() { }
        public override void Update() { }
        public override ITarget Sense(IActionReceiver agent, IComponentReference references, ITarget existingTarget)
        {
            var data = references.GetCachedComponent<AIDataProvider>();
            var target = data != null ? data.GetNearestMarkedEnemy() : null;
            if (target == null) return null;

            if (existingTarget is TransformTarget t)
                return t.SetTransform(target.transform);
            return new TransformTarget(target.transform);
        }
    }

    public class NearestCapturePointSensor : LocalTargetSensorBase
    {
        public override void Created() { }
        public override void Update() { }
        public override ITarget Sense(IActionReceiver agent, IComponentReference references, ITarget existingTarget)
        {
            var data = references.GetCachedComponent<AIDataProvider>();
            var point = data != null ? data.GetNearestUncapturedPoint() : null;
            if (point == null) return null;

            if (existingTarget is TransformTarget t)
                return t.SetTransform(point.transform);
            return new TransformTarget(point.transform);
        }
    }

    public class NearestOwnedPointSensor : LocalTargetSensorBase
    {
        public override void Created() { }
        public override void Update() { }
        public override ITarget Sense(IActionReceiver agent, IComponentReference references, ITarget existingTarget)
        {
            var data = references.GetCachedComponent<AIDataProvider>();
            var point = data != null ? data.GetNearestOwnedPoint() : null;
            if (point == null) return null;

            if (existingTarget is TransformTarget t)
                return t.SetTransform(point.transform);
            return new TransformTarget(point.transform);
        }
    }

    public class NearestGarrisonSensor : LocalTargetSensorBase
    {
        public override void Created() { }
        public override void Update() { }
        public override ITarget Sense(IActionReceiver agent, IComponentReference references, ITarget existingTarget)
        {
            var data = references.GetCachedComponent<AIDataProvider>();
            var g = data != null ? data.GetNearestGarrison() : null;
            if (g == null) return null;

            if (existingTarget is TransformTarget t)
                return t.SetTransform(g.transform);
            return new TransformTarget(g.transform);
        }
    }

    public class NearestSupplySensor : LocalTargetSensorBase
    {
        public override void Created() { }
        public override void Update() { }
        public override ITarget Sense(IActionReceiver agent, IComponentReference references, ITarget existingTarget)
        {
            var data = references.GetCachedComponent<AIDataProvider>();
            var s = data != null ? data.GetNearestSupply() : null;
            if (s == null) return null;

            if (existingTarget is TransformTarget t)
                return t.SetTransform(s.transform);
            return new TransformTarget(s.transform);
        }
    }

    public class NearestDownedAllySensor : LocalTargetSensorBase
    {
        public override void Created() { }
        public override void Update() { }
        public override ITarget Sense(IActionReceiver agent, IComponentReference references, ITarget existingTarget)
        {
            var data = references.GetCachedComponent<AIDataProvider>();
            var ally = data != null ? data.GetNearestDownedAlly() : null;
            if (ally == null) return null;

            if (existingTarget is TransformTarget t)
                return t.SetTransform(ally.transform);
            return new TransformTarget(ally.transform);
        }
    }

    public class NearestLowAmmoAllySensor : LocalTargetSensorBase
    {
        public override void Created() { }
        public override void Update() { }
        public override ITarget Sense(IActionReceiver agent, IComponentReference references, ITarget existingTarget)
        {
            var data = references.GetCachedComponent<AIDataProvider>();
            var ally = data != null ? data.GetNearestLowAmmoAlly() : null;
            if (ally == null) return null;

            if (existingTarget is TransformTarget t)
                return t.SetTransform(ally.transform);
            return new TransformTarget(ally.transform);
        }
    }

    public class NearestEnemyDeployableSensor : LocalTargetSensorBase
    {
        public override void Created() { }
        public override void Update() { }
        public override ITarget Sense(IActionReceiver agent, IComponentReference references, ITarget existingTarget)
        {
            var data = references.GetCachedComponent<AIDataProvider>();
            var s = data != null ? data.GetNearestEnemySensor() : null;
            if (s == null) return null;

            if (existingTarget is TransformTarget t)
                return t.SetTransform(s.transform);
            return new TransformTarget(s.transform);
        }
    }

    public class SquadLeaderPosSensor : LocalTargetSensorBase
    {
        public override void Created() { }
        public override void Update() { }
        public override ITarget Sense(IActionReceiver agent, IComponentReference references, ITarget existingTarget)
        {
            var data = references.GetCachedComponent<AIDataProvider>();
            var leader = data != null ? data.GetSquadLeader() : null;
            if (leader == null) return null;

            if (existingTarget is TransformTarget t)
                return t.SetTransform(leader.transform);
            return new TransformTarget(leader.transform);
        }
    }

    public class WanderPointSensor : LocalTargetSensorBase
    {
        public override void Created() { }
        public override void Update() { }

        public override ITarget Sense(IActionReceiver agent, IComponentReference references, ITarget existingTarget)
        {
            var data = references.GetCachedComponent<AIDataProvider>();

            // Prefer a contested/neutral objective to move toward while patrolling.
            var point = data != null ? data.GetNearestUncapturedPoint() : null;
            if (point != null)
            {
                if (existingTarget is TransformTarget t)
                    return t.SetTransform(point.transform);
                return new TransformTarget(point.transform);
            }

            // Fallback: random navmesh point near the agent.
            Vector3 pos = RandomNavMesh(agent.Transform.position, 8f);
            if (existingTarget is PositionTarget pt)
                return pt.SetPosition(pos);
            return new PositionTarget(pos);
        }

        private static Vector3 RandomNavMesh(Vector3 center, float radius)
        {
            for (int i = 0; i < 10; i++)
            {
                var p = center + new Vector3(
                    Random.Range(-1f, 1f), 0f, Random.Range(-1f, 1f)).normalized * Random.Range(0f, radius);
                if (NavMesh.SamplePosition(p, out NavMeshHit hit, 2f, NavMesh.AllAreas))
                    return hit.position;
            }
            return center;
        }
    }
}
