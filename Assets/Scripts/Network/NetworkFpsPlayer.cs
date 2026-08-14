using Mirror;
using UnityEngine;
using UnityEngine.InputSystem;
using cowsins;

namespace HagenDa.Networking
{
    /// <summary>
    /// Bridges Cowsins FPS Engine's single-player controller into a networked model:
    ///
    ///  - MOVEMENT: client-authoritative. The owning client runs the FPS Engine's
    ///    native PlayerMovement (run/jump/crouch/slide + camera FOV/tilt sync) and
    ///    NetworkTransform (ClientToServer) relays position to the server, which
    ///    forwards it to other clients.
    ///  - SHOOTING: server-authoritative (self-made). The owner reports a fire intent
    ///    (camera origin + direction) via [Command]; the server performs the hitscan
    ///    and applies damage through NetworkPlayerHealth. FPS Engine weapon assets
    ///    are used only for LOCAL presentation (fire animation / SFX / muzzle flash).
    ///
    /// Non-owner instances (server copies + remote clients) disable the FPS Engine's
    /// local simulation and show a simple capsule proxy instead.
    /// </summary>
    [RequireComponent(typeof(NetworkIdentity))]
    public class NetworkFpsPlayer : NetworkBehaviour
    {
        [Header("Server-authoritative combat")]
        public float shootDamage = 20f;
        public float shootRange = 200f;
        public float fireRate = 0.15f;

        [Header("FPS Engine references")]
        public PlayerMovement playerMovement;
        public WeaponController weaponController;
        public PlayerStats playerStats;
        public InputManager inputManager;

        [Header("Presentation")]
        public GameObject remoteBody; // visible capsule proxy for remote players

        // Subtrees hidden on non-owner instances (first-person-only presentation).
        private static readonly string[] HideOnRemote =
        {
            "CameraPivot",
            "FastActions",
            "PlayerGraphics",
            "GeneralManagers",
            "PlayerUI",
            "InputManager",
            "Camera"
        };

        private float nextFireTime;

        public override void OnStartClient()
        {
            if (isLocalPlayer)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
                if (remoteBody != null) remoteBody.SetActive(false);
            }
            else
            {
                DisableLocalComponents();
                if (remoteBody != null) remoteBody.SetActive(true);
            }
        }

        public override void OnStopClient()
        {
            if (isLocalPlayer)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
        }

        private void Update()
        {
            if (!isLocalPlayer) return;
            HandleFireInput();
        }

        // ---------------------------------------------------------------
        // SHOOTING (client intent -> server authority)
        // ---------------------------------------------------------------
        private void HandleFireInput()
        {
            var mouse = Mouse.current;
            if (mouse == null || !mouse.leftButton.isPressed) return;
            if (Time.time < nextFireTime) return;

            nextFireTime = Time.time + fireRate;

            // Local presentation only (FPS Engine assets).
            PlayLocalFireEffects();

            // Server-authoritative hit detection.
            Camera cam = Camera.main;
            if (cam == null) cam = weaponController != null ? weaponController.MainCamera : null;
            if (cam == null) return;

            CmdFire(cam.transform.position, cam.transform.forward);
        }

        [Command]
        private void CmdFire(Vector3 origin, Vector3 direction)
        {
            if (Physics.Raycast(origin, direction, out RaycastHit hit, shootRange))
            {
                var health = hit.collider.GetComponentInParent<NetworkPlayerHealth>();
                if (health != null)
                {
                    health.TakeDamage(shootDamage);
                    return;
                }

                var target = hit.collider.GetComponentInParent<NetworkShootableTarget>();
                if (target != null)
                {
                    target.TakeDamage(shootDamage);
                }
            }
        }

        // ---------------------------------------------------------------
        // LOCAL PRESENTATION (FPS Engine weapon assets)
        // ---------------------------------------------------------------
        private void PlayLocalFireEffects()
        {
            if (weaponController == null) return;

            var id = weaponController.Id;
            if (id == null) return;

            // Weapon fire animation.
            if (id.Animator != null)
                CowsinsUtilities.ForcePlayAnim("shooting", id.Animator);

            // Fire SFX.
            var sfx = id.GetFireSFX();
            if (sfx != null && SoundManager.Instance != null)
                SoundManager.Instance.PlaySound(sfx, 0, 0, true);

            // Muzzle flash.
            if (id.muzzleVFX != null && PoolManager.Instance != null &&
                id.FirePoint != null && id.FirePoint.Length > 0)
            {
                var firePoint = id.FirePoint[0];
                if (firePoint != null)
                {
                    Camera cam = Camera.main;
                    PoolManager.Instance.GetFromPool(
                        id.muzzleVFX,
                        firePoint.position,
                        cam != null ? cam.transform.rotation : firePoint.rotation);
                }
            }
        }

        // ---------------------------------------------------------------
        // OWNER / REMOTE ISOLATION
        // ---------------------------------------------------------------
        private void DisableLocalComponents()
        {
            // Disable every non-network MonoBehaviour in the hierarchy
            // (FPS Engine local simulation), keep network components alive.
            foreach (var mb in GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null) continue;
                if (mb is NetworkBehaviour) continue;
                if (mb is NetworkIdentity) continue;
                mb.enabled = false;
            }

            // Hide first-person-only presentation subtrees.
            foreach (var name in HideOnRemote)
            {
                var t = transform.Find(name);
                if (t != null) t.gameObject.SetActive(false);
            }

            // Stop physics simulation on non-owner copies.
            var rb = GetComponentInChildren<Rigidbody>(true);
            if (rb != null) rb.isKinematic = true;
        }
    }
}
