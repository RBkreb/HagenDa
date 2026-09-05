using UnityEngine;

namespace HagenDa.Animation
{
    /// <summary>第三人称斜视相机：围绕目标保持固定距离与俯角，平滑跟随。</summary>
    public class ThirdPersonCam : MonoBehaviour
    {
        public Transform target;
        public Vector3 offset = new Vector3(0f, 1.2f, 2.2f); // 右/上/后
        public float distance = 3.2f;
        public float height = 1.4f;
        public float pitch = 18f;      // 俯角（向下为正）
        public float yaw = 0f;
        public float lerp = 12f;

        void LateUpdate()
        {
            if (target == null) return;
            Vector3 center = target.position + Vector3.up * height;
            Quaternion rot = Quaternion.Euler(pitch, yaw, 0f);
            Vector3 dir = rot * Vector3.back;
            Vector3 desired = center + dir * distance;
            transform.position = Vector3.Lerp(transform.position, desired, lerp * Time.deltaTime);
            transform.rotation = Quaternion.LookRotation(center - transform.position);
        }
    }
}
