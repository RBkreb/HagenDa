using Mirror;
using UnityEngine;
using UnityEngine.InputSystem;

namespace HagenDa.Networking
{
    /// <summary>
    /// Server-authoritative, force-driven FPS player controller.
    ///
    /// Authority model (Mirror "Option A"):
    ///  - The owning client ONLY samples input (WASD / mouse / space / fire) and sends it
    ///    up via an unreliable [Command] every FixedUpdate. It never moves its own body.
    ///  - The server applies that input to the Rigidbody (movement forces + gravity +
    ///    jump) and performs the shooting hitscan, then syncs the result down:
    ///       * position + yaw -> NetworkTransformReliable (ServerToClient)
    ///       * pitch          -> SyncVar
    ///  - The owner renders its own camera immediately from raw mouse delta (linear,
    ///    frame-rate independent) and sends its ABSOLUTE view (yaw/pitch) up. The server
    ///    adopts that view exactly (client-authoritative aim), while still owning
    ///    movement / hit-detection / health.
    ///
    /// Locomotion is pure force-driven physics on a capsule Rigidbody (NOT Character
    /// Controller, so the body integrates naturally with the world):
    ///   * gravity               = 1g downward
    ///   * horizontal driving    = a horizontal force (moveAcceleration) in the move direction
    ///   * max horizontal speed  = 7 m/s, clamped every FixedUpdate
    ///   * ground friction       = velocity-proportional drag that stops the body when
    ///                             input is released (gradual deceleration to a stop)
    ///   * jump                  = height-based impulse: v = sqrt(2 * |gravity| * jumpHeight)
    ///
    /// Friction model:
    ///   The drag force is proportional to horizontal velocity (viscous friction), so it
    ///   is smooth ("gradually decelerates") and naturally caps top speed. While the
    ///   player is driving, a *small* drag is used — its magnitude at the speed cap is
    ///   still below the driving force (drag < drive), so the character can always
    ///   accelerate. When input is released, a *stronger* drag brings the body from max
    ///   speed to ~5% (effectively stopped) within ~0.5s.
    ///
    /// Airborne (not grounded): only gravity is applied. Driving force, drag (friction),
    /// speed clamp, and jump are all disabled while airborne.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    [RequireComponent(typeof(CapsuleCollider))]
    public class NetworkPlayerController : NetworkBehaviour
    {
        [Header("Movement")]
        [Tooltip("Horizontal driving acceleration, applied in the move direction. Kept above the drag at the speed cap so the body can always accelerate.")]
        public float moveAcceleration = 12f;

        [Tooltip("Velocity-proportional drag while driving (1/s). Terminal speed = moveAcceleration / friction.\nKept small so drag < drive at the speed cap (character can always accelerate).")]
        public float friction = 1.5f;

        [Tooltip("Velocity-proportional drag when input is released (1/s). Stops the body from max speed to ~5% within ~0.5s.")]
        public float stoppingFriction = 8f;

        [Tooltip("Maximum horizontal speed (m/s). Clamped every FixedUpdate.")]
        public float maxSpeed = 7f;

        [Tooltip("Gravity (1g = 9.81 m/s^2), applied downward while airborne.")]
        public float gravity = 9.81f;

        [Header("Jump")]
        [Tooltip("Jump apex height in meters. Converted to an impulse velocity via sqrt(2 * |gravity| * jumpHeight).")]
        public float jumpHeight = 0.6f;

        [Header("Look")]
        [Tooltip("Degrees of rotation per 1 unit of mouse delta. Linear: no acceleration and no frame-rate dependence.\nCalibrated default: 2500 DPI, 4.56 cm = 260 degrees.")]
        public float lookSensitivity = 0.05793f;
        public float minPitch = -89f;
        public float maxPitch = 89f;

        [Header("Camera")]
        [Tooltip("Distance from the capsule's TOP to the eye/camera, measured along the body's up axis.\nThe eye is derived from the capsule collider geometry (center/height/radius) so it\nautomatically follows crouch (height change) and prone (rotation change).")]
        public float eyeOffsetFromTop = 0.15f;

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
        private CapsuleCollider capsuleCol;
        private bool grounded;
        private NetworkInputState serverInput; // latest input from owner (server-side)

