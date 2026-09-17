using System.Collections.Generic;
using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// PHASE9 服务器端烟雾注册表。客户端烟雾（NetworkSmoke）只是视觉效果——
    /// 服务器端不存在碰撞体，AI 射线/视线判定无法命中它。本注册表在服务器端
    /// 维护活跃烟雾球体列表，供 AgentRaySensor 的射线解析做线段-球体相交测试。
    /// </summary>
    public static class NetworkSmokeVolume
    {
        public struct Volume
        {
            public Vector3 center;
            public float radius;
            public float startTime;
            public float decayTime;
            public float concentration;
        }

        private static readonly List<Volume> volumes = new List<Volume>();
        public static int Count => volumes.Count;

        /// <summary>注册一个烟雾球体（服务器端，SmokeThrowable.OnImpact 调用）。</summary>
        public static void Register(Vector3 center, float radius, float decayTime, float concentration)
        {
            volumes.Add(new Volume
            {
                center = center,
                radius = radius,
                startTime = Time.time,
                decayTime = decayTime,
                concentration = concentration,
            });
        }

        /// <summary>清理过期烟雾并更新 alpha。每帧由 FSMBattleSystem 调用。</summary>
        public static void Cleanup()
        {
            for (int i = volumes.Count - 1; i >= 0; i--)
            {
                if (Time.time - volumes[i].startTime >= volumes[i].decayTime)
                    volumes.RemoveAt(i);
            }
        }

        /// <summary>当前烟雾 alpha（0..1），线性衰减。</summary>
        public static float CurrentAlpha(in Volume v)
        {
            float t = Mathf.Clamp01((Time.time - v.startTime) / Mathf.Max(0.001f, v.decayTime));
            return v.concentration * (1f - t);
        }

        /// <summary>
        /// 线段-球体相交测试：判断从 origin 沿 dir 长 distance 的射线是否穿过
        /// 任意活跃烟雾球体（且 alpha &gt; 0.4 = PHASE6 "不可看透"阈值）。
        /// </summary>
        public static bool SegmentIntersects(Vector3 origin, Vector3 dir, float distance,
                                              out float hitDist)
        {
            hitDist = distance;
            for (int i = 0; i < volumes.Count; i++)
            {
                var v = volumes[i];
                if (CurrentAlpha(v) <= 0.4f) continue;

                Vector3 oc = origin - v.center;
                float b = Vector3.Dot(oc, dir);
                float c = Vector3.Dot(oc, oc) - v.radius * v.radius;
                float disc = b * b - c;
                if (disc < 0f) continue;

                float sqrtDisc = Mathf.Sqrt(disc);
                float t0 = -b - sqrtDisc;
                float t1 = -b + sqrtDisc;
                float t = t0 > 0f ? t0 : t1;
                if (t > 0f && t < distance && t < hitDist)
                    hitDist = t;
            }
            return hitDist < distance;
        }

        public static void Clear() => volumes.Clear();
    }
}
