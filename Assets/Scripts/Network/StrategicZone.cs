using System.Collections.Generic;
using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// 战略要地快照（ML 训练共识 §3 全局观测块）。纯数据：观测侧只读 Registry，
    /// 不绑定真实据点机制——未来指挥官系统可下发任意无争夺机制的要地
    /// （如"镇守某处高地"），对 AI 而言与争夺点同构。
    /// </summary>
    public struct StrategicZoneState
    {
        public Vector3 position;    // 世界坐标（观测侧自行换算相对量并归一化）
        public float radius;
        public int ownerTeam;       // -1 中立, 0 红, 1 蓝
        public float contest;       // -1 (红全占) .. +1 (蓝全占)，无争夺机制的纯要地恒 0
    }

    /// <summary>
    /// Scene-level registry of strategic zones (ML 训练共识 §3)。自动聚合场景中
    /// 所有 <see cref="StrategicZone"/>；观测收集器每决策步调
    /// <see cref="GetZones"/> 拿快照。非网络对象：仅服务端训练读取。
    /// </summary>
    public class StrategicZoneRegistry : MonoBehaviour
    {
        public static StrategicZoneRegistry Instance { get; private set; }

        /// <summary>观测编码的最大要地数（共识：全局块 ~15 维预算内）。</summary>
        public const int MaxZones = 3;

        private readonly List<StrategicZone> zones = new List<StrategicZone>();

        private void Awake()
        {
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        public void Register(StrategicZone zone)
        {
            if (zone != null && !zones.Contains(zone))
                zones.Add(zone);
        }

        public void Unregister(StrategicZone zone)
        {
            zones.Remove(zone);
        }

        /// <summary>
        /// 顺序稳定地取前 <see cref="MaxZones"/> 个要地快照（空位由调用方补零）。
        /// </summary>
        public void GetZones(List<StrategicZoneState> output)
        {
            output.Clear();
            int n = Mathf.Min(zones.Count, MaxZones);
            for (int i = 0; i < n; i++)
                output.Add(zones[i].GetState());
        }

        public int Count => zones.Count;

        /// <summary>距 position 最近的未占领/敌方要地（ownerTeam != myTeam）。无匹配时回退最近要地。</summary>
        public StrategicZoneState? NearestZone(Vector3 position, int myTeam)
        {
            StrategicZoneState best = default, fallback = default;
            float bestD = float.MaxValue, fallbackD = float.MaxValue;
            bool found = false, foundAny = false;

            int n = Mathf.Min(zones.Count, MaxZones);
            for (int i = 0; i < n; i++)
            {
                var z = zones[i].GetState();
                float d = (z.position - position).sqrMagnitude;
                if (d < fallbackD) { fallbackD = d; fallback = z; foundAny = true; }
                if (z.ownerTeam != myTeam && d < bestD) { bestD = d; best = z; found = true; }
            }
            if (found) return best;
            if (foundAny) return fallback;
            return null;
        }
    }

    /// <summary>
    /// 一个战略要地。当前实现：<see cref="capturePoint"/> 包装（位置/半径/归属/
    /// 争夺度直读）；未来：指挥官直接创建纯 marker 要地（无争夺机制，contest 恒 0）。
    /// </summary>
    public class StrategicZone : MonoBehaviour
    {
        [Tooltip("被包装的争夺点（可空：空 = 指挥官下发的纯要地）。")]
        public CapturePoint capturePoint;

        [Tooltip("纯要地参数（capturePoint 为空时生效）。")]
        public float radius = 10f;
        public int ownerTeam = -1;

        public StrategicZoneState GetState()
        {
            var s = new StrategicZoneState();
            if (capturePoint != null)
            {
                s.position = capturePoint.transform.position;
                s.radius = capturePoint.radius;
                s.ownerTeam = capturePoint.ownerTeam;
                s.contest = capturePoint.contention / 60f;   // -1..+1
            }
            else
            {
                s.position = transform.position;
                s.radius = radius;
                s.ownerTeam = ownerTeam;
                s.contest = 0f;
            }
            return s;
        }

        private void OnEnable()
        {
            if (StrategicZoneRegistry.Instance != null)
                StrategicZoneRegistry.Instance.Register(this);
        }

        private void OnDisable()
        {
            if (StrategicZoneRegistry.Instance != null)
                StrategicZoneRegistry.Instance.Unregister(this);
        }
    }
}
