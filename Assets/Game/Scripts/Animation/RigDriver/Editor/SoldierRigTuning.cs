using System.Globalization;
using System.Reflection;
using HagenDa.Networking;
using UnityEditor;
using UnityEngine;

namespace HagenDa.Animation.RigDriver.EditorTools
{
    /// <summary>
    /// PHASE14 rig 调参落盘（幂等）。把推荐值写进预制体，解决
    /// "脚本默认值改了但预制体上序列化的旧值仍生效"的问题。
    ///
    /// **只填未手调的字段**（本工具的核心约定）：
    /// 判定某个字段能否写入，按以下顺序（三方比对）：
    ///   1. 本工具**上次写过**什么（记录在 EditorPrefs，按预制体 GUID + 类型 + 字段名索引）
    ///      - 当前值 == 上次写入值 → 说明该字段仍由本工具管辖 → **可写入新推荐值**。
    ///        这样改了脚本里的推荐常量后，重跑本菜单能正常传播到预制体。
    ///      - 当前值 != 上次写入值 → 有人手调过 → **跳过并报告**。
    ///   2. 没有记录（首次运行/换了机器）→ 退化为"与脚本默认值比对"：
    ///      - 当前值 == 脚本字段默认值 → 从未被调过 → 可写入。
    ///      - 否则 → 视作手调值 → 跳过。
    ///
    /// 于是：手调过的值不会被覆盖；未调过的字段仍能被正确填上。
    /// 需要无条件重置时用 `Apply Rig Tuning (Force Overwrite)`。
    /// </summary>
    public static class SoldierRigTuning
    {
        const string FatuiPath = "Assets/Game/Characters/Fatui/Fatui with Collider.prefab";
        const string PlayerPrefabPath = "Assets/Game/Prefabs/NetworkPlayer.prefab";
        const string AiPrefabPath = "Assets/Game/Prefabs/AIEntity.prefab";

        // 推荐值（改这里后重跑菜单即可把新值传播到仍由本工具管辖的字段）
        public static readonly Vector3 BellyTarget = new Vector3(0f, 0.28f, 0f);
        public const float BodyPivotHeight = 0f;          // 0 = 运行时自动取腹部/骨盆高度
        public const bool UseRestFootPlacement = true;
        public const float FootHeightProne = 0.03f;
        // 蹲姿（含蹲行）脚 IK 权重上限：满权重，全程触地。蹲下时模型根下沉
        // crouchOffset(-0.5m) 而剪辑仍是站立高度 → 沿用移动上限 0.15 会让脚穿地 ~0.44m。
        public const float FootIKCapCrouch = 1f;
        // 蹲走程序化腿部解算（约束的 IK 目标取自自身输出会把脚钉死：步幅 0.767→0.031m）
        public const bool UseCrouchWalkLegs = true;
        // 蹲走抬脚量上限（摆动幅度上限；目标 Y 是绝对值，不靠它补偿蹲降量）
        public const float CrouchLiftMax = 0.35f;
        public const float SprintLateralOffset = -0.07f;
        public const float SprintHeightOffset = 0f;
        public const float SprintDropback = 0.06f;
        public const float SprintBlendSpeed = 6f;
        public const float ProneCapsuleLength = 1.85f;
        public const float ProneCapsuleForward = -0.02f;
        public const float NearClipNormal = 0.3f;
        public const float NearClipAiming = 0.01f;
        public const float DownLookDetachStart = 55f;
        public const float DownLookDetachMax = 0.5f;

        [MenuItem("HagenDa/SoldierAnim/Apply Rig Tuning")]
        public static void Apply() => ApplyInternal(false);

        [MenuItem("HagenDa/SoldierAnim/Apply Rig Tuning (Force Overwrite)")]
        public static void ApplyForce() => ApplyInternal(true);

        // =================================================================
        // 主流程
        // =================================================================

        static void ApplyInternal(bool force)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("[SoldierTuning] ").Append(force ? "强制覆盖模式. " : "仅填补未手调字段. ");
            int totalWritten = 0, totalSkipped = 0;

