using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Mirror;

namespace HagenDa.Networking
{
    /// <summary>
    /// PHASE11 指挥官武器系统（服务端权威）。三种武器共用"冷却 + 至多一条待投放
    /// 指令"的骨架：冷却中下达 → 排队（可重复下达更新坐标）；就绪瞬间自动投放。
    ///
    ///  1 广域侦测：半径50m 圆柱，持续20s，每5s 对范围内敌军标记1s；最后一次扫描
    ///    触发侦测完成事件；冷却120s。
    ///  2 广域电磁干扰：生成 NetworkEmpField(radius40/lifetime10/interfere10)；冷却180s。
    ///  3 炮击支援：半径30m 区域持续30s 每2s 随机上表面爆炸（中心150、半径8m），
    ///    只伤害敌方阵营（PHASE10 定案：对友军无伤害），击杀敌军 → 己方 +1 分；
    ///    冷却300s。
    ///
    /// PHASE11：坐标由 ToolContext 解析（格#N/据点编号 → 世界点）后传入，
    /// 本类不再持有任何空间换算引用。武器编号 LLM 口径 1..3。
    /// </summary>
    public class CommanderWeaponSystem : MonoBehaviour
    {
        private static readonly string[] Names =
            { "广域侦测", "广域电磁干扰", "炮击支援" };

        private CommanderConfig cfg;
        private int team;

        // 三武器统一状态。
        private readonly float[] readyAt = { 0f, 0f, 0f };
        private readonly bool[] hasPending = new bool[3];
        private readonly Vector3[] pendingWorld = new Vector3[3];
        private readonly string[] pendingDisplay = new string[3];
        private Coroutine[] running = new Coroutine[3];

        /// <summary>投放成功/自动投放事件（weaponNumber, 描述文本）→ 信息缓冲。</summary>
        public event Action<int, string> Deployed;

        /// <summary>广域侦测最后一次扫描完成（weaponNumber=1）。</summary>
        public event Action RadarFinalScan;

        public void Init(CommanderConfig cfg, int team)
        {
            this.cfg = cfg;
            this.team = team;
            for (int i = 0; i < 3; i++) readyAt[i] = Time.time;
        }

        // ---------------------------------------------------------------
        // 状态查询 / 下达指令
        // ---------------------------------------------------------------

        /// <summary>weapon_status 工具的输出：每行一件武器。</summary>
        public string DescribeStatus()
        {
            var lines = new List<string>();
            for (int i = 0; i < 3; i++)
            {
                float remain = Mathf.Max(0f, readyAt[i] - Time.time);
                string tail;
                if (hasPending[i])
                {
                    tail = remain > 0f
                        ? $"冷却{remain:F0}秒后自动投放 @{pendingDisplay[i]}"
                        : $"投放中 @{pendingDisplay[i]}";
                }
                else
                {
                    tail = remain > 0f ? $"冷却{remain:F0}秒" : "可用";
                }
                lines.Add($"{i + 1}{Names[i]}:{tail}");
            }
            return string.Join(";", lines);
        }

        /// <summary>commander_weapon 工具入口（PHASE11：目标已由 ToolContext 解析为世界点）。</summary>
        public string TryOrder(int weaponNumber, Vector3 world, string display)
        {
            // 硬闸：门控阶段无论调用来源（LLM/脚本）一律拒绝。
            if (NetworkCommanderState.GateActive)
                return "错误:对局尚未开始，武器不可用";

            int idx = weaponNumber - 1;
            if (idx < 0 || idx >= 3)
                return $"错误:未知武器编号 {weaponNumber}（可用 1-3）";

            float remain = Mathf.Max(0f, readyAt[idx] - Time.time);
            pendingWorld[idx] = world;
            pendingDisplay[idx] = string.IsNullOrEmpty(display)
                ? $"({world.x:F0},{world.z:F0})"
                : display;

            string at = pendingDisplay[idx];
            if (remain <= 0f)
            {
                Execute(idx);
                return $"投放成功:{Names[idx]} @{at}";
            }

            hasPending[idx] = true;
            return $"冷却中({remain:F0}秒)，已排队并将于冷却结束后自动投放 @{at}"
                 + ";可再次下达同编号指令以更新坐标";
        }

