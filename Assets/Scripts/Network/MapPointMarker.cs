using UnityEngine;

namespace HagenDa.Networking
{
    /// <summary>
    /// PHASE8 map point marker: a solid-coloured disc with a centre letter, used for
    /// HQ (capture points) and GR (garrisons) on the big map. Replaces the old
    /// semi-transparent highlight ring.
    ///
    /// Both the disc and the letter live on the <see cref="MapLayers"/> Highlight
    /// layer, so they appear on the big map and the main (world) camera, but NOT on
    /// the minimap (which renders only the entity indicator layer).
    /// </summary>
    public class MapPointMarker : MonoBehaviour
    {
        public string letter = "A";
        public float diameter = 10f;
        public Color color = Color.white;

        private Renderer discRenderer;
        private Material discMaterial;

        public void Init(string letter, float diameter, Color color)
        {
            this.letter = letter;
            this.diameter = Mathf.Max(2f, diameter);
            this.color = color;
            Build();
        }

        private void Build()
        {
            // Hide the old semi-transparent highlight ring, replaced by this marker.
            var oldVisual = transform.Find("Visual");
            if (oldVisual != null)
                oldVisual.gameObject.SetActive(false);

            // Solid disc.
            var disc = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            disc.name = "MarkerDisc";
            disc.transform.SetParent(transform, false);
            disc.transform.localPosition = Vector3.zero;
            disc.transform.localScale = new Vector3(this.diameter, 0.02f, this.diameter);
            if (MapLayers.Highlight >= 0) disc.layer = MapLayers.Highlight;

            var col = disc.GetComponent<Collider>();
            if (col != null) Destroy(col);

            discRenderer = disc.GetComponent<Renderer>();
            discMaterial = new Material(FindUnlitShader());
            discRenderer.sharedMaterial = discMaterial;
            ApplyColor();

            // Centre letter (world-space TextMesh lying flat, facing the top-down map).
            var labelGo = new GameObject("Label", typeof(TextMesh));
            labelGo.transform.SetParent(transform, false);
            labelGo.transform.localPosition = new Vector3(0f, 0.06f, 0f);
            labelGo.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            if (MapLayers.Highlight >= 0) labelGo.layer = MapLayers.Highlight;

            var label = labelGo.GetComponent<TextMesh>();
            label.text = letter;
            label.anchor = TextAnchor.MiddleCenter;
            label.alignment = TextAlignment.Center;
            label.fontSize = 100;
            label.characterSize = 0.5f;
            label.color = Color.white;
            label.fontStyle = FontStyle.Bold;
        }

        public void SetColor(Color c)
        {
            color = c;
            ApplyColor();
        }

        private void ApplyColor()
        {
            if (discMaterial == null) return;
            discMaterial.SetColor("_BaseColor", color);
            discMaterial.SetColor("_UnlitColor", color);
            discMaterial.SetColor("_Color", color);
        }

        private static Shader FindUnlitShader()
        {
            Shader s = Shader.Find("HDRP/Unlit");
            if (s == null) s = Shader.Find("Sprites/Default");
            if (s == null) s = Shader.Find("Standard");
            return s;
        }
    }
}
