using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// Server-only pooled bullet (PHASE5). Not a NetworkBehaviour — the server
    /// simulates it and clients only see the tracer via ClientRpc, so bullets are
    /// never spawned/despawned on the network (no per-bullet GC / Mirror churn).
    ///
    /// Flight: configured muzzle velocity with a constant deceleration. Each frame
    /// the server raycasts the segment between the previous and current position,
    /// picks the NEAREST non-owner hit, and:
    ///   - player/AI (NetworkPlayerHealth) -> distance-decayed hitbox damage, keep flying
    ///   - anything else (wall / shootable target) -> destroy (return to pool)
    ///   - after <see cref="lifetime"/> -> return to pool
    ///
    /// Damage: baseDamage * (1 - distance * decay) * part, floored at minDamage. The
    /// bullet records its spawn position so each hit computes its own travelled
    /// distance (penetration keeps PHASE4 behaviour: every penetrated enemy takes
    /// its own distance-based damage).
    /// </summary>
    public class NetworkBullet : MonoBehaviour
    {
        public float speed = 800f;
        public float lifetime = 5f;
        public float damage = 30f;

        private Vector3 direction;
        private Vector3 spawnPosition;
        private float spawnTime;
        private WeaponDefinition definition;
        private NetworkGun owner;
        private NetworkCombatant ownerCombatant;   // 射击者（击杀归属 / 友军判定）
        private int ownerTeam = -1;
        private BulletPool pool;
        private Collider[] ownerColliders;

        private readonly RaycastHit[] hitBuffer = new RaycastHit[16];

        public void Fire(Vector3 origin, Vector3 dir, WeaponDefinition def, NetworkGun owner)
        {
            transform.SetParent(null, true);   // detach to world so it flies independently
            transform.position = origin;

            direction = dir.normalized;
            spawnPosition = origin;
            definition = def;
            speed = def != null ? def.bulletSpeed : 800f;
            damage = def != null ? def.baseDamage : 30f;
            lifetime = def != null ? def.bulletLifetime : 5f;
            this.owner = owner;

            ownerCombatant = owner != null ? owner.GetComponent<NetworkCombatant>() : null;
            ownerTeam = ownerCombatant != null ? ownerCombatant.teamId : -1;

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
            if (step <= 0.0001f)
            {
                Expire();
                return;
            }

            Vector3 end = start + dir * step;

            // PHASE12: QueryTriggerInteraction.Collide — the soldier hitboxes are
            // trigger colliders attached to bones; they must be raycast targets.
            int n = Physics.RaycastNonAlloc(start, dir, hitBuffer, step,
                                            Physics.DefaultRaycastLayers,
                                            QueryTriggerInteraction.Collide);

            // RaycastNonAlloc order is not guaranteed — find the nearest VALID hit.
            // Validity rules:
            //   - NetworkHitbox trigger                -> part hitbox (head/body/limb)
            //   - any other trigger                    -> world zone, fly through
            //   - movement capsule of a hitbox entity  -> fly through (PHASE12)
            //   - movement capsule of a legacy entity  -> body hit (old capsule rules)
            //   - anything else                        -> static geometry (body)
            bool found = false;
            RaycastHit best = default;
            float bestDist = step;
            NetworkPlayerHealth bestHealth = null;
            float bestPart = HitboxUtility.BodyMultiplier;

            for (int i = 0; i < n; i++)
            {
                if (IsOwnerCollider(hitBuffer[i].collider)) continue;

                var hitbox = hitBuffer[i].collider.GetComponent<NetworkHitbox>();
                float part;
                NetworkPlayerHealth targetHealth;

                if (hitbox != null)
                {
                    part = NetworkHitbox.GetMultiplier(hitbox.part);
                    targetHealth = hitBuffer[i].collider.GetComponentInParent<NetworkPlayerHealth>();
                }
                else if (hitBuffer[i].collider.isTrigger)
                {
                    continue;   // non-hitbox trigger: bullets fly through
                }
                else
                {
                    targetHealth = hitBuffer[i].collider.GetComponentInParent<NetworkPlayerHealth>();
                    if (targetHealth != null && targetHealth.HasHitboxes)
                        continue;   // movement capsule: the hitboxes consume bullets instead
                    part = HitboxUtility.BodyMultiplier;
                }

                if (hitBuffer[i].distance < bestDist)
                {
                    bestDist = hitBuffer[i].distance;
                    best = hitBuffer[i];
                    bestHealth = targetHealth;
                    bestPart = part;
                    found = true;
                }
            }

            if (found)
            {
                owner?.NotifyImpact(best.point);

                if (bestHealth != null)
                {
                    // Living entity: penetrate and keep flying (PHASE4).
                    var targetCombatant = bestHealth.GetComponent<NetworkCombatant>();
                    bool friendly = ownerTeam >= 0 && targetCombatant != null &&
                                    targetCombatant.teamId == ownerTeam;

                    if (!friendly)
                    {
                        float finalDamage = ComputeDamage(best.point, bestPart);
                        bestHealth.TakeBulletDamage(finalDamage, best.point, direction, ownerCombatant);

                        bool headshot = bestPart >= HitboxUtility.HeadMultiplier;
                        bool killed = bestHealth.health <= 0f;
                        owner?.NotifyHit(headshot, killed);
                    }
                    // 友军：不伤害，子弹继续飞行。
                }
                else
                {
                    var damageable = best.collider.GetComponentInParent<IDamageable>();
                    if (damageable != null)
                    {
                        float finalDamage = ComputeDamage(best.point, bestPart);
                        damageable.TakeDamage(finalDamage);
                    }
                    // Non-entity (wall / static target): destroy.
                    Expire();
                    return;
                }
            }

            // Constant deceleration, then advance to the segment end.
            if (definition != null)
                speed -= definition.bulletDeceleration * Time.deltaTime;
            if (speed <= 0f)
            {
                Expire();
                return;
            }

            transform.position = end;
        }

        private float ComputeDamage(Vector3 hitPoint, float part)
        {
            if (definition == null)
                return damage * part;

            float distance = Vector3.Distance(spawnPosition, hitPoint);
            float decayed = definition.baseDamage * (1f - distance * definition.damageDecay);
            return Mathf.Max(decayed * part, definition.minDamage);
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