        private void Update()
        {
            if (!NetworkServer.active) return;

            for (int i = 0; i < 3; i++)
            {
                if (!hasPending[i]) continue;
                if (Time.time < readyAt[i]) continue;
                hasPending[i] = false;
                Execute(i);
            }
        }

        private void Execute(int idx)
        {
            // 启动冷却（此前缺失：导致武器永远"可用"，WTest 实测暴露）
            readyAt[idx] = Time.time + CooldownOf(idx);

            var world = pendingWorld[idx];
            var gridTxt = $"@{pendingDisplay[idx]}";

            switch (idx)
            {
                case 0:
                    if (running[0] != null) StopCoroutine(running[0]);
                    running[0] = StartCoroutine(RadarRoutine(world));
                    break;
                case 1:
                    if (running[1] != null) StopCoroutine(running[1]);
                    running[1] = StartCoroutine(EmpRoutine(world));
                    break;
                case 2:
                    if (running[2] != null) StopCoroutine(running[2]);
                    running[2] = StartCoroutine(ArtilleryRoutine(world));
                    break;
            }

            Deployed?.Invoke(idx + 1,
                $"武器[{Names[idx]}]已投放 {gridTxt}，冷却{Mathf.RoundToInt(CooldownOf(idx))}秒");
        }

        private float CooldownOf(int idx)
        {
            switch (idx)
            {
                case 0: return cfg.radarCooldown;
                case 1: return cfg.empCooldown;
                default: return cfg.artilleryCooldown;
            }
        }

        // ---------------------------------------------------------------
        // 1 广域侦测
        // ---------------------------------------------------------------

        private IEnumerator RadarRoutine(Vector3 center)
        {
            int scans = Mathf.Max(1, Mathf.RoundToInt(cfg.radarDuration / cfg.radarScanEvery));
            int last = scans - 1;

            for (int s = 0; s < scans; s++)
            {
                ScanOnce(center);
                bool final = s == last;
                if (final && RadarFinalScan != null) RadarFinalScan();
                yield return new WaitForSeconds(s == last ? 0.5f : cfg.radarScanEvery);
            }
            running[0] = null;
        }

        private void ScanOnce(Vector3 center)
        {
            var buf = new List<NetworkCombatant>();
            NetworkMatchManager.GetAllCombatants(buf);
            double until = NetworkTime.time + cfg.radarMarkDuration;
            float r2 = cfg.radarRadius * cfg.radarRadius;

            foreach (var c in buf)
            {
                if (c == null || c.teamId == team || c.teamId < 0 || c.IsDead) continue;
                Vector3 p = c.transform.position;
                float dxz = (new Vector2(p.x - center.x, p.z - center.z)).sqrMagnitude;
                if (dxz <= r2 && p.y >= center.y - 20f && p.y <= center.y + 60f)
                    c.SetMarked(until, team, null);
            }
        }

        // ---------------------------------------------------------------
        // 2 广域电磁干扰
        // ---------------------------------------------------------------

        private IEnumerator EmpRoutine(Vector3 center)
        {
            var prefab = FindEmpPrefab();
            if (prefab != null)
            {
                var go = Instantiate(prefab,
                    new Vector3(center.x, center.y + 1f, center.z), Quaternion.identity);
                var field = go.GetComponent<NetworkEmpField>();
                if (field != null)
                {
                    field.radius = cfg.empRadius;
                    field.lifetime = cfg.empLifetime;
                    field.interfereDuration = cfg.empInterfereDuration;
                }
                NetworkServer.Spawn(go);
            }
            else
            {
                Debug.LogWarning("[CommanderWeapons] 未找到 EmpField.prefab，电磁干扰未生效");
            }
            yield break;
        }

        /// <summary>EMP 预制体：优先取 NetworkManager 注册表（运行时正确），回退直接加载。</summary>
        private static GameObject FindEmpPrefab()
        {
            const string path = "Assets/Game/Prefabs/EmpField.prefab";

            var nm = NetworkManager.singleton;
            if (nm != null && nm.spawnPrefabs != null)
            {
                foreach (var p in nm.spawnPrefabs)
                    if (p != null && p.name == "EmpField")
                        return p;
            }
#if UNITY_EDITOR
            return UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(path);
#else
            return null;
#endif
        }

