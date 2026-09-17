namespace HagenDa.Networking.EditorTools
{
    /// <summary>
    /// Editor toolkit for building HagenDa's networked scenes and prefabs.
    ///
    /// This class is <c>partial</c>; the members are grouped by concern across
    /// sibling files so no single file carries the whole toolkit:
    ///
    ///   NetworkSetupPaths.cs             — path constants, folder / layer / dirty helpers
    ///   EditorScenePrimitives.cs         — spawns, walls, lighting, zones, map geometry, NavMesh
    ///   EditorPrefabFactory.cs           — player / AI / bullet / throwable / deployable prefabs
    ///   EditorEquipmentFactory.cs        — equipment, weapon and loadout assets, materials
    ///   EditorSoldierModelFactory.cs     — soldier model attach, Fatui rig + hitbox bake
    ///   Builders/MultiplayerSceneBuilder.cs — multiplayer / physics / animation-test / solo scenes
    ///   Builders/TrainingSceneBuilder.cs    — ML training and S1 training scenes
    ///   Builders/BattleSceneBuilder.cs      — FSM battle scenes (59 AI, all-support, HGTR, Map_v1)
    ///   Builders/PhaseSceneBuilder.cs       — Phase3/5/6/7/8 and match scenes
    ///
    /// Entry points are the <c>HagenDa/*</c> menu items in Builders/.
    /// </summary>
    public static partial class NetworkSetup
    {
    }
}
