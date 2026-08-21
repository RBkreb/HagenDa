using CrashKonijn.Goap.Runtime;

namespace HagenDa.Networking.AI
{
    // ============================================================
    // Target keys for the PHASE9 GOAP AI. Each key references a
    // Vector3 position in the world (via a PositionTarget or
    // TransformTarget produced by a TargetSensor).
    // ============================================================

    public class NearestEnemy : TargetKeyBase { }
    public class NearestMarkedEnemy : TargetKeyBase { }
    public class NearestCapturePoint : TargetKeyBase { }
    public class NearestOwnedPoint : TargetKeyBase { }
    public class NearestGarrison : TargetKeyBase { }
    public class NearestSupply : TargetKeyBase { }
    public class NearestDownedAlly : TargetKeyBase { }
    public class NearestLowAmmoAlly : TargetKeyBase { }
    public class NearestEnemyDeployable : TargetKeyBase { }
    public class SquadLeaderPos : TargetKeyBase { }
    public class WanderPoint : TargetKeyBase { }
}
