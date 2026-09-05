using UnityEngine;

namespace HagenDa.Animation
{
    /// <summary>
    /// 把相机绑定到指定骨骼(通常 head)随动画运动。
    /// position 完全跟随骨骼；rotation 可独立控制（默认只跟随 yaw，避免跑步头摆动导致俯仰翻滚）。
    /// </summary>
    public class HeadCamFollow : MonoBehaviour
    {
        public Transform target;               // 跟随的骨骼(头部)
        public Vector3 offset = new Vector3(0f, -0.03f, 0.08f); // 相对骨骼(眼睛前方略下)
        public bool followYaw = true;          // 水平跟随
        public bool followPitch = false;       // 俯仰跟随（跑步时建议 false 保持稳定）
        public float lerp = 20f;

        void LateUpdate()
        {
            if (target == null) return;
            Vector3 desired = target.TransformPoint(offset);
            transform.position = Vector3.Lerp(transform.position, desired, lerp * Time.deltaTime);

            if (followYaw)
            {
                float yaw = target.eulerAngles.y;
                if (!followPitch)
                {
                    // 仅水平朝向跟随，俯仰保持当前（稳定画面）
                    transform.rotation = Quaternion.Slerp(transform.rotation,
                        Quaternion.Euler(transform.eulerAngles.x, yaw, 0f), lerp * Time.deltaTime);
                }
                else
                {
                    transform.rotation = Quaternion.Slerp(transform.rotation, target.rotation, lerp * Time.deltaTime);
                }
            }
        }
    }
}
