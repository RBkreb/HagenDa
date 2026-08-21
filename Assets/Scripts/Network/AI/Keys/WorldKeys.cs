using CrashKonijn.Goap.Runtime;

namespace HagenDa.Networking.AI
{
    // ============================================================
    // World keys for the PHASE9 GOAP AI. Every value is an int.
    // Each key is a marker type; its name is used to match it in the
    // ScriptableObject configuration (CapabilityConfigScriptable).
    // ============================================================

    // ---- Basic status ----
    public class HealthLevel : WorldKeyBase { }
    public class ArmorLevel : WorldKeyBase { }
    public class AmmoLevel : WorldKeyBase { }
    public class IsReloading : WorldKeyBase { }

    // ---- Enemy awareness ----
    public class EnemyInSight : WorldKeyBase { }
    public class HasKnownEnemy : WorldKeyBase { }
    public class IsUnderFire : WorldKeyBase { }
    public class EnemyMarkedNearby : WorldKeyBase { }

    // ---- Mark state ----
    public class SelfIsMarked : WorldKeyBase { }
    public class SelfMarkImmune : WorldKeyBase { }
    public class SensorActive : WorldKeyBase { }

    // ---- Objective / position ----
    public class AtCapturePoint : WorldKeyBase { }
    public class IsSafe : WorldKeyBase { }
    public class SquadLeaderAlive : WorldKeyBase { }
    public class AllyDowned : WorldKeyBase { }

    // ---- Loadout / equipment (Has* = has ammo available) ----
    public class HasGrenade : WorldKeyBase { }
    public class HasSmokeGrenade : WorldKeyBase { }
    public class HasEmpGrenade : WorldKeyBase { }
    public class HasGrenadeLauncher : WorldKeyBase { }
    public class HasSmokeLauncher : WorldKeyBase { }
    public class HasRpg : WorldKeyBase { }
    public class HasSupplyPack : WorldKeyBase { }
    public class HasSupplyCrate : WorldKeyBase { }
    public class HasInterceptor : WorldKeyBase { }
    public class HasSensorEquip : WorldKeyBase { }
    public class HasQuickDash : WorldKeyBase { }
    public class HasJammer : WorldKeyBase { }
    public class HasArmorPlate : WorldKeyBase { }
    public class HasHealingSyringe : WorldKeyBase { }
    public class HasDefibrillator : WorldKeyBase { }
}
