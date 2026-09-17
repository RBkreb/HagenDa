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
    /// NetworkSetup (partial) — Equipment, weapon and loadout assets plus persistent materials.
    /// </summary>
    public static partial class NetworkSetup
    {
                private static Material CreatePersistentMaterial(string path, Color color, Color? emission = null)
                {
                    // NB: GetType().Name is "HDRenderPipelineAsset" (namespace not included);
                    // checking Name for "HighDefinition" always fails. Check FullName too.
                    var pipeline = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
                    bool hdrp = pipeline != null &&
                        (pipeline.GetType().FullName.Contains("HighDefinition") ||
                         pipeline.GetType().Name.Contains("HDRenderPipeline"));

                    Shader shader = hdrp ? Shader.Find("HDRP/Lit") : Shader.Find("Standard");
                    if (shader == null) shader = Shader.Find("Standard");
                    if (shader == null)
                    {
                        Debug.LogError("[NetworkSetup] No usable Lit shader found (tried HDRP/Lit, Standard).");
                        return null;
                    }

                    EnsureFolder("Assets/Game", "Materials");

                    var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                    if (mat == null || mat.shader != shader)
                    {
                        if (mat != null) AssetDatabase.DeleteAsset(path);
                        mat = new Material(shader);
                        AssetDatabase.CreateAsset(mat, path);
                    }

                    // HDRP uses _BaseColor; built-in uses _Color. Set both for safety.
                    mat.SetColor("_BaseColor", color);
                    mat.SetColor("_Color", color);

                    if (emission.HasValue)
                    {
                        if (hdrp)
                        {
                            mat.EnableKeyword("_EMISSIVE_COLOR_MAP"); // HDRP emission keyword
                        }
                        else
                        {
                            mat.EnableKeyword("_EMISSION");
                        }
                        mat.SetColor("_EmissionColor", emission.Value);
                    }

                    EditorUtility.SetDirty(mat);
                    return mat;
                }

                private static EquipmentDefinition GetOrCreateEquipmentDef(string fileName)
                {
                    EnsureFolder("Assets/Game", "Equipment");
                    string path = EquipmentFolder + "/" + fileName + ".asset";
                    var def = AssetDatabase.LoadAssetAtPath<EquipmentDefinition>(path);
                    if (def == null)
                    {
                        def = ScriptableObject.CreateInstance<EquipmentDefinition>();
                        def.name = fileName;
                        AssetDatabase.CreateAsset(def, path);
                    }
                    return def;
                }

                internal static List<EquipmentDefinition> BuildEquipmentAssets()
                {
                    // Build throwable prefabs first (idempotent), so equipment defs can
                    // reference them.
                    var handGrenade = BuildHandGrenadePrefab();
                    var smokeGrenade = BuildSmokeGrenadePrefab();
                    var empField = BuildEmpFieldPrefab();
                    var empGrenade = BuildEmpGrenadePrefab(empField);
                    var launcherGrenade = BuildLauncherGrenadePrefab();
                    var launcherSmoke = BuildLauncherSmokePrefab();
                    var rpg = BuildRpgPrefab();
                    var signal = BuildSignalChargePrefab();
                    var wired = BuildWiredChargePrefab();
                    var delayedBomb = BuildDelayedBombPrefab();
                    var supplyPack = BuildSupplyPackPrefab();

                    var list = new List<EquipmentDefinition>();

                    // 0. 手雷
                    var g = GetOrCreateEquipmentDef("HandGrenade");
                    g.type = EquipmentType.Grenade;
                    g.displayName = "手雷";
                    g.useStyle = EquipmentUseStyle.Throw;
                    g.maxCarry = 1; g.supplyCost = 100; g.throwSpeed = 15f;
                    g.throwablePrefab = handGrenade;
                    list.Add(g);

                    // 1. 烟雾手雷
                    var sg = GetOrCreateEquipmentDef("SmokeGrenade");
                    sg.type = EquipmentType.SmokeGrenade;
                    sg.displayName = "烟雾手雷";
                    sg.useStyle = EquipmentUseStyle.Throw;
                    sg.maxCarry = 2; sg.supplyCost = 80; sg.throwSpeed = 15f;
                    sg.throwablePrefab = smokeGrenade;
                    list.Add(sg);

                    // 2. 电磁手雷
                    var eg = GetOrCreateEquipmentDef("EmpGrenade");
                    eg.type = EquipmentType.EmpGrenade;
                    eg.displayName = "电磁手雷";
                    eg.useStyle = EquipmentUseStyle.Throw;
                    eg.maxCarry = 1; eg.supplyCost = 100; eg.throwSpeed = 15f;
                    eg.throwablePrefab = empGrenade;
                    eg.empRadius = 6f; eg.empLifetime = 10f; eg.empInterfereDuration = 2f;
                    list.Add(eg);

                    // 3. 榴弹炮
                    var gl = GetOrCreateEquipmentDef("GrenadeLauncher");
                    gl.type = EquipmentType.GrenadeLauncher;
                    gl.displayName = "榴弹炮";
                    gl.useStyle = EquipmentUseStyle.BoltLauncher;
                    gl.maxCarry = 5; gl.supplyCost = 100; gl.boltTime = 1.5f; gl.throwSpeed = 250f;
                    gl.throwablePrefab = launcherGrenade;
                    list.Add(gl);

                    // 4. 烟雾发射器
                    var sl = GetOrCreateEquipmentDef("SmokeLauncher");
                    sl.type = EquipmentType.SmokeLauncher;
                    sl.displayName = "烟雾发射器";
                    sl.useStyle = EquipmentUseStyle.BoltLauncher;
                    sl.maxCarry = 5; sl.supplyCost = 100; sl.boltTime = 1.5f; sl.throwSpeed = 250f;
                    sl.throwablePrefab = launcherSmoke;
                    list.Add(sl);

                    // 5. RPG
                    var r = GetOrCreateEquipmentDef("Rpg");
                    r.type = EquipmentType.Rpg;
                    r.displayName = "RPG";
                    r.useStyle = EquipmentUseStyle.BoltLauncher;
                    r.maxCarry = 5; r.supplyCost = 120; r.boltTime = 2f; r.throwSpeed = 750f;
                    r.throwablePrefab = rpg;
                    list.Add(r);

                    // 6. 信号炸药（EMP 可禁）
                    var sc = GetOrCreateEquipmentDef("SignalCharge");
                    sc.type = EquipmentType.SignalCharge;
                    sc.displayName = "信号炸药";
                    sc.useStyle = EquipmentUseStyle.RemoteCharge;
                    sc.maxCarry = 3; sc.supplyCost = 120; sc.throwSpeed = 3f;
                    sc.empVulnerable = true;
                    sc.throwablePrefab = signal;
                    list.Add(sc);

                    // 7. 线控炸药（EMP 不可禁）
                    var wc = GetOrCreateEquipmentDef("WiredCharge");
                    wc.type = EquipmentType.WiredCharge;
                    wc.displayName = "线控炸药";
                    wc.useStyle = EquipmentUseStyle.RemoteCharge;
                    wc.maxCarry = 3; wc.supplyCost = 120; wc.throwSpeed = 2f;
                    wc.empVulnerable = false;
                    wc.throwablePrefab = wired;
                    list.Add(wc);

                    // 8. 延时炸弹（EMP 可禁）
                    var db = GetOrCreateEquipmentDef("DelayedBomb");
                    db.type = EquipmentType.DelayedBomb;
                    db.displayName = "延时炸弹";
                    db.useStyle = EquipmentUseStyle.Throw;
                    db.maxCarry = 2; db.supplyCost = 120; db.throwSpeed = 3f;
                    db.empVulnerable = true;
                    db.throwablePrefab = delayedBomb;
                    list.Add(db);

                    // 9. 小型补给包（每 10s 自动 +1）
                    var sp = GetOrCreateEquipmentDef("SmallSupplyPack");
                    sp.type = EquipmentType.SmallSupplyPack;
                    sp.displayName = "小型补给包";
                    sp.useStyle = EquipmentUseStyle.Throw;
                    sp.maxCarry = 3; sp.supplyCost = 100; sp.throwSpeed = 3f;
                    sp.ammoRegenInterval = 10f;
                    sp.throwablePrefab = supplyPack;
                    list.Add(sp);

                    // 10. 大型补给箱（Deploy）
                    var lc = GetOrCreateEquipmentDef("LargeSupplyCrate");
                    lc.type = EquipmentType.LargeSupplyCrate;
                    lc.displayName = "大型补给箱";
                    lc.useStyle = EquipmentUseStyle.Deploy;
                    lc.maxCarry = 1; lc.supplyCost = 100; lc.ammoRegenInterval = 10f;
                    lc.throwablePrefab = BuildLargeSupplyCratePrefab();
                    list.Add(lc);

                    // 11. 拦截系统（Deploy，部署上限 3）
                    var it = GetOrCreateEquipmentDef("Interceptor");
                    it.type = EquipmentType.Interceptor;
                    it.displayName = "拦截系统";
                    it.useStyle = EquipmentUseStyle.Deploy;
                    it.maxCarry = 1; it.supplyCost = 100;
                    it.deployCap = 3;
                    it.throwablePrefab = BuildInterceptorPrefab();
                    list.Add(it);

                    // 11b. 感应器（特有，Deploy，瞬发型，部署上限 1，30s 回复，EMP 可摧毁）
                    var sn = GetOrCreateEquipmentDef("Sensor");
                    sn.type = EquipmentType.Sensor;
                    sn.displayName = "感应器";
                    sn.useStyle = EquipmentUseStyle.Deploy;
                    sn.maxCarry = 1; sn.supplyCost = 0;
                    sn.ammoRegenInterval = 30f;
                    sn.empVulnerable = true;
                    sn.deployCap = 1;
                    sn.throwablePrefab = BuildSensorProbePrefab();
                    list.Add(sn);

                    // 11c. 部署信标（可选，Deploy，瞬发型，部署上限 1，同小队重部署点，
                    //      5 次用尽自毁，EMP 可摧毁，重部署时保留）
                    var bn = GetOrCreateEquipmentDef("DeployBeacon");
                    bn.type = EquipmentType.DeployBeacon;
                    bn.displayName = "部署信标";
                    bn.useStyle = EquipmentUseStyle.Deploy;
                    bn.maxCarry = 1; bn.supplyCost = 150;
                    bn.empVulnerable = true;
                    bn.persistOnRedeploy = true;
                    bn.throwablePrefab = BuildDeployBeaconPrefab();
                    list.Add(bn);

                    // 12. 快速机动装置（EMP 可禁）
                    var qd = GetOrCreateEquipmentDef("QuickDash");
                    qd.type = EquipmentType.QuickDash;
                    qd.displayName = "快速机动装置";
                    qd.useStyle = EquipmentUseStyle.SelfInstant;
                    qd.maxCarry = 1; qd.supplyCost = 0; qd.dashCooldown = 15f;
                    qd.empVulnerable = true;
                    list.Add(qd);

                    // 13. 护甲板
                    var ap = GetOrCreateEquipmentDef("ArmorPlate");
                    ap.type = EquipmentType.ArmorPlate;
                    ap.displayName = "护甲板";
                    ap.useStyle = EquipmentUseStyle.SelfChannel;
                    ap.maxCarry = 2; ap.supplyCost = 80; ap.channelTime = 1.5f; ap.armorGrant = 25f;
                    list.Add(ap);

                    // 14. 治疗针（特有）
                    var hs = GetOrCreateEquipmentDef("HealingSyringe");
                    hs.type = EquipmentType.HealingSyringe;
                    hs.displayName = "治疗针";
                    hs.useStyle = EquipmentUseStyle.SelfChannel;
                    hs.maxCarry = 2; hs.supplyCost = 80; hs.channelTime = 1f;
                    list.Add(hs);

                    // 15. 除颤仪（特有，每 3s +1）
                    var df = GetOrCreateEquipmentDef("Defibrillator");
                    df.type = EquipmentType.Defibrillator;
                    df.displayName = "除颤仪";
                    df.useStyle = EquipmentUseStyle.TargetChannel;
                    df.maxCarry = 3; df.supplyCost = 0; df.channelTime = 2f; df.ammoRegenInterval = 3f;
                    list.Add(df);

                    // 16. 防爆盾（batch B 填充 throwablePrefab / 组件）
                    var bs = GetOrCreateEquipmentDef("BlastShield");
                    bs.type = EquipmentType.BlastShield;
                    bs.displayName = "防爆盾";
                    bs.useStyle = EquipmentUseStyle.ShieldToggle;
                    bs.maxCarry = 1; bs.supplyCost = 0; bs.shieldExplosionReduction = 0.6f;
                    list.Add(bs);

                    // 17. 干扰器（可选，瞬发清除自身标记 + 30s 免疫标记，免疫结束后
                    //     30s 冷却恢复 1 次，EMP 可禁）
                    var jm = GetOrCreateEquipmentDef("Jammer");
                    jm.type = EquipmentType.Jammer;
                    jm.displayName = "干扰器";
                    jm.useStyle = EquipmentUseStyle.SelfInstant;
                    jm.maxCarry = 1; jm.supplyCost = 0;
                    jm.empVulnerable = true;
                    list.Add(jm);

                    // PHASE8: 按类型统一赋值 category + instantUse + deployCap。
                    foreach (var d in list)
                    {
                        if (d == null) continue;

                        switch (d.type)
                        {
                            case EquipmentType.HealingSyringe:
                            case EquipmentType.Defibrillator:
                            case EquipmentType.Sensor:
                                d.category = EquipmentCategory.Special;
                                break;
                            case EquipmentType.Grenade:
                            case EquipmentType.SmokeGrenade:
                            case EquipmentType.EmpGrenade:
                                d.category = EquipmentCategory.Throwable;
                                break;
                            default:
                                d.category = EquipmentCategory.Optional;
                                break;
                        }

                        switch (d.type)
                        {
                            case EquipmentType.LargeSupplyCrate:
                            case EquipmentType.SmallSupplyPack:
                            case EquipmentType.QuickDash:
                            case EquipmentType.HealingSyringe:
                            case EquipmentType.ArmorPlate:
                            case EquipmentType.Grenade:   // 手雷：瞬发型（z 键直接投掷）
                            case EquipmentType.SmokeGrenade:   // 烟雾手雷：瞬发型
                            case EquipmentType.EmpGrenade:     // 电磁手雷：瞬发型
                            case EquipmentType.Sensor:         // 感应器：瞬发型特有（g 键直接部署）
                            case EquipmentType.DeployBeacon:   // 部署信标：瞬发型（放置即部署）
                            case EquipmentType.Jammer:         // 干扰器：瞬发型（slot 键直接使用）
                                d.instantUse = true;
                                break;
                            default:
                                d.instantUse = false;
                                break;
                        }

                        switch (d.type)
                        {
                            case EquipmentType.LargeSupplyCrate:
                                d.deployCap = 1;
                                break;
                            case EquipmentType.SmallSupplyPack:
                            case EquipmentType.Interceptor:
                            case EquipmentType.SignalCharge:
                            case EquipmentType.WiredCharge:
                                d.deployCap = 3;
                                break;
                            case EquipmentType.Sensor:
                                d.deployCap = 1;
                                break;
                            case EquipmentType.DeployBeacon:   // 部署信标：单人同时仅 1 个
                                d.deployCap = 1;
                                break;
                            default:
                                d.deployCap = 0;
                                break;
                        }
                    }

                    foreach (var d in list)
                        if (d != null) EditorUtility.SetDirty(d);

                    return list;
                }

                internal static WeaponDefinition BuildM4Definition()
                {
                    EnsureFolder("Assets/Game", "Weapons");

                    var def = AssetDatabase.LoadAssetAtPath<WeaponDefinition>(M4DefinitionPath);
                    if (def == null)
                    {
                        def = ScriptableObject.CreateInstance<WeaponDefinition>();
                        def.name = "M4Definition";
                        AssetDatabase.CreateAsset(def, M4DefinitionPath);
                    }

                    EditorUtility.SetDirty(def);
                    return def;
                }
    }
}