        // Client-side input cache, sampled every rendered frame in Update() so that
        // edge-triggered inputs (e.g. jump) are never missed between FixedUpdate ticks.
        private Vector2 clientMove;
        private bool clientFire;
        private bool jumpRequested; // latched until consumed by SendInput()

        private void Awake()
        {
            rb = GetComponent<Rigidbody>();
            capsuleCol = GetComponent<CapsuleCollider>();
        }

        public override void OnStartServer()
        {
            rb.useGravity = false;   // gravity is applied manually (1g)
            rb.freezeRotation = true;
            rb.isKinematic = false;

            if (isServerOnly && playerCamera != null)
                playerCamera.enabled = false;
        }

        public override void OnStartClient()
        {
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

        // Sample all player input once per rendered frame (Update), so inputs are
        // captured reliably regardless of the FixedUpdate/physics cadence.
        private void SampleInput()
        {
            var k = Keyboard.current;
            var m = Mouse.current;

            clientMove = ReadMove();
            clientFire = m != null && m.leftButton.isPressed;

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

            if (playerCamera != null)
            {
                // Position the camera at the capsule top minus eyeOffsetFromTop, using
                // the body's actual transform (handles crouch height + prone rotation).
                playerCamera.transform.position = GetEyeWorldPosition();

                // World-space rotation: independent of the server-synced root yaw, so
                // the local view never snaps back to the tick-rate-quantized server value.
                playerCamera.transform.rotation = Quaternion.Euler(localPitch, localYaw, 0f);
            }
        }

        // The eye position in LOCAL space: capsule center + up*(height/2 - eyeOffsetFromTop).
        // Read from the live CapsuleCollider so crouch (height/center change) is automatic.
        private Vector3 GetEyeLocalPosition()
        {
            return capsuleCol.center + Vector3.up * (capsuleCol.height * 0.5f - eyeOffsetFromTop);
        }

        // The eye position in WORLD space: the local offset transformed by the body's
        // current transform, so prone (body rotation) moves the eye with the body.
        private Vector3 GetEyeWorldPosition()
        {
            return transform.TransformPoint(GetEyeLocalPosition());
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

            s.fire = clientFire;

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

            if (grounded)
            {
                // Movement direction is derived from the now client-synced yaw, so
                // movement matches the client's view.
                Vector3 dir = transform.forward * serverInput.move.y + transform.right * serverInput.move.x;
                if (dir.sqrMagnitude > 1f) dir = dir.normalized;

                bool hasInput = dir.sqrMagnitude > 0.0001f;

                // Horizontal driving force in the move direction.
                if (hasInput)
                    rb.AddForce(dir * moveAcceleration, ForceMode.Acceleration);

                // Ground friction (velocity-proportional drag). Small while driving (so
                // drag < drive and the body can accelerate); strong when input is released
                // (gradual deceleration to a stop within ~0.5s).
                Vector3 hVel = new Vector3(rb.velocity.x, 0f, rb.velocity.z);
                float drag = hasInput ? friction : stoppingFriction;
                rb.AddForce(-hVel * drag, ForceMode.Acceleration);

                // Limit horizontal speed every FixedUpdate.
                ClampHorizontalSpeed();

                // Jump: height-based impulse (edge-triggered, only when grounded).
                if (serverInput.jump)
                {
                    float jumpVelocity = Mathf.Sqrt(2f * Mathf.Abs(gravity) * jumpHeight);
                    rb.AddForce(Vector3.up * jumpVelocity, ForceMode.Impulse);
                    grounded = false; // lift off immediately so gravity applies
                }
            }
            else
            {
                // Airborne: gravity only. No driving force, drag, speed clamp, or jump.
                rb.AddForce(Vector3.down * gravity, ForceMode.Acceleration);
            }

            // Fire (server-authoritative hitscan).
            if (serverInput.fire && Time.time >= nextFireTime)
            {
                nextFireTime = Time.time + fireRate;
                FireOnServer();
            }
        }

        private void ClampHorizontalSpeed()
        {
            Vector3 v = rb.velocity;
            Vector3 h = new Vector3(v.x, 0f, v.z);

            if (h.magnitude > maxSpeed)
            {
                h = h.normalized * maxSpeed;
                rb.velocity = new Vector3(h.x, v.y, h.z);
            }
        }

        private void FireOnServer()
        {
            // Eye position (capsule top - 0.15m), shared with the client camera.
            Vector3 origin = GetEyeWorldPosition();
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
