using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// Server-only pooled bullet (PHASE4). Not a NetworkBehaviour — the server
    /// simulates it and clients only see the tracer fired via ClientRpc, so bullets
    /// are never spawned/despawned on the network (no per-bullet GC / Mirror churn).
    ///
    /// Flight: 750 m/s. Each frame the server raycasts the segment between the
    /// previous and current position, picks the NEAREST non-owner hit, and:
    ///   - player/AI (NetworkPlayerHealth) -> apply hitbox damage, keep flying
    ///   - anything else (wall / shootable target) -> destroy (return to pool)
    ///   - after <see cref="lifetime"/> (5s) -> return to pool
    /// </summary>
    public class NetworkBullet : MonoBehaviour
    {
        public float speed = 750f;
        public float lifetime = 5f;
        public float damage = 20f;

        private Vector3 direction;
        private float spawnTime;
        private NetworkCombat owner;
        private BulletPool pool;
        private Collider[] ownerColliders;

        private readonly RaycastHit[] hitBuffer = new RaycastHit[16];

        public void Fire(Vector3 origin, Vector3 dir, float spd, float dmg, float life, NetworkCombat owner)
        {
            transform.SetParent(null, true);   // detach to world so it flies independently
            transform.position = origin;

            direction = dir.normalized;
            speed = spd;
            damage = dmg;
            lifetime = life;
            this.owner = owner;

            ownerColliders = owner != null
                ? owner.GetComponentsInChildren<Collider>(true)
                : null;

            spawnTime = Time.time;
        }

        private void Update()
        {
            if (Time.time - spawnTime >= lifetime)
            {
                Expire();
                return;
            }

            Vector3 start = transform.position;
            Vector3 dir = direction;
            float step = speed * Time.deltaTime;
            Vector3 end = start + dir * step;

            float dist = step;
            if (dist > 0.0001f)
            {
                int n = Physics.RaycastNonAlloc(start, dir, hitBuffer, dist,
                                                Physics.DefaultRaycastLayers,
                                                QueryTriggerInteraction.Ignore);

                // RaycastNonAlloc order is not guaranteed — find the nearest valid hit.
                bool found = false;
                RaycastHit best = default;
                float bestDist = dist;

                for (int i = 0; i < n; i++)
                {
                    if (IsOwnerCollider(hitBuffer[i].collider)) continue;
                    if (hitBuffer[i].distance < bestDist)
                    {
                        bestDist = hitBuffer[i].distance;
                        best = hitBuffer[i];
                        found = true;
                    }
                }

                if (found)
                {
                    // Hit feedback: a small smoke puff at the exact hit point, on any
                    // hit (living entity, static target, or cover/wall).
                    owner?.NotifyImpact(best.point);

                    var damageable = best.collider.GetComponentInParent<IDamageable>();
                    if (damageable != null)
                    {
                        bool living = best.collider.GetComponentInParent<NetworkPlayerHealth>() != null;

                        damageable.TakeDamage(damage, best.point);
                        if (living)
                            owner?.NotifyHit();

                        if (!living)
                        {
                            // Hit a non-entity (wall / static target): destroy.
                            Expire();
                            return;
                        }
                        // Living entity: penetrate and keep flying.
                    }
                    else
                    {
                        // Hit geometry with no IDamageable (wall etc.): destroy.
                        Expire();
                        return;
                    }
                }
            }

            transform.position = end;
        }

        private bool IsOwnerCollider(Collider c)
        {
            if (ownerColliders == null) return false;
            for (int i = 0; i < ownerColliders.Length; i++)
                if (ownerColliders[i] == c) return true;
            return false;
        }

        private void Expire()
        {
            pool?.Release(this);
        }

        public void SetPool(BulletPool p)
        {
            pool = p;
        }
    }

    /// <summary>
    /// Fixed-capacity pool (PHASE4: 60 bullets per shooter). Pre-instantiates the
    /// whole capacity up front and recycles expired bullets, so firing never
    /// allocates/destroys objects after warm-up (no high-frequency GC).
    /// </summary>
    public class BulletPool
    {
        private readonly GameObject prefab;
        private readonly Transform parent;
        private readonly System.Collections.Generic.Stack<NetworkBullet> available =
            new System.Collections.Generic.Stack<NetworkBullet>();

        public BulletPool(GameObject prefab, int capacity, Transform parent)
        {
            this.prefab = prefab;
            this.parent = parent;

            capacity = Mathf.Max(1, capacity);
            for (int i = 0; i < capacity; i++)
            {
                var go = Object.Instantiate(prefab, parent);
                go.name = "Bullet";
                var b = go.GetComponent<NetworkBullet>();
                if (b == null) b = go.AddComponent<NetworkBullet>();
                b.SetPool(this);
                go.SetActive(false);
                available.Push(b);
            }
        }

        public NetworkBullet Get()
        {
            if (available.Count == 0) return null;   // pool exhausted (60 in flight)
            var b = available.Pop();
            b.gameObject.SetActive(true);
            return b;
        }

        public void Release(NetworkBullet b)
        {
            if (b == null) return;
            b.transform.SetParent(parent, true);   // re-parent for auto-cleanup with shooter
            b.gameObject.SetActive(false);
            available.Push(b);
        }
    }
}
