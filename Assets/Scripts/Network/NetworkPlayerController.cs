using Mirror;
using UnityEngine;
using UnityEngine.InputSystem;

namespace HagenDa.Networking
{
    /// <summary>
    /// Server-authoritative FPS player controller.
    ///
    /// Authority model (Mirror "Option A"):
    ///  - The owning client ONLY samples input (WASD / mouse / jump / fire) and sends it
    ///    up via an unreliable [Command] every FixedUpdate. It never moves its own body.
    ///  - The server applies that input to the Rigidbody (movement, gravity, jump) and
    ///    to the look state (yaw + pitch), then syncs the result down:
    ///       * position + yaw  -> NetworkTransformReliable (ServerToClient)
    ///       * pitch           -> SyncVar
    ///  - The owner renders its own camera immediately from raw mouse delta (linear,
    ///    frame-rate independent) and sends its ABSOLUTE view (yaw/pitch) up. The
    ///    server adopts that view exactly (client-authoritative aim), while still
    ///    owning movement/hit-detection/health. Remote players render the
    ///    server-synced position/yaw/pitch.
    ///
    /// This mirrors the FPS Engine physics model: Rigidbody + CapsuleCollider,
    /// freezeRotation, manual gravity, velocity-driven locomotion.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    [RequireComponent(typeof(CapsuleCollider))]
    public class NetworkPlayerController : NetworkBehaviour
    {
        [Header("Movement")]
        public float walkSpeed = 5f;
        public float runSpeed = 8f;
        public float crouchSpeed = 3f;
        public float jumpSpeed = 8f;
        public float gravity = 20f;
        public float eyeHeight = 1.6f;

        [Header("Look")]
        [Tooltip("Degrees of rotation per 1 unit of mouse delta. Linear: no acceleration and no frame-rate dependence.\nCalibrated default: 2500 DPI, 4.56 cm = 260 degrees.")]
        public float lookSensitivity = 0.05793f;
        public float minPitch = -89f;
        public float maxPitch = 89f;

        [Header("Combat")]
        public float shootRange = 200f;
        public float shootDamage = 20f;
        public float fireRate = 0.15f;

        [Header("References")]
        public Camera playerCamera;
        public GameObject visual; // remote body (hidden for owner)

        // Server-authoritative look state. Yaw is the root rotation (synced by
        // NetworkTransform), pitch is synced separately via SyncVar.
        [SyncVar] public float pitch;
        private float yaw;
        private float nextFireTime;

        // Client-side look state (client-authoritative). The local camera is driven
        // from raw mouse delta every rendered frame (linear, no tick quantization).
        // The absolute yaw/pitch are sent up so the server adopts the client's view
        // exactly (no delta accumulation drift, no packet-loss drift).
        private float localYaw;
        private float localPitch;

        private Rigidbody rb;
        private bool grounded;
        private NetworkInputState serverInput; // latest input from owner (server-side)

        // Client-side input cache, sampled every rendered frame in Update() so that
        // edge-triggered inputs (e.g. jump) are never missed between FixedUpdate ticks.
        private Vector2 clientMove;
        private bool clientSprint;
        private bool clientCrouch;
        private bool clientFire;
        private bool clientAim;
        private bool jumpRequested; // latched until consumed by SendInput()

        public override void OnStartServer()
        {
            rb = GetComponent<Rigidbody>();
            rb.useGravity = false;
            rb.freezeRotation = true;
            rb.isKinematic = false;

            if (isServerOnly && playerCamera != null)
                playerCamera.enabled = false;
        }

        public override void OnStartClient()
        {
            rb = GetComponent<Rigidbody>();

            // Cap client render rate at 180 Hz. Requires vsync off.
            Application.targetFrameRate = 180;
            QualitySettings.vSyncCount = 0;

            if (!isServer)
            {
                // Only the server simulates physics; clients are kinematic and
                // moved purely by NetworkTransform / SyncVars.
                rb.isKinematic = true;
                rb.useGravity = false;
            }

            if (isLocalPlayer)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
                if (playerCamera != null) playerCamera.enabled = true;
                if (visual != null) visual.SetActive(false);
            }
            else
            {
                if (playerCamera != null) playerCamera.enabled = false;
                if (visual != null) visual.SetActive(true);
            }
        }

        private void Update()
        {
            if (!isLocalPlayer) return;

            SampleInput();
            UpdateLocalLook();
        }

        // Sample all player input once per rendered frame (Update), so edge-triggered
        // inputs are captured reliably regardless of the FixedUpdate/physics cadence.
        private void SampleInput()
        {
            var k = Keyboard.current;
            var m = Mouse.current;

            clientMove = ReadMove();

            clientSprint = k != null && k.leftShiftKey.isPressed;
            clientCrouch = k != null && k.cKey.isPressed;
            clientFire = m != null && m.leftButton.isPressed;
            clientAim = m != null && m.rightButton.isPressed;

            if (k != null && k.spaceKey.wasPressedThisFrame)
                jumpRequested = true;
        }

