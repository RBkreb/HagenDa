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
    /// NetworkSetup (partial) — Soldier model attach and Fatui rig/hitbox baking.
    /// </summary>
    public static partial class NetworkSetup
    {
                [MenuItem("HagenDa/Setup Soldier Models")]
                public static void SetupSoldierModels()
                {
                    EnsureSoldierHitboxParts(NatlanSoldierPath);
                    BuildFatuiCombatRig();

                    // Rebuild every entity prefab so the models propagate to all scenes
                    // (FSM/Scripted prefabs are saved copies, not variants — rebuild them too).
                    GameObject grenadePrefab = BuildGrenadePrefab();
                    GameObject smokePrefab = BuildSmokePrefab();
                    GameObject rescuePrefab = BuildRescuePrefab();
                    GameObject bulletPrefab = BuildBulletPrefab();
                    List<EquipmentDefinition> equipmentList = BuildEquipmentAssets();

                    BuildPlayerPrefab(grenadePrefab, smokePrefab, rescuePrefab, bulletPrefab, equipmentList);
                    GameObject aiPrefab = BuildAIEntityPrefab(grenadePrefab, smokePrefab, rescuePrefab, bulletPrefab, equipmentList);
                    BuildFSMAIPrefab(aiPrefab);
                    BuildScriptedAIPrefab(aiPrefab);

                    AssetDatabase.SaveAssets();
                    Debug.Log("[NetworkSetup] Soldier models wired: Natlan hitbox parts, Fatui rig, entity prefabs rebuilt.");
                }

                private static void EnsureSoldierHitboxParts(string prefabPath)
                {
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                    if (prefab == null)
                    {
                        Debug.LogWarning($"[NetworkSetup] Soldier prefab not found at {prefabPath}");
                        return;
                    }

                    bool changed = false;
                    foreach (var col in prefab.GetComponentsInChildren<Collider>(true))
                    {
                        var part = PartFromName(col.gameObject.name);
                        if (part == null) continue;

                        var marker = col.GetComponent<NetworkHitbox>();
                        if (marker == null)
                        {
                            marker = col.gameObject.AddComponent<NetworkHitbox>();
                            changed = true;
                        }
                        if (marker.part != part.Value)
                        {
                            marker.part = part.Value;
                            changed = true;
                        }
                    }
                    if (changed) EditorUtility.SetDirty(prefab);
                }

                private static HitboxPart? PartFromName(string name)
                {
                    string n = name.ToLowerInvariant();
                    if (n.Contains("head")) return HitboxPart.Head;
                    if (n.Contains("arm") || n.Contains("leg")) return HitboxPart.Limb;
                    if (n.Contains("body")) return HitboxPart.Body;
                    return null;
                }

                private static void BuildFatuiCombatRig()
                {
                    var fatui = AssetDatabase.LoadAssetAtPath<GameObject>(FatuiSoldierPath);
                    var natlan = AssetDatabase.LoadAssetAtPath<GameObject>(NatlanSoldierPath);
                    if (fatui == null || natlan == null)
                    {
                        Debug.LogWarning("[NetworkSetup] Soldier prefab missing; skip Fatui rig build.");
                        return;
                    }

                    // Idempotent: already rigged.
                    if (fatui.GetComponentInChildren<NetworkHitbox>(true) != null &&
                        fatui.GetComponentInChildren<TwoBoneIKConstraint>(true) != null)
                        return;

                    var contents = PrefabUtility.LoadPrefabContents(FatuiSoldierPath);
                    try
                    {
                        float natlanHeight, fatuiHeight;
                        RenderHeight(natlan, out natlanHeight);
                        RenderHeight(contents, out fatuiHeight);
                        float heightRatio = natlanHeight > 0.01f ? fatuiHeight / natlanHeight : 1f;
                        float scaleComp = (100f / 70f) * heightRatio;

                        // 1) Hitboxes.
                        int made = 0;
                        foreach (var col in natlan.GetComponentsInChildren<Collider>(true))
                        {
                            var part = PartFromName(col.gameObject.name);
                            if (part == null) continue;

                            var bone = FindChildByName(contents.transform, col.transform.parent.name);
                            if (bone == null)
                            {
                                Debug.LogWarning($"[NetworkSetup] Fatui bone '{col.transform.parent.name}' not found for {col.name}.");
                                continue;
                            }

                            var go = new GameObject(col.gameObject.name);
                            go.transform.SetParent(bone, false);
                            go.transform.localPosition = col.transform.localPosition;
                            go.transform.localRotation = col.transform.localRotation;
                            go.transform.localScale = col.transform.localScale * scaleComp;

                            Collider dst;
                            var sphere = col as SphereCollider;
                            if (sphere != null)
                            {
                                var s = go.AddComponent<SphereCollider>();
                                s.center = sphere.center;
                                s.radius = sphere.radius;
                                dst = s;
                            }
                            else
                            {
                                var srcC = (CapsuleCollider)col;
                                var c = go.AddComponent<CapsuleCollider>();
                                c.center = srcC.center;
                                c.radius = srcC.radius;
                                c.height = srcC.height;
                                c.direction = srcC.direction;
                                dst = c;
                            }
                            dst.isTrigger = true;
                            go.AddComponent<NetworkHitbox>().part = part.Value;
                            made++;
                        }

                        // 2) Weapon + hand IK (same rig as Natlan: handlers on the gun).
                        var nM4 = FindChildByName(natlan.transform, "M4_8");
                        if (nM4 != null)
                        {
                            var m4Prefab = AssetDatabase.LoadAssetAtPath<GameObject>(M4PrefabPath);
                            GameObject m4 = null;
                            if (m4Prefab != null)
                            {
                                m4 = (GameObject)PrefabUtility.InstantiatePrefab(m4Prefab);
                                m4.name = "M4_8";
                                m4.transform.SetParent(contents.transform, false);
                                m4.transform.localPosition = nM4.localPosition * heightRatio;
                                m4.transform.localRotation = nM4.localRotation;
                                m4.transform.localScale = Vector3.one;
                            }

                            if (m4 != null)
                            {
                                AttachCopy(m4.transform, nM4.transform, "RightHandler");
                                AttachCopy(m4.transform, nM4.transform, "LeftHandler");
                            }

                            AttachCopy(contents.transform, natlan.transform, "RightHint", heightRatio);
                            AttachCopy(contents.transform, natlan.transform, "LeftHint", heightRatio);

                            var twoHandRig = new GameObject("TwoHandRig");
                            twoHandRig.transform.SetParent(contents.transform, false);
                            var rigComponent = twoHandRig.AddComponent<Rig>();
                            rigComponent.weight = 1f;

                            BuildHandIK(twoHandRig, contents.transform, "RightHandRig",
                                m4 != null ? m4.transform.Find("RightHandler") : null,
                                FindChildByName(contents.transform, "RightHint"),
                                "DEF-upper_arm.R", "DEF-forearm.R", "DEF-hand.R", 1f);
                            BuildHandIK(twoHandRig, contents.transform, "LeftHandRig",
                                m4 != null ? m4.transform.Find("LeftHandler") : null,
                                FindChildByName(contents.transform, "LeftHint"),
                                "DEF-upper_arm.L", "DEF-forearm.L", "DEF-hand.L", 0.9f);

                            var rigBuilder = contents.GetComponent<RigBuilder>();
                            if (rigBuilder == null) rigBuilder = contents.AddComponent<RigBuilder>();
                            rigBuilder.layers.Add(new RigLayer(rigComponent));
                        }

                        PrefabUtility.SaveAsPrefabAsset(contents, FatuiSoldierPath);
                        Debug.Log($"[NetworkSetup] Fatui rig built: {made} hitboxes + M4 hand IK (scale comp x{scaleComp:F2}).");
                    }
                    finally
                    {
                        PrefabUtility.UnloadPrefabContents(contents);
                    }
                }

                private static void BuildHandIK(GameObject parent, Transform modelRoot, string rigName,
                                                 Transform target, Transform hint,
                                                 string rootBone, string midBone, string tipBone, float hintWeight)
                {
                    var rigGo = new GameObject(rigName);
                    rigGo.transform.SetParent(parent.transform, false);
                    var ik = rigGo.AddComponent<TwoBoneIKConstraint>();
                    var d = ik.data;
                    d.root = FindChildByName(modelRoot, rootBone);
                    d.mid = FindChildByName(modelRoot, midBone);
                    d.tip = FindChildByName(modelRoot, tipBone);
                    d.target = target;
                    d.hint = hint;
                    d.targetRotationWeight = 1f;
                    d.targetPositionWeight = 1f;
                    d.hintWeight = hintWeight;
                    ik.data = d;
                }

                private static void AttachCopy(Transform newParent, Transform sourceRoot, string childName, float posScale = 1f)
                {
                    var src = FindChildByName(sourceRoot, childName);
                    if (src == null)
                    {
                        Debug.LogWarning($"[NetworkSetup] Source transform '{childName}' not found.");
                        return;
                    }
                    var go = new GameObject(childName);
                    go.transform.SetParent(newParent, false);
                    go.transform.localPosition = src.localPosition * posScale;
                    go.transform.localRotation = src.localRotation;
                    go.transform.localScale = src.localScale;
                }

                private static void RenderHeight(GameObject prefab, out float height)
                {
                    bool has = false;
                    Bounds all = default;
                    foreach (var r in prefab.GetComponentsInChildren<Renderer>(true))
                    {
                        if (!has) { all = r.bounds; has = true; }
                        else all.Encapsulate(r.bounds);
                    }
                    height = has ? all.size.y : 0f;
                }

                private static Transform FindChildByName(Transform root, string name)
                {
                    if (root.name == name) return root;
                    foreach (Transform c in root)
                    {
                        var r = FindChildByName(c, name);
                        if (r != null) return r;
                    }
                    return null;
                }

                private static void AttachSoldierModels(GameObject root)
                {
                    var soldierAnimator = root.GetComponent<NetworkSoldierAnimator>();
                    if (soldierAnimator == null) soldierAnimator = root.AddComponent<NetworkSoldierAnimator>();

                    soldierAnimator.redModel = AttachTeamModel(root, NatlanSoldierPath, "RedSoldierModel", 0f);
                    soldierAnimator.blueModel = AttachTeamModel(root, FatuiSoldierPath, "BlueSoldierModel", 0.08f);
                    // PHASE14:队伍未指定(单人验证场景 teamId<0)时显示 Fatui —— 它是唯一带
                    // 完整 PHASE14 rig(HipsPose/SpineAim/FootIK/Hands)的模型;Natlan 无。
                    soldierAnimator.defaultModel = soldierAnimator.blueModel;

                    // PHASE14 角色结构:胶囊根 -> [角色模型 + 枪械基准(同级)] -> 枪模。
                    // 枪械基准挂在实体根(动画层级之外),双手 IK 目标读到的是实时 Transform。
                    EnsureWeaponBasis(root);

                    // PHASE13: force the self-made NetworkSoldier controller (guards against
                    // the model prefabs reverting to the vendor demo controller).
                    var soldierCtrl = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(SoldierControllerPath);
                    if (soldierCtrl != null)
                    {
                        foreach (var model in new[] { soldierAnimator.redModel, soldierAnimator.blueModel })
                        {
                            if (model == null) continue;
                            var modelAnim = model.GetComponentInChildren<Animator>(true);
                            if (modelAnim != null)
                            {
                                modelAnim.runtimeAnimatorController = soldierCtrl;
                                modelAnim.applyRootMotion = false;   // force-driven rigidbody
                            }
                        }
                    }

                    // Default to the red model; NetworkSoldierAnimator switches by teamId
                    // (SyncVar) on the first update. Avoids a one-frame double-model overlap.
                    if (soldierAnimator.blueModel != null) soldierAnimator.blueModel.SetActive(false);

                    // Editor-only bone gizmos: off on the spawned instances.
                    foreach (var br in root.GetComponentsInChildren<BoneRenderer>(true))
                        br.enabled = false;

                    var body = root.transform.Find("Body");
                    if (body != null)
                    {
                        var r = body.GetComponent<Renderer>();
                        if (r != null) r.enabled = false;
                    }
                }

                private static Transform EnsureWeaponBasis(GameObject root)
                {
                    var basis = root.transform.Find("WeaponBasis");
                    if (basis == null)
                    {
                        var go = new GameObject("WeaponBasis");
                        go.transform.SetParent(root.transform, false);
                        basis = go.transform;
                    }
                    basis.localPosition = Vector3.zero;
                    basis.localRotation = Quaternion.identity;

                    if (basis.childCount > 0) return basis;   // 已有枪

                    var gunPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(M4PrefabPath);
                    if (gunPrefab == null) return basis;
                    var gun = (GameObject)PrefabUtility.InstantiatePrefab(gunPrefab);
                    gun.name = "M4_8";
                    gun.transform.SetParent(basis, false);
                    gun.transform.localPosition = Vector3.zero;
                    gun.transform.localRotation = Quaternion.identity;
                    gun.transform.localScale = Vector3.one;
                    // 枪自带 AimConstraint 在 rig 之后求值会造成握把锚点滞后,停用
                    var ac = gun.GetComponent<AimConstraint>();
                    if (ac == null) ac = gun.AddComponent<AimConstraint>();
                    ac.constraintActive = false;
                    return basis;
                }

                private static GameObject AttachTeamModel(GameObject root, string prefabPath, string name, float yOffset)
                {
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                    if (prefab == null)
                    {
                        Debug.LogWarning($"[NetworkSetup] Soldier model not found at {prefabPath}");
                        return null;
                    }

                    var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                    inst.name = name;
                    inst.transform.SetParent(root.transform, false);
                    inst.transform.localPosition = new Vector3(0f, yOffset, 0f);
                    inst.transform.localRotation = Quaternion.identity;
                    inst.transform.localScale = Vector3.one;
                    return inst;
                }
    }
}
