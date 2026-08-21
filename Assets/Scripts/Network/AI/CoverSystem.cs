using UnityEngine;
using UnityEngine.AI;

namespace HagenDa.Networking
{
    /// <summary>
    /// PHASE9 cover finder. Uses <see cref="NavMesh.Raycast"/> to locate the nearest
    /// obstacle edge away from a threat, providing a cover position and an estimated
    /// cover height (used to decide stand / crouch / prone).
    /// </summary>
    public static class CoverSystem
    {
        public struct CoverInfo
        {
            public bool hasCover;
            public Vector3 coverPosition;   // position behind the obstacle edge
            public float height;            // estimated obstacle height (metres)
        }

        /// <summary>
        /// Find cover by casting away from <paramref name="threatPos"/>. Returns a
        /// default (no cover) struct when no obstacle is found within range.
        /// </summary>
        public static CoverInfo FindCover(Vector3 aiPos, Vector3 threatPos, float maxDist = 10f)
        {
            Vector3 away = aiPos - threatPos;
            away.y = 0f;
            if (away.sqrMagnitude < 0.0001f) away = Vector3.forward;
            away.Normalize();

            var info = new CoverInfo { hasCover = false };

            if (NavMesh.Raycast(aiPos, aiPos + away * maxDist, out NavMeshHit hit, NavMesh.AllAreas))
            {
                // hit.position is the obstacle edge on the navmesh; step back a bit.
                Vector3 coverPos = hit.position - away * 0.6f;
                if (NavMesh.SamplePosition(coverPos, out NavMeshHit sample, 2f, NavMesh.AllAreas))
                    coverPos = sample.position;

                info.hasCover = true;
                info.coverPosition = coverPos;
                info.height = EstimateHeight(coverPos);
            }

            return info;
        }

        private static float EstimateHeight(Vector3 pos)
        {
            var origin = pos + Vector3.up * 0.1f;
            if (Physics.Raycast(origin, Vector3.up, out RaycastHit hit, 2.5f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                return Mathf.Max(0.1f, hit.point.y - pos.y);
            return 2.5f;   // no ceiling -> treat as full height (no usable cover)
        }
    }
}