        // Read mouse delta once per rendered frame and apply it IMMEDIATELY to the
        // local camera (smooth, linear, every frame). localYaw/localPitch hold the
        // client's absolute view and are sent up verbatim in SendInput().
        private void UpdateLocalLook()
        {
            var ms = Mouse.current;
            if (ms == null) return;

            Vector2 d = ms.delta.ReadValue();
            d.y = -d.y;                       // Unity mouse delta.y is inverted relative to camera pitch
            d *= lookSensitivity;             // degrees, linear, frame-rate independent

            localYaw += d.x;
            localPitch = Mathf.Clamp(localPitch + d.y, minPitch, maxPitch);

            // World-space rotation: independent of the server-synced root yaw, so the
            // local view never snaps back to the tick-rate-quantized server value.
            if (playerCamera != null)
                playerCamera.transform.rotation = Quaternion.Euler(localPitch, localYaw, 0f);
        }

        private void FixedUpdate()
        {
            if (isLocalPlayer) SendInput();
            if (isServer) SimulateServer();
        }

        // ---------------------------------------------------------------
        // CLIENT -> SERVER (input uplink)
        // ---------------------------------------------------------------
        private void SendInput()
        {
            NetworkInputState s = default;
            s.move = clientMove;

            // Send the client's absolute view so the server adopts it exactly.
            s.yaw = localYaw;
            s.pitch = localPitch;

            s.jump = jumpRequested;
            jumpRequested = false;

            s.sprint = clientSprint;
            s.crouch = clientCrouch;
            s.fire = clientFire;
            s.aim = clientAim;

            CmdInput(s);
        }

        [Command(channel = Channels.Unreliable)]
        private void CmdInput(NetworkInputState s)
        {
            serverInput = s;
        }

        private Vector2 ReadMove()
        {
            Vector2 m = Vector2.zero;
            var k = Keyboard.current;
            if (k == null) return m;

            if (k.wKey.isPressed) m.y += 1f;
            if (k.sKey.isPressed) m.y -= 1f;
            if (k.dKey.isPressed) m.x += 1f;
            if (k.aKey.isPressed) m.x -= 1f;
            return Vector2.ClampMagnitude(m, 1f);
        }

        // ---------------------------------------------------------------
        // SERVER SIMULATION (authoritative)
        // ---------------------------------------------------------------
        private void SimulateServer()
        {
            // Look: adopt the client's absolute view (client-authoritative aim).
            yaw = serverInput.yaw;
            pitch = Mathf.Clamp(serverInput.pitch, minPitch, maxPitch);
            transform.rotation = Quaternion.Euler(0f, yaw, 0f);

            // Movement (server-authoritative): direction is derived from the now
            // client-synced yaw, so movement matches the client's view.
            float speed = serverInput.sprint ? runSpeed : (serverInput.crouch ? crouchSpeed : walkSpeed);
            Vector3 dir = transform.forward * serverInput.move.y + transform.right * serverInput.move.x;
            if (dir.sqrMagnitude > 1f) dir = dir.normalized;

            Vector3 vel = rb.velocity;
            vel.x = dir.x * speed;
            vel.z = dir.z * speed;

            if (!grounded) vel.y -= gravity * Time.fixedDeltaTime;
            if (serverInput.jump && grounded) vel.y = jumpSpeed;

            rb.velocity = vel;

            // Fire
            if (serverInput.fire && Time.time >= nextFireTime)
            {
                nextFireTime = Time.time + fireRate;
                FireOnServer();
            }
        }

        private void FireOnServer()
        {
            Vector3 origin = transform.position + Vector3.up * eyeHeight;
            Vector3 forward = Quaternion.Euler(pitch, yaw, 0f) * Vector3.forward;

            if (Physics.Raycast(origin, forward, out RaycastHit hit, shootRange))
            {
                var playerHealth = hit.collider.GetComponentInParent<NetworkPlayerHealth>();
                if (playerHealth != null)
                {
                    playerHealth.TakeDamage(shootDamage);
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
        // GROUND DETECTION (server-side)
        // ---------------------------------------------------------------
        private void OnCollisionStay(Collision collision)
        {
            if (!isServer) return;

            foreach (var contact in collision.contacts)
            {
                if (Vector3.Dot(contact.normal, Vector3.up) > 0.5f)
                {
                    grounded = true;
                    return;
                }
            }
        }

        private void OnCollisionExit(Collision collision)
        {
            if (isServer) grounded = false;
        }
    }
}
