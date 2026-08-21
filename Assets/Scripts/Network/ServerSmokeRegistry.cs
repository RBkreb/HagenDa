using System.Collections.Generic;
using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// Server-side smoke registry (PHASE9). Smoke clouds are purely visual on the
    /// client (NetworkSmoke spawned via ClientRpc), so the server has no smoke data
    /// by default. This registry records active smoke on the server so the AI vision
    /// system can treat dense smoke as a line-of-sight blocker.
    ///
    /// A smoke cloud blocks vision only while its current concentration (linearly
    /// decaying over its lifetime) is above <see cref="BlockThreshold"/>.
    /// </summary>
    public static class ServerSmokeRegistry
    {
        /// <summary>Minimum current concentration for a cloud to block AI vision.</summary>
        public const float BlockThreshold = 0.3f;

        private struct SmokeEntry
        {
            public Vector3 position;
            public float radius;
            public float concentration;  // initial concentration
            public float decayTime;
            public float startTime;
        }

        private static readonly List<SmokeEntry> Clouds = new List<SmokeEntry>();

        /// <summary>Record a smoke cloud on the server (called by SmokeThrowable.OnImpact).</summary>
        public static void Register(Vector3 pos, float concentration, float radius, float decayTime)
        {
            Clouds.Add(new SmokeEntry
            {
                position = pos,
                concentration = concentration,
                radius = radius,
                decayTime = Mathf.Max(0.001f, decayTime),
                startTime = Time.time,
            });
        }

        /// <summary>True when the segment from→to passes through a sufficiently dense smoke cloud.</summary>
        public static bool IsSmokeBlocked(Vector3 from, Vector3 to)
        {
            if (Clouds.Count == 0) return false;

            Cleanup();

            for (int i = 0; i < Clouds.Count; i++)
            {
                var s = Clouds[i];
                float t = Mathf.Clamp01((Time.time - s.startTime) / s.decayTime);
                float current = s.concentration * (1f - t);
                if (current <= BlockThreshold) continue;   // thin smoke is penetrable

                if (SegmentIntersectsSphere(from, to, s.position, s.radius))
                    return true;
            }
            return false;
        }

        /// <summary>True when <paramref name="pos"/> is inside a sufficiently dense smoke cloud.</summary>
        public static bool IsInsideSmoke(Vector3 pos, float threshold = BlockThreshold)
        {
            if (Clouds.Count == 0) return false;

            Cleanup();

            for (int i = 0; i < Clouds.Count; i++)
            {
                var s = Clouds[i];
                float t = Mathf.Clamp01((Time.time - s.startTime) / s.decayTime);
                float current = s.concentration * (1f - t);
                if (current <= threshold) continue;

                if ((pos - s.position).sqrMagnitude <= s.radius * s.radius)
                    return true;
            }
            return false;
        }

        /// <summary>Reset all smoke (safe on domain reload / play-mode restart).</summary>
        public static void Clear() => Clouds.Clear();

        private static void Cleanup()
        {
            for (int i = Clouds.Count - 1; i >= 0; i--)
            {
                if (Time.time - Clouds[i].startTime >= Clouds[i].decayTime)
                    Clouds.RemoveAt(i);
            }
        }

        private static bool SegmentIntersectsSphere(Vector3 a, Vector3 b, Vector3 center, float radius)
        {
            Vector3 ab = b - a;
            float lenSq = ab.sqrMagnitude;
            if (lenSq < 0.0001f)
                return Vector3.Distance(a, center) <= radius;

            Vector3 ac = center - a;
            float t = Vector3.Dot(ac, ab) / lenSq;
            t = Mathf.Clamp01(t);

            Vector3 closest = a + ab * t;
            return (closest - center).sqrMagnitude <= radius * radius;
        }
    }
}
