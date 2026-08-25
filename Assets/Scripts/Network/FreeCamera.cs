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
            transform.position = new Vector3(0f, 60f, -50f);
        }

        private void Update()
        {
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
