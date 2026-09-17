using System.Collections.Generic;
using System.Linq;
using Mirror;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Animations;
using UnityEngine.Animations.Rigging;
using UnityEngine.SceneManagement;
using UnityEngine.InputSystem.UI;
using Unity.AI.Navigation;
using HagenDa.Animation.RigGraph;

namespace HagenDa.Networking.EditorTools
{
    /// <summary>
    /// NetworkSetup (partial) — Asset/scene path constants and folder, layer and dirty-flag helpers.
    /// </summary>
    public static partial class NetworkSetup
    {
                private const string PrefabPath = "Assets/Game/Prefabs/NetworkPlayer.prefab";

                private const string GrenadePrefabPath = "Assets/Game/Prefabs/GrenadeThrowable.prefab";

                private const string SmokePrefabPath = "Assets/Game/Prefabs/SmokeThrowable.prefab";

                private const string RescuePrefabPath = "Assets/Game/Prefabs/RescueThrowable.prefab";

                private const string BulletPrefabPath = "Assets/Game/Prefabs/Bullet.prefab";

                private const string AIPrefabPath = "Assets/Game/Prefabs/AIEntity.prefab";

                private const string WeaponFolder = "Assets/Game/Weapons";

                private const string M4DefinitionPath = "Assets/Game/Weapons/M4Definition.asset";

                private const string M4PrefabPath = "Assets/Game/Characters/Guns/M4_8.prefab";

                private const string NatlanSoldierPath = "Assets/Game/Characters/natlan/Natlan Soldier FBX with collider.prefab";

                private const string FatuiSoldierPath = "Assets/Game/Characters/Fatui/Fatui with Collider.prefab";

                private const string SoldierControllerPath = "Assets/Game/Animation/NetworkSoldierLayers.controller";

                private const string EquipmentFolder = "Assets/Game/Equipment";

                private const string EmpFieldPrefabPath = "Assets/Game/Prefabs/EmpField.prefab";

                private const string HandGrenadePrefabPath = "Assets/Game/Prefabs/HandGrenade.prefab";

                private const string SmokeGrenadePrefabPath = "Assets/Game/Prefabs/SmokeGrenade.prefab";

                private const string EmpGrenadePrefabPath = "Assets/Game/Prefabs/EmpGrenadeThrowable.prefab";

                private const string LauncherGrenadePrefabPath = "Assets/Game/Prefabs/LauncherGrenade.prefab";

                private const string LauncherSmokePrefabPath = "Assets/Game/Prefabs/LauncherSmoke.prefab";

                private const string RpgPrefabPath = "Assets/Game/Prefabs/Rpg.prefab";

                private const string SignalChargePrefabPath = "Assets/Game/Prefabs/SignalCharge.prefab";

                private const string WiredChargePrefabPath = "Assets/Game/Prefabs/WiredCharge.prefab";

                private const string DelayedBombPrefabPath = "Assets/Game/Prefabs/DelayedBomb.prefab";

                private const string SupplyPackPrefabPath = "Assets/Game/Prefabs/SupplyPack.prefab";

                private const string LargeSupplyCratePrefabPath = "Assets/Game/Prefabs/LargeSupplyCrate.prefab";

                private const string InterceptorPrefabPath = "Assets/Game/Prefabs/Interceptor.prefab";

                private const string SensorProbePrefabPath = "Assets/Game/Prefabs/SensorProbe.prefab";

                private const string DeployBeaconPrefabPath = "Assets/Game/Prefabs/DeployBeacon.prefab";

                private const string RgdModelPath = "Assets/ThirdParty/Low Poly Weapons VOL.1/Prefabs/RGD-5.prefab";

                private const string SmokeModelPath = "Assets/ThirdParty/Low Poly Weapons VOL.1/Prefabs/Smoke.prefab";

                private const string FlashModelPath = "Assets/ThirdParty/Low Poly Weapons VOL.1/Prefabs/Flash.prefab";

                private const string RpgModelPath = "Assets/ThirdParty/Low Poly Weapons VOL.1/Prefabs/RPG7.prefab";

                private const string AnimationTestScenePath = "Assets/Game/Scenes/AnimationTest.scene";

                private const string MirrorBodyLayer = "MirrorBody";

                private const string SoldierSoloScenePath = "Assets/Game/Scenes/SoldierSoloFPS.scene";

                private const string TrainingScenePath = "Assets/Game/Scenes/TrainingArena.scene";

                private const string TrainingMapsFolder = "Assets/Game/TrainingMaps";

                private const string S1TrainingScenePath = "Assets/Game/Scenes/S1Training.scene";

                private const string FSMAIPrefabPath = "Assets/Game/Prefabs/FSMAIEntity.prefab";

                private const string HGTRMapRootName = "HGTR_map";

                internal static void EnsureLayerNamed(string layerName)
                {
                    var tagManager = new SerializedObject(
                        AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
                    var layersProp = tagManager.FindProperty("layers");
                    EnsureLayerAt(layersProp, layerName);
                    tagManager.ApplyModifiedProperties();
                }

                internal static void EnsureFolder(string parent, string folder)
                {
                    string full = parent + "/" + folder;
                    if (!AssetDatabase.IsValidFolder(full))
                        AssetDatabase.CreateFolder(parent, folder);
                }

                internal static void EnsureMapLayers()
                {
                    var tagManager = new SerializedObject(
                        AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);

                    var layersProp = tagManager.FindProperty("layers");
                    EnsureLayerAt(layersProp, MapLayers.IndicatorName);
                    EnsureLayerAt(layersProp, MapLayers.HighlightName);
                    EnsureLayerAt(layersProp, MapLayers.ZoneName);
                    EnsureLayerAt(layersProp, MapLayers.ZoneOutlineName);
                    tagManager.ApplyModifiedProperties();
                }

                private static void EnsureLayerAt(SerializedProperty layersProp, string layerName)
                {
                    // Layers 0-7 are reserved (built-in). Insert into the first empty slot.
                    for (int i = 8; i < layersProp.arraySize; i++)
                    {
                        var slot = layersProp.GetArrayElementAtIndex(i);
                        if (string.IsNullOrEmpty(slot.stringValue))
                        {
                            slot.stringValue = layerName;
                            return;
                        }
                        if (slot.stringValue == layerName)
                            return;   // already present
                    }
                }

                private static void MarkSceneDirty()
                {
                    var scene = SceneManager.GetActiveScene();
                    UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
                }

                internal static void SaveActiveScene()
                {
                    var scene = SceneManager.GetActiveScene();
                    UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene);
                }
    }
}
