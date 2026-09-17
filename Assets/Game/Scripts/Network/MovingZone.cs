using System.Collections.Generic;
using Mirror;
using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// 移动区域驱动器(MATCH-LAYER):让据点(CapturePoint)/安全区(GarrisonZone)
    /// 沿航点路径移动。挂在与区域同一 GameObject 上。
    ///
    ///  - 仅服务端移动(NetworkServer.active);客户端经
    ///    NetworkTransformReliable(SyncDirection = ServerToClient)跟随,
    ///    避免双端各自积分产生漂移。
    ///  - 逻辑侧天然跟随:CapturePoint/GarrisonZone 的 Overlap 查询、
    ///    DeployPointSet 子物体部署点、MapPointMarker 视觉、
    ///    StrategicZone.GetState() 都实时读取 transform.position。
    ///  - 编辑器 Scene 视图画出航点路径(gizmo),便于在资产/场景中摆放。
    /// </summary>
    [ExecuteAlways]
    public class MovingZone : MonoBehaviour
    {
        [Tooltip("航点(世界坐标)。少于 2 个时不移动。")]
        public List<Vector3> waypoints = new List<Vector3>();

        [Tooltip("移动速度 (m/s)。")]
        public float moveSpeed = 3f;

        [Tooltip("到达航点后的驻留秒数。")]
        public float dwellSeconds = 0f;

        [Tooltip("true = 走到终点后原路折返;false = 循环(终点瞬回起点)。")]
        public bool pingPong = true;

        [Tooltip("开局等待秒数(部署阶段区域内不动)。")]
        public float startDelay = 5f;

        /// <summary>是否正在移动(服务端运行中)。</summary>
        public bool IsRunning { get; private set; }

        private int wpIndex;          // 当前目标航点
        private int step = 1;         // +1 / -1(ping-pong 方向;必须初始 +1,否则永远停在第 0 航点)
        private float dwellUntil;
        private bool waiting;
        private float startTime = -1f;

        private void Update()
        {
            if (waypoints == null || waypoints.Count < 2) return;
            if (moveSpeed <= 0f) return;

#if UNITY_EDITOR
            if (!Application.isPlaying) return;   // 编辑器只画 gizmo
#endif
            if (!NetworkServer.active) return;    // 客户端由 NetworkTransform 同步

            if (startTime < 0f) startTime = Time.time;
            if (Time.time - startTime < startDelay) return;

            IsRunning = true;
            if (waiting)
            {
                if (Time.time < dwellUntil) return;
                waiting = false;
                AdvanceIndex();
            }

            Vector3 target = waypoints[wpIndex];
            Vector3 delta = target - transform.position;
            float maxStep = moveSpeed * Time.deltaTime;

            if (delta.sqrMagnitude <= maxStep * maxStep)
            {
                transform.position = target;
                if (dwellSeconds > 0f)
                {
                    waiting = true;
                    dwellUntil = Time.time + dwellSeconds;
                }
                else AdvanceIndex();
                return;
            }

            transform.position += delta.normalized * maxStep;
        }

        private void AdvanceIndex()
        {
            if (pingPong)
            {
                wpIndex += step;
                if (wpIndex >= waypoints.Count) { wpIndex = waypoints.Count - 2; step = -1; }
                else if (wpIndex < 0) { wpIndex = 1; step = +1; }
            }
            else
            {
                wpIndex = (wpIndex + 1) % waypoints.Count;
            }
        }

        // ---------------------------------------------------------------
        // GIZMOS(编辑器 + 运行时 Scene 视图路径预览)
        // ---------------------------------------------------------------
        private static readonly Color GizmoPath = new Color(1f, 0.85f, 0.2f, 0.9f);
        private static readonly Color GizmoNode = new Color(1f, 0.6f, 0.1f, 0.9f);

        private void OnDrawGizmos()
        {
            if (waypoints == null || waypoints.Count < 2) return;

            Gizmos.color = GizmoPath;
            for (int i = 0; i < waypoints.Count - 1; i++)
                Gizmos.DrawLine(waypoints[i], waypoints[i + 1]);
            if (!pingPong)
                Gizmos.DrawLine(waypoints[waypoints.Count - 1], waypoints[0]);

            Gizmos.color = GizmoNode;
            foreach (var wp in waypoints)
            {
                Gizmos.DrawWireSphere(wp, 1.5f);
                Gizmos.DrawLine(wp, wp + Vector3.up * 3f);
            }
        }
    }
}
