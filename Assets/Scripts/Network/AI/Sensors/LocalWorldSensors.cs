using CrashKonijn.Agent.Core;
using CrashKonijn.Goap.Core;
using CrashKonijn.Goap.Runtime;

namespace HagenDa.Networking.AI
{
    // ============================================================
    // Local world sensors: each maps one WorldKey to an int (0/1 or
    // a percentage) read from the AI's AIDataProvider cache.
    // ============================================================

    public class HealthLevelSensor : LocalWorldSensorBase
    {
        public override void Created() { }
        public override void Update() { }
        public override SenseValue Sense(IActionReceiver agent, IComponentReference references)
        {
            var data = references.GetCachedComponent<AIDataProvider>();
            return data != null ? data.GetHealthLevel() : 0;
        }
    }

    public class ArmorLevelSensor : LocalWorldSensorBase
    {
        public override void Created() { }
        public override void Update() { }
        public override SenseValue Sense(IActionReceiver agent, IComponentReference references)
        {
            var data = references.GetCachedComponent<AIDataProvider>();
            return data != null ? data.GetArmorLevel() : 0;
        }
    }

    public class AmmoLevelSensor : LocalWorldSensorBase
    {
        public override void Created() { }
        public override void Update() { }
        public override SenseValue Sense(IActionReceiver agent, IComponentReference references)
        {
            var data = references.GetCachedComponent<AIDataProvider>();
            return data != null ? data.GetAmmoLevel() : 0;
        }
    }

    public class IsReloadingSensor : LocalWorldSensorBase
    {
        public override void Created() { }
        public override void Update() { }
        public override SenseValue Sense(IActionReceiver agent, IComponentReference references)
        {
            var data = references.GetCachedComponent<AIDataProvider>();
            return data != null && data.IsReloading();
        }
    }

    public class EnemyInSightSensor : LocalWorldSensorBase
    {
        public override void Created() { }
        public override void Update() { }
        public override SenseValue Sense(IActionReceiver agent, IComponentReference references)
        {
            var data = references.GetCachedComponent<AIDataProvider>();
            return data != null && data.IsEnemyInSight();
        }
    }

    public class HasKnownEnemySensor : LocalWorldSensorBase
    {
        public override void Created() { }
        public override void Update() { }
        public override SenseValue Sense(IActionReceiver agent, IComponentReference references)
        {
            var data = references.GetCachedComponent<AIDataProvider>();
            return data != null && data.HasKnownEnemy();
        }
    }

    public class IsUnderFireSensor : LocalWorldSensorBase
    {
        public override void Created() { }
        public override void Update() { }
        public override SenseValue Sense(IActionReceiver agent, IComponentReference references)
        {
            var data = references.GetCachedComponent<AIDataProvider>();
            return data != null && data.IsUnderFire();
        }
    }

    public class EnemyMarkedNearbySensor : LocalWorldSensorBase
    {
        public override void Created() { }
        public override void Update() { }
        public override SenseValue Sense(IActionReceiver agent, IComponentReference references)
        {
            var data = references.GetCachedComponent<AIDataProvider>();
            return data != null && data.GetNearestMarkedEnemy() != null;
        }
    }

    public class SelfIsMarkedSensor : LocalWorldSensorBase
    {
        public override void Created() { }
        public override void Update() { }
        public override SenseValue Sense(IActionReceiver agent, IComponentReference references)
        {
            var data = references.GetCachedComponent<AIDataProvider>();
            return data != null && data.IsSelfMarked();
        }
    }

    public class SelfMarkImmuneSensor : LocalWorldSensorBase
    {
        public override void Created() { }
        public override void Update() { }
        public override SenseValue Sense(IActionReceiver agent, IComponentReference references)
        {
            var data = references.GetCachedComponent<AIDataProvider>();
            return data != null && data.IsMarkImmune();
        }
    }

    public class SensorActiveSensor : LocalWorldSensorBase
    {
        public override void Created() { }
        public override void Update() { }
        public override SenseValue Sense(IActionReceiver agent, IComponentReference references)
        {
            var data = references.GetCachedComponent<AIDataProvider>();
            return data != null && data.HasFriendlySensor();
        }
    }

    public class AtCapturePointSensor : LocalWorldSensorBase
    {
        public override void Created() { }
        public override void Update() { }
        public override SenseValue Sense(IActionReceiver agent, IComponentReference references)
        {
            var data = references.GetCachedComponent<AIDataProvider>();
            return data != null && data.IsAtCapturePoint();
        }
    }

    public class IsSafeSensor : LocalWorldSensorBase
    {
        public override void Created() { }
        public override void Update() { }
        public override SenseValue Sense(IActionReceiver agent, IComponentReference references)
        {
            var data = references.GetCachedComponent<AIDataProvider>();
            return data != null && data.IsInGarrison();
        }
    }

    public class SquadLeaderAliveSensor : LocalWorldSensorBase
    {
        public override void Created() { }
        public override void Update() { }
        public override SenseValue Sense(IActionReceiver agent, IComponentReference references)
        {
            var data = references.GetCachedComponent<AIDataProvider>();
            return data != null && data.GetSquadLeader() != null;
        }
    }

    public class AllyDownedSensor : LocalWorldSensorBase
    {
        public override void Created() { }
        public override void Update() { }
        public override SenseValue Sense(IActionReceiver agent, IComponentReference references)
        {
            var data = references.GetCachedComponent<AIDataProvider>();
            return data != null && data.GetNearestDownedAlly() != null;
        }
    }

    // Continuous-behaviour sensors: always 0, so the goal is never "already met"
    // and the resolver keeps the agent performing the corresponding action.
    public class IsPatrollingSensor : LocalWorldSensorBase
    {
        public override void Created() { }
        public override void Update() { }
        public override SenseValue Sense(IActionReceiver agent, IComponentReference references) => 0;
    }

    public class IsWithSquadSensor : LocalWorldSensorBase
    {
        public override void Created() { }
        public override void Update() { }
        public override SenseValue Sense(IActionReceiver agent, IComponentReference references) => 0;
    }
}