        // ---------------------------------------------------------------
        // 3 炮击支援
        // ---------------------------------------------------------------

        private IEnumerator ArtilleryRoutine(Vector3 areaCenter)
        {
            int shells = Mathf.Max(1, Mathf.RoundToInt(cfg.artilleryDuration / cfg.artilleryEvery));
            for (int i = 0; i < shells; i++)
            {
                FireOneShell(areaCenter);
                yield return new WaitForSeconds(cfg.artilleryEvery);
            }
            running[2] = null;
        }

        private void FireOneShell(Vector3 areaCenter)
        {
            Vector2 circle = UnityEngine.Random.insideUnitCircle * cfg.artilleryAreaRadius;
            Vector3 flat = new Vector3(areaCenter.x + circle.x, 0f, areaCenter.z + circle.y);

            // 从上方射线取随机点的上表面。
            Vector3 top = flat + Vector3.up * 50f;
            if (Physics.Raycast(top, Vector3.down, out RaycastHit hit, 200f,
                                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                top = hit.point;
            else
                top = flat + Vector3.up * 0.5f;

            ExplosionUtility.SpawnVisual(top, cfg.artilleryBlastRadius);
            CommanderArtilleryDamage(top);
        }

        /// <summary>
        /// 敌方专属爆炸伤害：线性衰减到半径边缘归零；遮挡射线免疫（特殊掩体穿透）；
        /// 仅作用于敌军阵营；本次爆炸直接击杀数 → 己方队伍加分（AddScore 1/kill）。
        /// </summary>
        private void CommanderArtilleryDamage(Vector3 center)
        {
            var buf = new List<NetworkCombatant>();
            NetworkMatchManager.GetAllCombatants(buf);

            // 收集命中目标（先算后打，便于统计击杀差值）。
            var hits = new List<(NetworkPlayerHealth health, float dmg, Vector3 entityCenter)>();

            foreach (var c in buf)
            {
                if (c == null || c.teamId == team || c.teamId < 0 || c.IsDead) continue;
                var h = c.Health;
                if (h == null) continue;

                Vector3 ec = c.transform.position + Vector3.up * 0.9f;
                float dist = Vector3.Distance(ec, center);
                if (dist > cfg.artilleryBlastRadius) continue;

                // 遮挡检测：中心 → 实体 的射线上有别的物体则遮挡（特殊掩体穿透）。
                Vector3 dir = (ec - center) / Mathf.Max(0.001f, dist);
                bool blocked = false;
                var blockers = Physics.RaycastAll(center, dir, dist + 0.05f,
                                                  Physics.DefaultRaycastLayers,
                                                  QueryTriggerInteraction.Ignore);
                foreach (var bh in blockers)
                {
                    // 目标自身的碰撞体不算遮挡。
                    if (bh.collider.GetComponentInParent<NetworkPlayerHealth>() == h)
                        continue;
                    if (bh.collider.GetComponentInParent<SpecialCover>() != null)
                        continue;   // 特殊掩体可被爆炸穿透（PHASE6）
                    blocked = true;
                    break;
                }
                if (blocked) continue;

                float dmg = cfg.artilleryYield * (1f - dist / cfg.artilleryBlastRadius);
                if (dmg > 0f) hits.Add((h, dmg, ec));
            }

            int killedBefore = CountAlive(hits);
            foreach (var hit in hits)
                hit.health.TakeExplosionDamage(hit.dmg, null, center);
            int killedAfter = CountAlive(hits);

            int kills = Mathf.Max(0, killedBefore - killedAfter);
            if (kills > 0 && NetworkMatchManager.Instance != null)
                NetworkMatchManager.Instance.AddScore((MatchTeam)team, kills);
        }

        private static int CountAlive(List<(NetworkPlayerHealth health, float dmg, Vector3 ec)> hits)
        {
            int n = 0;
            foreach (var h in hits)
                if (h.health != null && !h.health.IsDead) n++;
            return n;
        }
    }
}
