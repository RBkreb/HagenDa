using System.Collections.Generic;
using Mirror;
using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// PHASE9 自由观战相机。WASD 前后左右、空格上升、Shift 下降，
    /// 鼠标拖拽自由视角，无碰撞体。
    /// </summary>
    public class FreeCamera : MonoBehaviour
    {
        public float moveSpeed = 30f;
        public float sprintMultiplier = 2f;
        public float lookSensitivity = 3f;
        public float pitchMin = -89f;
        public float pitchMax = 89f;

        private float yaw, pitch;
        private bool cursorLocked;

        private void Start()
        {
            yaw = transform.eulerAngles.y;
            pitch = transform.eulerAngles.x;

            // ML-branch: 初始机位自适应地图 AABB（Ground 层）。非 HGTR 场景
            // （无 Ground 层）保持旧默认位。
            if (MapLayers.TryGetMapBounds(out var b))
            {
                transform.position = new Vector3(
                    b.center.x,
                    Mathf.Max(b.size.x, b.size.z) * 0.5f,
                    b.center.z - b.size.z * 0.45f);
                yaw = 0f;
                pitch = 40f;
                transform.rotation = Quaternion.Euler(pitch, yaw, 0f);

                var cam = GetComponent<Camera>();
                if (cam != null)
                    cam.farClipPlane = Mathf.Max(cam.farClipPlane, b.size.magnitude * 1.5f);
            }
            else
            {
                transform.position = new Vector3(0f, 60f, -50f);
            }
        }

        private void Update()
        {
            // ML-branch: 人类玩家存在时自由观战相机退位（其自带第一人称相机
            // + AudioListener；两台相机同时活跃会互相覆盖 + 双监听器警告）。
            // 纯 AI 对局（无玩家）时保持观战。
            if (NetworkClient.localPlayer != null)
            {
                gameObject.SetActive(false);
                return;
            }

            // 鼠标左键按住时锁定并旋转
            if (Input.GetMouseButtonDown(0))
            {
                cursorLocked = true;
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
            if (Input.GetMouseButtonUp(0))
            {
                cursorLocked = false;
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }

            if (cursorLocked)
            {
                yaw += Input.GetAxis("Mouse X") * lookSensitivity;
                pitch -= Input.GetAxis("Mouse Y") * lookSensitivity;
                pitch = Mathf.Clamp(pitch, pitchMin, pitchMax);
                transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
            }

            // WASD + Space/Shift
            float spd = moveSpeed * (Input.GetKey(KeyCode.LeftShift) ? sprintMultiplier : 1f);
            Vector3 move = Vector3.zero;

            if (Input.GetKey(KeyCode.W)) move += transform.forward;
            if (Input.GetKey(KeyCode.S)) move -= transform.forward;
            if (Input.GetKey(KeyCode.D)) move += transform.right;
            if (Input.GetKey(KeyCode.A)) move -= transform.right;

            // 空格上升、Shift 下降（Shift 同时按 W 时 sprint 优先，下降用 Ctrl 备选）
            if (Input.GetKey(KeyCode.Space)) move += Vector3.up;
            if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.C)) move -= Vector3.up;

            transform.position += move.normalized * spd * Time.deltaTime;
        }
    }
}
