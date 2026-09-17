using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// PHASE8 map point marker: a solid-coloured ground shape with a centre letter,
    /// used for HQ (capture points) and GR (garrisons) on the big map.
    ///
    /// ML-branch: shape is either a circle (diameter) or an axis-aligned rectangle
    /// (extent = XZ half-sizes). The fill + letter live on the <see cref="MapLayers"/>
    /// Zone layer (map cameras only); a 1m outline around the shape lives on the
    /// ZoneOutline layer so the main (world) camera shows zone borders without the
    /// filled discs.
    /// </summary>
    public class MapPointMarker : MonoBehaviour
    {
        public string letter = "A";
        public float diameter = 10f;
        public Color color = Color.white;

        [Tooltip("矩形半宽/半深 (XZ)。x,y > 0 时为矩形，否则为直径圆形。")]
        public Vector2 extent = Vector2.zero;

        private Renderer fillRenderer;
        private Material fillMaterial;
        private readonly System.Collections.Generic.List<Renderer> outlineRenderers
            = new System.Collections.Generic.List<Renderer>();

        public bool IsRect => extent.x > 0f && extent.y > 0f;

        public void Init(string letter, float diameter, Color color, Vector2 rectExtent = default)
        {
            this.letter = letter;
            this.diameter = Mathf.Max(2f, diameter);
            this.color = color;
            this.extent = rectExtent;
            Build();
        }

        private void Build()
        {
            // Hide the old semi-transparent highlight ring / setup-time visual.
            var oldVisual = transform.Find("Visual");
            if (oldVisual != null)
                oldVisual.gameObject.SetActive(false);

            if (IsRect) BuildRect();
            else BuildCircle();

            // Centre letter (world-space TextMesh lying flat, facing the top-down map).
            var labelGo = new GameObject("Label", typeof(TextMesh));
            labelGo.transform.SetParent(transform, false);
            labelGo.transform.localPosition = new Vector3(0f, 0.06f, 0f);
            labelGo.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            SetLayer(labelGo, MapLayers.Zone);

            var label = labelGo.GetComponent<TextMesh>();
            label.text = letter;
            label.anchor = TextAnchor.MiddleCenter;
            label.alignment = TextAlignment.Center;
            label.fontSize = 100;
            label.characterSize = 0.5f;
            label.color = Color.white;
            label.fontStyle = FontStyle.Bold;
        }

        // ---------------------------------------------------------------
        // Shapes (fill on Zone layer, outline on ZoneOutline layer)
        // ---------------------------------------------------------------

        private void BuildCircle()
        {
            float d = diameter;

            var disc = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            disc.name = "MarkerFill";
            disc.transform.SetParent(transform, false);
            disc.transform.localPosition = Vector3.zero;
            disc.transform.localScale = new Vector3(d, 0.02f, d);
            var col = disc.GetComponent<Collider>();
            if (col != null) Destroy(col);
            SetLayer(disc, MapLayers.Zone);
            fillRenderer = disc.GetComponent<Renderer>();
            fillMaterial = NewUnlitMaterial();
            fillRenderer.sharedMaterial = fillMaterial;

            // Ring outline: 32 thin quads around the circumference.
            const int segments = 32;
            for (int i = 0; i < segments; i++)
            {
                float a0 = (i / (float)segments) * Mathf.PI * 2f;
                float a1 = ((i + 1) / (float)segments) * Mathf.PI * 2f;
                float mid = (a0 + a1) * 0.5f;
                float chord = 2f * (d * 0.5f) * Mathf.Sin((a1 - a0) * 0.5f) + 0.9f;   // overlap seam
                var seg = GameObject.CreatePrimitive(PrimitiveType.Cube);
                seg.name = "OutlineSeg";
                seg.transform.SetParent(transform, false);
                seg.transform.localPosition = new Vector3(
                    Mathf.Cos(mid) * d * 0.5f, 0.09f, Mathf.Sin(mid) * d * 0.5f);
                // 立方体长轴(X)必须沿切线：Euler(0,θ,0) 的 X 轴 = (cosθ,0,-sinθ)，
                // 令其 = 切线 (-sin(mid),0,cos(mid)) ⇒ θ = -mid - 90°。
                // （此前 θ=-mid 使长轴指向半径方向 → 放射状线条。）
                seg.transform.localRotation = Quaternion.Euler(
                    0f, -mid * Mathf.Rad2Deg - 90f, 0f);
                seg.transform.localScale = new Vector3(chord, 0.04f, 1f);
                var segCol = seg.GetComponent<Collider>();
                if (segCol != null) Destroy(segCol);
                SetLayer(seg, MapLayers.ZoneOutline);
                var rend = seg.GetComponent<Renderer>();
                rend.sharedMaterial = NewUnlitMaterial();
                outlineRenderers.Add(rend);
            }
        }

        private void BuildRect()
        {
            float w = extent.x * 2f;   // X
            float l = extent.y * 2f;   // Z

            var quad = GameObject.CreatePrimitive(PrimitiveType.Cube);
            quad.name = "MarkerFill";
            quad.transform.SetParent(transform, false);
            quad.transform.localPosition = new Vector3(0f, 0.03f, 0f);
            quad.transform.localScale = new Vector3(w, 0.02f, l);
            var col = quad.GetComponent<Collider>();
            if (col != null) Destroy(col);
            SetLayer(quad, MapLayers.Zone);
            fillRenderer = quad.GetComponent<Renderer>();
            fillMaterial = NewUnlitMaterial();
            fillRenderer.sharedMaterial = fillMaterial;

            // Rect outline: 4 border strips (1m wide, centred on the boundary).
            AddOutlineStrip("Outline_N", new Vector3(0f, 0.09f, l * 0.5f), new Vector3(w + 1f, 0.04f, 1f));
            AddOutlineStrip("Outline_S", new Vector3(0f, 0.09f, -l * 0.5f), new Vector3(w + 1f, 0.04f, 1f));
            AddOutlineStrip("Outline_E", new Vector3(w * 0.5f, 0.09f, 0f), new Vector3(1f, 0.04f, l + 1f));
            AddOutlineStrip("Outline_W", new Vector3(-w * 0.5f, 0.09f, 0f), new Vector3(1f, 0.04f, l + 1f));
        }

        private void AddOutlineStrip(string name, Vector3 localPos, Vector3 scale)
        {
            var strip = GameObject.CreatePrimitive(PrimitiveType.Cube);
            strip.name = name;
            strip.transform.SetParent(transform, false);
            strip.transform.localPosition = localPos;
            strip.transform.localScale = scale;
            var col = strip.GetComponent<Collider>();
            if (col != null) Destroy(col);
            SetLayer(strip, MapLayers.ZoneOutline);
            var rend = strip.GetComponent<Renderer>();
            rend.sharedMaterial = NewUnlitMaterial();
            outlineRenderers.Add(rend);
        }

        // ---------------------------------------------------------------
        // Colour
        // ---------------------------------------------------------------

        public void SetColor(Color c)
        {
            color = c;
            ApplyColor();
        }

        private void ApplyColor()
        {
            if (fillMaterial != null)
                SetUnlitColor(fillMaterial, color);
            foreach (var r in outlineRenderers)
                if (r != null)
                    SetUnlitColor(r.sharedMaterial, color);
        }

        private static void SetUnlitColor(Material m, Color c)
        {
            m.SetColor("_BaseColor", c);
            m.SetColor("_UnlitColor", c);
            m.SetColor("_Color", c);
        }

        private static Material NewUnlitMaterial()
        {
            Shader s = Shader.Find("HDRP/Unlit");
            if (s == null) s = Shader.Find("Sprites/Default");
            if (s == null) s = Shader.Find("Standard");
            return new Material(s);
        }

        private static void SetLayer(GameObject go, int layer)
        {
            if (layer >= 0) go.layer = layer;
        }
    }
}
