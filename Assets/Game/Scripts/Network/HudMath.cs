using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// Pure HUD math (PHASE8): HQ contention → colour mapping.
    /// </summary>
    public static class HudMath
    {
        /// <summary>
        /// HQ contention value (-60..+60) → display colour. 0 → white, -60 → red,
        /// +60 → blue; linear in between.
        /// </summary>
        public static Color ContentionColor(float contention)
        {
            float t = Mathf.Clamp01(Mathf.Abs(contention) / 60f);
            if (contention <= 0f)
                return Color.Lerp(Color.white, new Color(0.9f, 0.2f, 0.2f), t);
            return Color.Lerp(Color.white, new Color(0.2f, 0.4f, 0.9f), t);
        }
    }
}
