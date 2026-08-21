using CrashKonijn.Agent.Core;
using CrashKonijn.Goap.Core;
using CrashKonijn.Goap.Runtime;

namespace HagenDa.Networking.AI
{
    /// <summary>
    /// PHASE9 GOAP configuration (code-based). Replaces the ScriptableObject path:
    /// the GOAP ClassScanner uses MonoScript.GetClass() (one class per file), which
    /// would force ~80 single-class files. The builder API registers every goal /
    /// action / sensor by generic type instead, matching the project's code-driven
    /// setup (NetworkSetup.cs).
    ///
    /// Attach this to the GoapBehaviour GameObject (or any GOAP runner) and add it to
    /// the GoapBehaviour.agentTypeConfigFactories list.
    /// </summary>
    public class CombatantAgentTypeFactory : AgentTypeFactoryBase
    {
        public override IAgentTypeConfig Create()
        {
            var builder = this.CreateBuilder("Combatant");

            builder.CreateCapability("Combat", capability =>
            {
                // ============================================================
                // GOALS
                // ============================================================
                capability.AddGoal<SurviveGoal>()
                    .AddCondition<IsSafe>(Comparison.GreaterThanOrEqual, 1);

                capability.AddGoal<TakeCoverGoal>()
                    .AddCondition<IsInCover>(Comparison.GreaterThanOrEqual, 1);

                capability.AddGoal<RescueAllyGoal>()
                    .AddCondition<AllyRescued>(Comparison.GreaterThanOrEqual, 1);

                capability.AddGoal<ResupplyGoal>()
                    .AddCondition<AmmoLevel>(Comparison.GreaterThanOrEqual, 50)
                    .AddCondition<HealthLevel>(Comparison.GreaterThanOrEqual, 50);

                capability.AddGoal<CaptureObjectiveGoal>()
                    .AddCondition<AtCapturePoint>(Comparison.GreaterThanOrEqual, 1);

                capability.AddGoal<DefendObjectiveGoal>()
                    .AddCondition<AtCapturePoint>(Comparison.GreaterThanOrEqual, 1);

                capability.AddGoal<EliminateEnemyGoal>()
                    .AddCondition<HasKnownEnemy>(Comparison.SmallerThanOrEqual, 0);

                capability.AddGoal<PatrolGoal>()
                    .AddCondition<IsPatrolling>(Comparison.GreaterThanOrEqual, 1);

                // ============================================================
                // ACTIONS — combat
                // ============================================================
                capability.AddAction<AttackEnemyAction>()
                    .SetTarget<NearestEnemy>()
                    .AddCondition<HasKnownEnemy>(Comparison.GreaterThanOrEqual, 1)
                    .AddEffect<HasKnownEnemy>(EffectType.Decrease)
                    .SetBaseCost(5f)
                    .SetStoppingDistance(15f)
                    .SetMoveMode(ActionMoveMode.PerformWhileMoving);

                capability.AddAction<ReloadAction>()
                    .AddCondition<IsReloading>(Comparison.SmallerThanOrEqual, 0)
                    .AddCondition<AmmoLevel>(Comparison.SmallerThan, 30)
                    .AddEffect<AmmoLevel>(EffectType.Increase)
                    .SetBaseCost(2f)
                    .SetRequiresTarget(false)
                    .SetStoppingDistance(0f);

                capability.AddAction<ThrowGrenadeAction>()
                    .SetTarget<NearestEnemy>()
                    .AddCondition<HasGrenade>(Comparison.GreaterThanOrEqual, 1)
                    .AddCondition<HasKnownEnemy>(Comparison.GreaterThanOrEqual, 1)
                    .AddEffect<HasGrenade>(EffectType.Decrease)
                    .SetBaseCost(8f)
                    .SetStoppingDistance(30f);

                capability.AddAction<FireGrenadeLauncherAction>()
                    .SetTarget<NearestEnemy>()
                    .AddCondition<HasGrenadeLauncher>(Comparison.GreaterThanOrEqual, 1)
                    .AddCondition<HasKnownEnemy>(Comparison.GreaterThanOrEqual, 1)
                    .AddEffect<HasGrenadeLauncher>(EffectType.Decrease)
                    .SetBaseCost(6f)
                    .SetStoppingDistance(40f);

                capability.AddAction<FireRpgAction>()
                    .SetTarget<NearestEnemy>()
                    .AddCondition<HasRpg>(Comparison.GreaterThanOrEqual, 1)
                    .AddCondition<HasKnownEnemy>(Comparison.GreaterThanOrEqual, 1)
                    .AddEffect<HasRpg>(EffectType.Decrease)
                    .SetBaseCost(5f)
                    .SetStoppingDistance(50f);

                capability.AddAction<ThrowEmpGrenadeAction>()
                    .SetTarget<NearestEnemyDeployable>()
                    .AddCondition<HasEmpGrenade>(Comparison.GreaterThanOrEqual, 1)
                    .AddEffect<HasEmpGrenade>(EffectType.Decrease)
                    .SetBaseCost(7f)
                    .SetStoppingDistance(25f);

                // ============================================================
                // ACTIONS — support
                // ============================================================
                capability.AddAction<ThrowSmokeAction>()
                    .AddCondition<HasSmokeGrenade>(Comparison.GreaterThanOrEqual, 1)
                    .AddCondition<IsUnderFire>(Comparison.GreaterThanOrEqual, 1)
                    .AddEffect<HasSmokeGrenade>(EffectType.Decrease)
                    .SetBaseCost(6f)
                    .SetRequiresTarget(false)
                    .SetStoppingDistance(0f);

                capability.AddAction<FireSmokeLauncherAction>()
                    .AddCondition<HasSmokeLauncher>(Comparison.GreaterThanOrEqual, 1)
                    .AddCondition<IsUnderFire>(Comparison.GreaterThanOrEqual, 1)
                    .AddEffect<HasSmokeLauncher>(EffectType.Decrease)
                    .SetBaseCost(6f)
                    .SetRequiresTarget(false)
                    .SetStoppingDistance(0f);

                capability.AddAction<ThrowSupplyPackAction>()
                    .SetTarget<NearestLowAmmoAlly>()
                    .AddCondition<HasSupplyPack>(Comparison.GreaterThanOrEqual, 1)
                    .AddEffect<HasSupplyPack>(EffectType.Decrease)
                    .SetBaseCost(5f)
                    .SetStoppingDistance(15f);

                // ============================================================
                // ACTIONS — deploy
                // ============================================================
                capability.AddAction<DeploySupplyCrateAction>()
                    .AddCondition<HasSupplyCrate>(Comparison.GreaterThanOrEqual, 1)
                    .AddEffect<HasSupplyCrate>(EffectType.Decrease)
                    .AddEffect<AmmoLevel>(EffectType.Increase)
                    .AddEffect<HealthLevel>(EffectType.Increase)
                    .SetBaseCost(4f)
                    .SetRequiresTarget(false)
                    .SetStoppingDistance(0f);

                capability.AddAction<DeployInterceptorAction>()
                    .AddCondition<HasInterceptor>(Comparison.GreaterThanOrEqual, 1)
                    .AddEffect<HasInterceptor>(EffectType.Decrease)
                    .SetBaseCost(6f)
                    .SetRequiresTarget(false)
                    .SetStoppingDistance(0f);

                capability.AddAction<DeploySensorAction>()
                    .AddCondition<HasSensorEquip>(Comparison.GreaterThanOrEqual, 1)
                    .AddCondition<SensorActive>(Comparison.SmallerThanOrEqual, 0)
                    .AddEffect<HasSensorEquip>(EffectType.Decrease)
                    .SetBaseCost(6f)
                    .SetRequiresTarget(false)
                    .SetStoppingDistance(0f);

                // ============================================================
                // ACTIONS — self
                // ============================================================
                capability.AddAction<UseJammerAction>()
                    .AddCondition<HasJammer>(Comparison.GreaterThanOrEqual, 1)
                    .AddCondition<SelfIsMarked>(Comparison.GreaterThanOrEqual, 1)
                    .AddEffect<HasJammer>(EffectType.Decrease)
                    .AddEffect<SelfMarkImmune>(EffectType.Increase)
                    .SetBaseCost(3f)
                    .SetRequiresTarget(false)
                    .SetStoppingDistance(0f);

                capability.AddAction<QuickDashAction>()
                    .AddCondition<HasQuickDash>(Comparison.GreaterThanOrEqual, 1)
                    .AddCondition<IsUnderFire>(Comparison.GreaterThanOrEqual, 1)
                    .AddEffect<HasQuickDash>(EffectType.Decrease)
                    .SetBaseCost(4f)
                    .SetRequiresTarget(false)
                    .SetStoppingDistance(0f);

                capability.AddAction<ApplyArmorPlateAction>()
                    .AddCondition<HasArmorPlate>(Comparison.GreaterThanOrEqual, 1)
                    .AddCondition<ArmorLevel>(Comparison.SmallerThan, 10)
                    .AddEffect<HasArmorPlate>(EffectType.Decrease)
                    .AddEffect<ArmorLevel>(EffectType.Increase)
                    .SetBaseCost(5f)
                    .SetRequiresTarget(false)
                    .SetStoppingDistance(0f);

                capability.AddAction<UseHealingSyringeAction>()
                    .AddCondition<HasHealingSyringe>(Comparison.GreaterThanOrEqual, 1)
                    .AddCondition<HealthLevel>(Comparison.SmallerThan, 50)
                    .AddEffect<HasHealingSyringe>(EffectType.Decrease)
                    .AddEffect<HealthLevel>(EffectType.Increase)
                    .SetBaseCost(2f)
                    .SetRequiresTarget(false)
                    .SetStoppingDistance(0f);

                capability.AddAction<UseDefibrillatorAction>()
                    .SetTarget<NearestDownedAlly>()
                    .AddCondition<HasDefibrillator>(Comparison.GreaterThanOrEqual, 1)
                    .AddCondition<AllyDowned>(Comparison.GreaterThanOrEqual, 1)
                    .AddEffect<HasDefibrillator>(EffectType.Decrease)
                    .AddEffect<AllyRescued>(EffectType.Increase)
                    .SetBaseCost(3f)
                    .SetStoppingDistance(2f)
                    .SetMoveMode(ActionMoveMode.PerformWhileMoving);

                // ============================================================
                // ACTIONS — movement
                // ============================================================
                capability.AddAction<MoveToCapturePointAction>()
                    .SetTarget<NearestCapturePoint>()
                    .AddEffect<AtCapturePoint>(EffectType.Increase)
                    .SetBaseCost(3f)
                    .SetStoppingDistance(3f);

                capability.AddAction<MoveToDefendPointAction>()
                    .SetTarget<NearestOwnedPoint>()
                    .AddEffect<AtCapturePoint>(EffectType.Increase)
                    .SetBaseCost(3f)
                    .SetStoppingDistance(3f);

                capability.AddAction<RetreatToGarrisonAction>()
                    .SetTarget<NearestGarrison>()
                    .AddCondition<HealthLevel>(Comparison.SmallerThan, 35)
                    .AddEffect<IsSafe>(EffectType.Increase)
                    .SetBaseCost(1f)
                    .SetStoppingDistance(3f);

                capability.AddAction<MoveToCoverAction>()
                    .SetTarget<CoverPosition>()
                    .AddCondition<HealthLevel>(Comparison.SmallerThan, 35)
                    .AddEffect<IsInCover>(EffectType.Increase)
                    .SetBaseCost(1f)
                    .SetStoppingDistance(1f)
                    .SetMoveMode(ActionMoveMode.PerformWhileMoving);

                capability.AddAction<MoveToSupplyAction>()
                    .SetTarget<NearestSupply>()
                    .AddCondition<AmmoLevel>(Comparison.SmallerThan, 50)
                    .AddEffect<AmmoLevel>(EffectType.Increase)
                    .AddEffect<HealthLevel>(EffectType.Increase)
                    .SetBaseCost(4f)
                    .SetStoppingDistance(3f);

                capability.AddAction<WanderAction>()
                    .SetTarget<WanderPoint>()
                    .AddEffect<IsPatrolling>(EffectType.Increase)
                    .SetBaseCost(10f)
                    .SetStoppingDistance(1f);

                // ============================================================
                // WORLD SENSORS (local)
                // ============================================================
                capability.AddWorldSensor<HealthLevelSensor>().SetKey<HealthLevel>();
                capability.AddWorldSensor<ArmorLevelSensor>().SetKey<ArmorLevel>();
                capability.AddWorldSensor<AmmoLevelSensor>().SetKey<AmmoLevel>();
                capability.AddWorldSensor<IsReloadingSensor>().SetKey<IsReloading>();
                capability.AddWorldSensor<EnemyInSightSensor>().SetKey<EnemyInSight>();
                capability.AddWorldSensor<HasKnownEnemySensor>().SetKey<HasKnownEnemy>();
                capability.AddWorldSensor<IsUnderFireSensor>().SetKey<IsUnderFire>();
                capability.AddWorldSensor<EnemyMarkedNearbySensor>().SetKey<EnemyMarkedNearby>();
                capability.AddWorldSensor<SelfIsMarkedSensor>().SetKey<SelfIsMarked>();
                capability.AddWorldSensor<SelfMarkImmuneSensor>().SetKey<SelfMarkImmune>();
                capability.AddWorldSensor<SensorActiveSensor>().SetKey<SensorActive>();
                capability.AddWorldSensor<AtCapturePointSensor>().SetKey<AtCapturePoint>();
                capability.AddWorldSensor<IsSafeSensor>().SetKey<IsSafe>();
                capability.AddWorldSensor<SquadLeaderAliveSensor>().SetKey<SquadLeaderAlive>();
                capability.AddWorldSensor<AllyDownedSensor>().SetKey<AllyDowned>();
                capability.AddWorldSensor<IsPatrollingSensor>().SetKey<IsPatrolling>();
                capability.AddWorldSensor<IsInCoverSensor>().SetKey<IsInCover>();
                capability.AddWorldSensor<AllyRescuedSensor>().SetKey<AllyRescued>();

                // ============================================================
                // TARGET SENSORS (local)
                // ============================================================
                capability.AddTargetSensor<NearestEnemySensor>().SetTarget<NearestEnemy>();
                capability.AddTargetSensor<NearestMarkedEnemySensor>().SetTarget<NearestMarkedEnemy>();
                capability.AddTargetSensor<NearestCapturePointSensor>().SetTarget<NearestCapturePoint>();
                capability.AddTargetSensor<NearestOwnedPointSensor>().SetTarget<NearestOwnedPoint>();
                capability.AddTargetSensor<NearestGarrisonSensor>().SetTarget<NearestGarrison>();
                capability.AddTargetSensor<NearestSupplySensor>().SetTarget<NearestSupply>();
                capability.AddTargetSensor<NearestDownedAllySensor>().SetTarget<NearestDownedAlly>();
                capability.AddTargetSensor<NearestLowAmmoAllySensor>().SetTarget<NearestLowAmmoAlly>();
                capability.AddTargetSensor<NearestEnemyDeployableSensor>().SetTarget<NearestEnemyDeployable>();
                capability.AddTargetSensor<SquadLeaderPosSensor>().SetTarget<SquadLeaderPos>();
                capability.AddTargetSensor<WanderPointSensor>().SetTarget<WanderPoint>();
                capability.AddTargetSensor<CoverPositionSensor>().SetTarget<CoverPosition>();

                // ============================================================
                // MULTI SENSOR (equipment Has* keys)
                // ============================================================
                capability.AddMultiSensor<EquipmentSensors>();
            });

            return builder.Build();
        }
    }
}