            // ---- 1) Fatui 预制体 / SoldierRigDriver ----
            var probeDrv = MakeProbe<SoldierRigDriver>();
            try
            {
                int w = 0, s = 0;
                if (!EditPrefab(FatuiPath, contents =>
                {
                    var drv = contents.GetComponent<SoldierRigDriver>();
                    if (drv == null)
                    {
                        Debug.LogError($"[SoldierTuning] {FatuiPath} 上没有 SoldierRigDriver —— 先运行 Bake Fatui Rig");
                        return false;
                    }
                    var log = new System.Text.StringBuilder();
                    Fill(drv, probeDrv, FatuiPath, nameof(SoldierRigDriver.bodyPivotHeight), BodyPivotHeight, force, ref w, ref s, log);
                    Fill(drv, probeDrv, FatuiPath, nameof(SoldierRigDriver.proneBellyTarget), BellyTarget, force, ref w, ref s, log);
                    Fill(drv, probeDrv, FatuiPath, nameof(SoldierRigDriver.useRestFootPlacement), UseRestFootPlacement, force, ref w, ref s, log);
                    Fill(drv, probeDrv, FatuiPath, nameof(SoldierRigDriver.footHeightProne), FootHeightProne, force, ref w, ref s, log);
                    Fill(drv, probeDrv, FatuiPath, nameof(SoldierRigDriver.footIKCapCrouch), FootIKCapCrouch, force, ref w, ref s, log);
                    Fill(drv, probeDrv, FatuiPath, nameof(SoldierRigDriver.useCrouchWalkLegs), UseCrouchWalkLegs, force, ref w, ref s, log);
                    Fill(drv, probeDrv, FatuiPath, nameof(SoldierRigDriver.crouchLiftMax), CrouchLiftMax, force, ref w, ref s, log);
                    Fill(drv, probeDrv, FatuiPath, nameof(SoldierRigDriver.sprintLateralOffset), SprintLateralOffset, force, ref w, ref s, log);
                    Fill(drv, probeDrv, FatuiPath, nameof(SoldierRigDriver.sprintHeightOffset), SprintHeightOffset, force, ref w, ref s, log);
                    Fill(drv, probeDrv, FatuiPath, nameof(SoldierRigDriver.sprintDropback), SprintDropback, force, ref w, ref s, log);
                    Fill(drv, probeDrv, FatuiPath, nameof(SoldierRigDriver.sprintBlendSpeed), SprintBlendSpeed, force, ref w, ref s, log);
                    if (log.Length > 0) sb.Append("Fatui[").Append(log.ToString().TrimEnd()).Append("] ");
                    return true;
                }, w > 0 || force))
                {
                    sb.Append("Fatui 写入失败. ");
                }
                totalWritten += w; totalSkipped += s;
            }
            finally { DestroyProbe(probeDrv); }

            // ---- 2) 实体预制体 / NetworkPlayerController ----
            var probeCtrl = MakeProbe<NetworkPlayerController>();
            try
            {
                foreach (var path in new[] { PlayerPrefabPath, AiPrefabPath })
                {
                    int w = 0, s = 0;
                    string file = System.IO.Path.GetFileName(path);
                    EditPrefab(path, contents =>
                    {
                        var ctrl = contents.GetComponent<NetworkPlayerController>();
                        if (ctrl == null) { sb.Append(file).Append(" 无 NetworkPlayerController. "); return false; }
                        var log = new System.Text.StringBuilder();
                        Fill(ctrl, probeCtrl, path, nameof(NetworkPlayerController.proneCapsuleLength), ProneCapsuleLength, force, ref w, ref s, log);
                        Fill(ctrl, probeCtrl, path, nameof(NetworkPlayerController.proneCapsuleForward), ProneCapsuleForward, force, ref w, ref s, log);
                        Fill(ctrl, probeCtrl, path, nameof(NetworkPlayerController.nearClipNormal), NearClipNormal, force, ref w, ref s, log);
                        Fill(ctrl, probeCtrl, path, nameof(NetworkPlayerController.nearClipAiming), NearClipAiming, force, ref w, ref s, log);
                        Fill(ctrl, probeCtrl, path, nameof(NetworkPlayerController.downLookDetachStart), DownLookDetachStart, force, ref w, ref s, log);
                        Fill(ctrl, probeCtrl, path, nameof(NetworkPlayerController.downLookDetachMax), DownLookDetachMax, force, ref w, ref s, log);
                        if (log.Length > 0) sb.Append(file).Append('[').Append(log.ToString().TrimEnd()).Append("] ");
                        return true;
                    }, w > 0 || force);
                    totalWritten += w; totalSkipped += s;
                }
            }
            finally { DestroyProbe(probeCtrl); }

            AssetDatabase.SaveAssets();
            sb.Append("写入=").Append(totalWritten).Append(" 跳过(已手调)=").Append(totalSkipped).Append('。');
            if (totalSkipped > 0 && !force)
                sb.Append("如需重置被跳过的字段,改用 HagenDa/SoldierAnim/Apply Rig Tuning (Force Overwrite)。");
            Debug.Log(sb.ToString());
        }

        /// <summary>加载预制体内容 → 回调修改 → 仅在有写入时保存回盘。</summary>
        static bool EditPrefab(string path, System.Func<GameObject, bool> edit, bool save)
        {
            var contents = PrefabUtility.LoadPrefabContents(path);
            if (contents == null) { Debug.LogError($"[SoldierTuning] 找不到 {path}"); return false; }
            bool ok;
            try
            {
                ok = edit(contents);
                if (ok && save) PrefabUtility.SaveAsPrefabAsset(contents, path);
            }
            finally { PrefabUtility.UnloadPrefabContents(contents); }
            return ok;
        }

        // =================================================================
        // 只填未手调的字段
        // =================================================================

        /// <summary>
        /// 按"三方比对"决定是否写入（见类注释）。可写则写入推荐值并登记记录；
        /// 不可写则跳过并写进 log（供使用者确认哪些值被保留）。
        /// </summary>
        static void Fill(object target, object probe, string prefabPath, string field, object recommended,
                         bool force, ref int written, ref int skipped, System.Text.StringBuilder log)
        {
            var fi = target.GetType().GetField(field, BindingFlags.Public | BindingFlags.Instance);
            if (fi == null) { log.Append("缺字段:").Append(field).Append(' '); return; }

            object cur = fi.GetValue(target);
            string key = RecordKey(prefabPath, target.GetType().Name, field);
            string record = EditorPrefs.GetString(key, string.Empty);

            bool claimable;
            if (force) claimable = true;
            else if (!string.IsNullOrEmpty(record)) claimable = Serialize(cur) == record;
            else claimable = probe != null && ValueEquals(cur, fi.GetValue(probe));

            if (!claimable)
            {
                skipped++;
                log.Append("跳 ").Append(field).Append('=').Append(Serialize(cur)).Append("(已手调) ");
                return;
            }

            fi.SetValue(target, recommended);
            EditorPrefs.SetString(key, Serialize(recommended));
            written++;
        }

        static string RecordKey(string prefabPath, string typeName, string field)
            => "HagenDa.RigTuning." + AssetDatabase.AssetPathToGUID(prefabPath) + "." + typeName + "." + field;

        static bool ValueEquals(object a, object b)
        {
            if (a == null || b == null) return ReferenceEquals(a, b);
            if (a is float fa && b is float fb) return Mathf.Approximately(fa, fb);
            if (a is Vector3 va && b is Vector3 vb) return (va - vb).sqrMagnitude < 1e-8f;
            return a.Equals(b);
        }

        /// <summary>稳定可比的字符串形式（用于记录比对，必须与设备区域设置无关）。</summary>
        static string Serialize(object v)
        {
            if (v is float f) return f.ToString("R", CultureInfo.InvariantCulture);
            if (v is bool b) return b ? "1" : "0";
            if (v is Vector3 vec) return string.Format(CultureInfo.InvariantCulture, "{0:R},{1:R},{2:R}", vec.x, vec.y, vec.z);
            return v?.ToString() ?? "null";
        }

        // =================================================================
        // 默认值探针
        // =================================================================

        /// <summary>
        /// 取脚本字段默认值：新建一个**未激活**的临时物体再 AddComponent ——
        /// 未激活可确保不触发 Awake/OnEnable（否则 SoldierRigDriver 会因找不到
        /// Animator 而刷警告），字段初始化器仍会执行，读到的即脚本声明的默认值。
        ///
        /// NetworkBehaviour 子类（NetworkPlayerController）必须先进 NetworkIdentity，
        /// 否则 Mirror 的 OnValidate 会报 "requires a NetworkIdentity"。
        /// </summary>
        static T MakeProbe<T>() where T : Component
        {
            var go = new GameObject("__HagenDaRigTuningProbe") { hideFlags = HideFlags.HideAndDontSave };
            go.SetActive(false);
            if (typeof(T).IsSubclassOf(typeof(Mirror.NetworkBehaviour)))
                go.AddComponent<Mirror.NetworkIdentity>();
            return go.AddComponent<T>();
        }

        static void DestroyProbe(Component c)
        {
            if (c != null) UnityEngine.Object.DestroyImmediate(c.gameObject);
        }
    }
}
