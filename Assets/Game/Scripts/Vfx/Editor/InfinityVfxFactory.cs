using System.IO;
using Tuanjie.Infinity;
using UnityEditor;
using UnityEngine;
using UnityEngine.Infinity;
using UnityEngine.Rendering;

namespace HagenDa.Vfx.EditorTools
{
    /// <summary>
    /// One-click factory that builds 5 battle VFX (explosion / smoke / dust / sparks / EMP)
    /// as Infinity particle prefabs: procedural textures + HDRP/Unlit source materials,
    /// then converts each Shuriken ParticleSystem via Infinity's own ShurikenConverter.
    /// Re-running rebuilds everything under Assets/Game/VFX.
    /// </summary>
    public static class InfinityVfxFactory
    {
        private const string RootFolder = "Assets/Game/VFX";
        private const string TexFolder = RootFolder + "/Textures";
        private const string MatFolder = RootFolder + "/Materials";
        private const string PrefabFolder = RootFolder + "/Prefabs";
        private const string ConfigFolder = RootFolder + "/Configs";
        private const string Menu = "HagenDa/VFX/Build Infinity Effects";

        // Cached loaded assets for the current build pass
        private static Texture2D _softCircle;
        private static Texture2D _smokePuff;
        private static Texture2D _ring;
        private static Texture2D _glow;

        [MenuItem(Menu)]
        public static void BuildAll()
        {
            // Full rebuild: a stale "<name>_Infinity.mat" would make ShurikenConverter
            // hit its reuse/overwrite branch; clean regeneration avoids dialogs.
            AssetDatabase.DeleteAsset(RootFolder);
            EnsureFolder(RootFolder);
            EnsureFolder(TexFolder);
            EnsureFolder(MatFolder);
            EnsureFolder(PrefabFolder);
            EnsureFolder(ConfigFolder);

            _softCircle = CreateTexture("SoftCircle", 256, SoftCirclePixel);
            _smokePuff = CreateTexture("SmokePuff", 512, SmokePuffPixel);
            _ring = CreateTexture("Ring", 256, RingPixel);
            _glow = CreateTexture("Glow", 256, GlowPixel);

            BuildSmokePlume();
            BuildDustRise();
            BuildSparksBurst();
            BuildExplosion();
            BuildEmpBlast();

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[InfinityVfxFactory] Done. Output: {PrefabFolder}");
        }

        // ---------------------------------------------------------------- effects

        private static void BuildSmokePlume()
        {
            var go = NewEffectRoot("SmokePlume");
            var ps = go.AddComponent<ParticleSystem>();
            ConfigureBase(ps, looped: true, duration: 5f, maxParticles: 300);

            var em = ps.emission;
            em.rateOverTime = 16f;

            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.5f;

            var main = ps.main;
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.15f, 0.35f);
            main.startSize = new ParticleSystem.MinMaxCurve(1.6f, 2.6f);
            main.startLifetime = new ParticleSystem.MinMaxCurve(3.5f, 5.2f);
            main.gravityModifier = new ParticleSystem.MinMaxCurve(-0.02f);
            main.startRotation = new ParticleSystem.MinMaxCurve(-0.5f, 0.5f);

            var vel = ps.velocityOverLifetime;
            vel.enabled = true;
            vel.y = new ParticleSystem.MinMaxCurve(0.5f, 0.9f);

            SizeGrow(ps, 0.5f, 1.0f, 2.3f);
            FadeGradient(ps, new Color(0.32f, 0.30f, 0.28f), new Color(0.40f, 0.37f, 0.35f),
                new Color(0.47f, 0.44f, 0.42f), peakAlpha: 0.55f, fadeIn: 0.15f);
            Spin(ps, 25f);

            var noise = ps.noise;
            noise.enabled = true;
            noise.strength = 0.6f;
            noise.frequency = 0.35f;
            noise.damping = true;

            ps.GetComponent<ParticleSystemRenderer>().sharedMaterial =
                MakeMaterial("MatSmoke", _smokePuff, Color.white, additive: false);

            SavePrefab(go, "SmokePlume");
        }

        private static void BuildDustRise()
        {
            var go = NewEffectRoot("DustRise");
            var ps = go.AddComponent<ParticleSystem>();
            ConfigureBase(ps, looped: true, duration: 4f, maxParticles: 300);

            var em = ps.emission;
            em.rateOverTime = 26f;

            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.7f;

            var main = ps.main;
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.1f, 0.25f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.18f, 0.42f);
            main.startLifetime = new ParticleSystem.MinMaxCurve(1.2f, 2.2f);
            main.gravityModifier = new ParticleSystem.MinMaxCurve(-0.03f);
            main.startRotation = new ParticleSystem.MinMaxCurve(-1f, 1f);

            var vel = ps.velocityOverLifetime;
            vel.enabled = true;
            vel.y = new ParticleSystem.MinMaxCurve(0.5f, 1.1f);

            SizeGrow(ps, 0.4f, 1.0f, 1.7f);
            FadeGradient(ps, new Color(0.52f, 0.45f, 0.35f), new Color(0.55f, 0.47f, 0.36f),
                new Color(0.58f, 0.50f, 0.40f), peakAlpha: 0.5f, fadeIn: 0.2f);
            Spin(ps, 40f);

            var noise = ps.noise;
            noise.enabled = true;
            noise.strength = 0.25f;
            noise.frequency = 0.5f;
            noise.damping = true;

            ps.GetComponent<ParticleSystemRenderer>().sharedMaterial =
                MakeMaterial("MatDust", _smokePuff, Color.white, additive: false);

            SavePrefab(go, "DustRise");
        }

        private static void BuildSparksBurst()
        {
            var go = NewEffectRoot("SparksBurst");
            var ps = go.AddComponent<ParticleSystem>();
            ConfigureBase(ps, looped: false, duration: 0.1f, maxParticles: 200);

            var em = ps.emission;
            em.SetBursts(new[] { new ParticleSystem.Burst(0f, 50) });

            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 32f;
            shape.radius = 0.05f;

            var main = ps.main;
            main.startSpeed = new ParticleSystem.MinMaxCurve(5f, 10f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.05f, 0.09f);
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.4f, 1.0f);
            main.gravityModifier = new ParticleSystem.MinMaxCurve(0.9f);

            var rr = ps.GetComponent<ParticleSystemRenderer>();
            rr.renderMode = ParticleSystemRenderMode.Stretch;
            rr.lengthScale = 0.5f;
            rr.velocityScale = 0.15f;

            var lim = ps.limitVelocityOverLifetime;
            lim.enabled = true;
            lim.limit = 3f;
            lim.dampen = 0.05f;

            var col = ps.colorOverLifetime;
            col.enabled = true;
            col.color = new ParticleSystem.MinMaxGradient(HotGradient());

            rr.sharedMaterial = MakeMaterial("MatSpark", _softCircle, Color.white, additive: true);

            SavePrefab(go, "SparksBurst");
        }

        private static void BuildExplosion()
        {
            var root = NewEffectRoot("Explosion");

            // Bright flash core
            {
                var ps = AddChildSystem(root, "Flash", out var rr);
                ConfigureBase(ps, looped: false, duration: 0.05f, maxParticles: 4);
                ps.emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 1) });
                var mainFlash = ps.main;
                mainFlash.startLifetime = new ParticleSystem.MinMaxCurve(0.18f);
                SizeGrow(ps, 2.2f, 1.0f, 1.9f);

                var col = ps.colorOverLifetime;
                col.enabled = true;
                col.color = new ParticleSystem.MinMaxGradient(GlowFadeGradient());
                rr.sharedMaterial = MakeMaterial("MatFlash", _glow, Color.white, additive: true);
            }

            // Fireball puffs
            {
                var ps = AddChildSystem(root, "Fireball", out var rr);
                ConfigureBase(ps, looped: false, duration: 0.05f, maxParticles: 64);
                ps.emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 26) });

                var shape = ps.shape;
                shape.shapeType = ParticleSystemShapeType.Sphere;
                shape.radius = 0.3f;

                var main = ps.main;
                main.startSpeed = new ParticleSystem.MinMaxCurve(3.5f, 6.5f);
                main.startSize = new ParticleSystem.MinMaxCurve(0.7f, 1.2f);
                main.startLifetime = new ParticleSystem.MinMaxCurve(0.45f, 0.75f);
                main.startRotation = new ParticleSystem.MinMaxCurve(-1f, 1f);

                var force = ps.forceOverLifetime;
                force.enabled = true;
                force.y = new ParticleSystem.MinMaxCurve(1.2f);

                SizeGrow(ps, 0.6f, 1.0f, 1.9f);
                FadeGradient(ps, new Color(1f, 0.9f, 0.55f), new Color(1f, 0.42f, 0.08f),
                    new Color(0.3f, 0.06f, 0.02f), peakAlpha: 1f, fadeIn: 0f);
                Spin(ps, 60f);

                rr.sharedMaterial = MakeMaterial("MatFire", _softCircle, Color.white, additive: true);
            }

            // Outward spark streaks
            {
                var ps = AddChildSystem(root, "Sparks", out var rr);
                ConfigureBase(ps, looped: false, duration: 0.05f, maxParticles: 64);
                ps.emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 24) });

                var shape = ps.shape;
                shape.shapeType = ParticleSystemShapeType.Sphere;
                shape.radius = 0.15f;

                var main = ps.main;
                main.startSpeed = new ParticleSystem.MinMaxCurve(6f, 11f);
                main.startSize = new ParticleSystem.MinMaxCurve(0.06f, 0.12f);
                main.startLifetime = new ParticleSystem.MinMaxCurve(0.5f, 0.9f);
                main.gravityModifier = new ParticleSystem.MinMaxCurve(0.7f);

                rr.renderMode = ParticleSystemRenderMode.Stretch;
                rr.lengthScale = 0.5f;
                rr.velocityScale = 0.15f;

                var lim = ps.limitVelocityOverLifetime;
                lim.enabled = true;
                lim.limit = 8f;
                lim.dampen = 0.05f;

                var col = ps.colorOverLifetime;
                col.enabled = true;
                col.color = new ParticleSystem.MinMaxGradient(HotGradient());

                rr.sharedMaterial = MakeMaterial("MatSpark", _softCircle, Color.white, additive: true);
            }

            // Ground shockwave ring
            {
                var ps = AddChildSystem(root, "Shockwave", out var rr);
                ps.transform.localEulerAngles = new Vector3(-90f, 0f, 0f);
                ConfigureBase(ps, looped: false, duration: 0.05f, maxParticles: 4);
                ps.emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 1) });
                var mainWave = ps.main;
                mainWave.startLifetime = new ParticleSystem.MinMaxCurve(0.4f);
                SizeGrow(ps, 0.6f, 1.0f, 10.8f);

                var col = ps.colorOverLifetime;
                col.enabled = true;
                col.color = new ParticleSystem.MinMaxGradient(
                    GradientAlpha(new Color(0.9f, 0.95f, 1f), new Color(0.5f, 0.7f, 1f), 1f, 0f, 0.4f));
                rr.sharedMaterial = MakeMaterial("MatRing", _ring, Color.white, additive: true);
            }

            SavePrefab(root, "Explosion");
        }

        private static void BuildEmpBlast()
        {
            var root = NewEffectRoot("EMPBlast");

            // Electric core flash
            {
                var ps = AddChildSystem(root, "Core", out var rr);
                ConfigureBase(ps, looped: false, duration: 0.05f, maxParticles: 4);
                ps.emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 1) });
                var mainCore = ps.main;
                mainCore.startLifetime = new ParticleSystem.MinMaxCurve(0.35f);
                SizeGrow(ps, 1.2f, 1.0f, 3.2f);

                var col = ps.colorOverLifetime;
                col.enabled = true;
                col.color = new ParticleSystem.MinMaxGradient(
                    GradientAlpha(new Color(0.8f, 1.4f, 1.9f), new Color(0.1f, 0.4f, 1.2f), 1f, 0f, 0.55f));
                rr.sharedMaterial = MakeMaterial("MatEMPFlash", _glow, Color.white, additive: true);
            }

            // Lightning arcs
            {
                var ps = AddChildSystem(root, "Arcs", out var rr);
                ConfigureBase(ps, looped: false, duration: 0.05f, maxParticles: 48);
                ps.emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 20) });

                var shape = ps.shape;
                shape.shapeType = ParticleSystemShapeType.Sphere;
                shape.radius = 0.1f;

                var main = ps.main;
                main.startSpeed = new ParticleSystem.MinMaxCurve(8f, 14f);
                main.startSize = new ParticleSystem.MinMaxCurve(0.05f, 0.12f);
                main.startLifetime = new ParticleSystem.MinMaxCurve(0.18f, 0.4f);

                rr.renderMode = ParticleSystemRenderMode.Stretch;
                rr.lengthScale = 0.6f;
                rr.velocityScale = 0.2f;

                var lim = ps.limitVelocityOverLifetime;
                lim.enabled = true;
                lim.limit = 12f;
                lim.dampen = 0.02f;

                var col = ps.colorOverLifetime;
                col.enabled = true;
                col.color = new ParticleSystem.MinMaxGradient(
                    GradientAlpha(new Color(0.55f, 0.95f, 1f), new Color(0.1f, 0.35f, 1f), 1f, 0f, 0.7f));

                rr.sharedMaterial = MakeMaterial("MatEMPArc", _softCircle, Color.white, additive: true);
            }

            // Expanding EMP ring
            {
                var ps = AddChildSystem(root, "Ring", out var rr);
                ps.transform.localEulerAngles = new Vector3(-90f, 0f, 0f);
                ConfigureBase(ps, looped: false, duration: 0.05f, maxParticles: 4);
                ps.emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 1) });
                var mainRing = ps.main;
                mainRing.startLifetime = new ParticleSystem.MinMaxCurve(0.5f);
                SizeGrow(ps, 0.5f, 1.0f, 15f);

                var col = ps.colorOverLifetime;
                col.enabled = true;
                col.color = new ParticleSystem.MinMaxGradient(
                    GradientAlpha(new Color(0.7f, 0.95f, 1f), new Color(0.2f, 0.5f, 1f), 1f, 0f, 0.5f));
                rr.sharedMaterial = MakeMaterial("MatEMPRing", _ring, Color.white, additive: true);
            }

            SavePrefab(root, "EMPBlast");
        }

        // ------------------------------------------------------------ shared setup

        private static GameObject NewEffectRoot(string name)
        {
            return new GameObject(name);
        }

        private static ParticleSystem AddChildSystem(GameObject root, string name, out ParticleSystemRenderer renderer)
        {
            var child = new GameObject(name);
            child.transform.SetParent(root.transform, false);
            var ps = child.AddComponent<ParticleSystem>();
            renderer = ps.GetComponent<ParticleSystemRenderer>();
            return ps;
        }

        private static void ConfigureBase(ParticleSystem ps, bool looped, float duration, int maxParticles)
        {
            var main = ps.main;
            main.loop = looped;
            main.duration = duration;
            main.maxParticles = maxParticles;
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            main.playOnAwake = true;

            var rr = ps.GetComponent<ParticleSystemRenderer>();
            rr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            rr.receiveShadows = false;
            rr.motionVectorGenerationMode = MotionVectorGenerationMode.Camera;
        }

        private static void SizeGrow(ParticleSystem ps, float startSize, float startMul, float endMul)
        {
            var main = ps.main;
            main.startSize = new ParticleSystem.MinMaxCurve(startSize);
            var size = ps.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(startMul, AnimationCurve.EaseInOut(0f, 1f, 1f, endMul));
        }

        private static void FadeGradient(ParticleSystem ps, Color start, Color peak, Color end,
            float peakAlpha, float fadeIn)
        {
            var g = new Gradient();
            var keys = new[]
            {
                new GradientColorKey(start, 0f),
                new GradientColorKey(peak, 0.25f),
                new GradientColorKey(end, 1f),
            };
            var aKeys = new[]
            {
                new GradientAlphaKey(0f, 0f),
                new GradientAlphaKey(peakAlpha, fadeIn),
                new GradientAlphaKey(peakAlpha * 0.8f, 0.7f),
                new GradientAlphaKey(0f, 1f),
            };
            g.SetKeys(keys, aKeys);
            var col = ps.colorOverLifetime;
            col.enabled = true;
            col.color = new ParticleSystem.MinMaxGradient(g);
        }

        private static void Spin(ParticleSystem ps, float degPerSec)
        {
            var rot = ps.rotationOverLifetime;
            rot.enabled = true;
            rot.z = new ParticleSystem.MinMaxCurve(-degPerSec * Mathf.Deg2Rad, degPerSec * Mathf.Deg2Rad);
        }

        private static Gradient HotGradient()
        {
            var g = new Gradient();
            g.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(1f, 0.95f, 0.7f), 0f),
                    new GradientColorKey(new Color(1f, 0.6f, 0.15f), 0.45f),
                    new GradientColorKey(new Color(0.8f, 0.15f, 0.02f), 1f),
                },
                new[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(1f, 0.6f),
                    new GradientAlphaKey(0f, 1f),
                });
            return g;
        }

        private static Gradient GlowFadeGradient()
        {
            var g = new Gradient();
            g.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(2.2f, 2.0f, 1.4f), 0f),
                    new GradientColorKey(new Color(1.2f, 0.9f, 0.4f), 1f),
                },
                new[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(0f, 1f),
                });
            return g;
        }

        private static Gradient GradientAlpha(Color start, Color end, float a0, float a1, float endT)
        {
            var g = new Gradient();
            g.SetKeys(
                new[]
                {
                    new GradientColorKey(start, 0f),
                    new GradientColorKey(end, endT),
                },
                new[]
                {
                    new GradientAlphaKey(a0, 0f),
                    new GradientAlphaKey(a1, 1f),
                });
            return g;
        }

        // ------------------------------------------------------------- materials

        /// <summary>
        /// HDRP/Unlit material, configured exactly like BaseUnlitGUI does for
        /// transparent Alpha/Additive blending so ShurikenConverter's
        /// CopyPropertiesFromMaterial carries everything onto the Infinity shader.
        /// </summary>
        private static Material MakeMaterial(string name, Texture2D tex, Color color, bool additive)
        {
            var mat = new Material(Shader.Find("HDRP/Unlit"))
            {
                name = name,
                hideFlags = HideFlags.None,
            };
            mat.SetTexture("_UnlitColorMap", tex);
            mat.SetColor("_UnlitColor", color);

            mat.SetFloat("_SurfaceType", 1f);
            mat.SetOverrideTag("RenderType", "Transparent");
            mat.SetInt("_ZWrite", 0);
            CoreUtils.SetKeyword(mat, "_SURFACE_TYPE_TRANSPARENT", true);
            mat.SetFloat("_BlendMode", additive ? 2f : 0f);
            mat.SetInt("_SrcBlend", (int)BlendMode.One);
            mat.SetInt("_DstBlend", (int)(additive ? BlendMode.One : BlendMode.OneMinusSrcAlpha));
            mat.SetInt("_AlphaSrcBlend", (int)BlendMode.One);
            mat.SetInt("_AlphaDstBlend", (int)(additive ? BlendMode.One : BlendMode.OneMinusSrcAlpha));
            mat.renderQueue = 2450; // HDRP transparent default queue

            string path = $"{MatFolder}/{name}.mat";
            AssetDatabase.CreateAsset(mat, path);
            return mat;
        }

        // -------------------------------------------------------------- textures

        private static Texture2D CreateTexture(string name, int size, System.Func<int, int, int, Color> pixelFn)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var pixels = new Color[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                pixels[y * size + x] = pixelFn(x, y, size);
            tex.SetPixels(pixels);
            tex.Apply();

            string path = $"{TexFolder}/{name}.png";
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);

            AssetDatabase.ImportAsset(path);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            var settings = new TextureImporterSettings();
            importer.ReadTextureSettings(settings);
            settings.alphaIsTransparency = true; // otherwise white background bleeds through
            settings.mipmapEnabled = true;
            settings.wrapMode = TextureWrapMode.Clamp;
            settings.filterMode = FilterMode.Bilinear;
            settings.readable = false;
            importer.SetTextureSettings(settings);
            importer.SetPlatformTextureSettings(new TextureImporterPlatformSettings
            {
                maxTextureSize = size,
                format = TextureImporterFormat.Automatic,
                overridden = false,
            });
            importer.sRGBTexture = true;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        private static float Radial(int x, int y, int size)
        {
            float fx = (x + 0.5f) / size * 2f - 1f;
            float fy = (y + 0.5f) / size * 2f - 1f;
            return Mathf.Sqrt(fx * fx + fy * fy);
        }

        private static Color SoftCirclePixel(int x, int y, int size)
        {
            float r = Radial(x, y, size);
            float a = 1f - Mathf.SmoothStep(0.35f, 1f, r);
            return new Color(1f, 1f, 1f, a);
        }

        private static Color GlowPixel(int x, int y, int size)
        {
            float r = Radial(x, y, size);
            float a = Mathf.Pow(Mathf.Max(0f, 1f - r), 2.6f);
            return new Color(1f, 1f, 1f, Mathf.Clamp01(a * 1.2f));
        }

        private static Color RingPixel(int x, int y, int size)
        {
            float r = Radial(x, y, size);
            float band = Mathf.Exp(-Mathf.Pow((r - 0.82f) / 0.055f, 2f));
            float edge = 1f - Mathf.SmoothStep(0.96f, 1f, r);
            return new Color(1f, 1f, 1f, band * edge);
        }

        private static Color SmokePuffPixel(int x, int y, int size)
        {
            float r = Radial(x, y, size);
            float mask = 1f - Mathf.SmoothStep(0.25f, 1f, r);
            float n = Fbm(x / (float)size, y / (float)size, 3);
            float a = Mathf.Clamp01(mask * (0.35f + 0.65f * n) * 1.4f);
            a *= 1f - Mathf.SmoothStep(0.9f, 1f, r);
            return new Color(1f, 1f, 1f, a);
        }

        // Tileable value-noise fBm for the smoke puff texture.
        private static float Fbm(float u, float v, int octaves)
        {
            float sum = 0f, amp = 0.5f, freq = 4f, norm = 0f;
            for (int o = 0; o < octaves; o++)
            {
                sum += ValueNoise(u * freq, v * freq) * amp;
                norm += amp;
                amp *= 0.5f;
                freq *= 2f;
            }
            return sum / norm;
        }

        private static float ValueNoise(float u, float v)
        {
            float iu = Mathf.Floor(u), iv = Mathf.Floor(v);
            float fu = u - iu, fv = v - iv;
            fu = fu * fu * (3f - 2f * fu);
            fv = fv * fv * (3f - 2f * fv);
            float a = Hash(iu, iv), b = Hash(iu + 1f, iv);
            float c = Hash(iu, iv + 1f), d = Hash(iu + 1f, iv + 1f);
            return Mathf.Lerp(Mathf.Lerp(a, b, fu), Mathf.Lerp(c, d, fu), fv);
        }

        private static float Hash(float x, float y)
        {
            float n = Mathf.Sin(x * 127.1f + y * 311.7f) * 43758.5453f;
            return n - Mathf.Floor(n);
        }

        // -------------------------------------------------------------- plumbing

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path))
                return;
            string parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
            string leaf = Path.GetFileName(path);
            if (!AssetDatabase.IsValidFolder(parent))
                EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }

        private static void SavePrefab(GameObject root, string name)
        {
            // Convert every Shuriken source system on the hierarchy into an
            // InfinityParticleSystem using Infinity's own editor converter.
            // The converter reads/writes `sharp.assetMainModule`, so each component
            // must first be given a default InfinityAsset (same as the official
            // "Create an InfinityAsset" button / convert-tool flow).
            foreach (var ps in root.GetComponentsInChildren<ParticleSystem>(true))
            {
                var inf = ps.gameObject.AddComponent<InfinityParticleSystem>();
                string configPath = $"{ConfigFolder}/{name}_{ps.gameObject.name}.infinity";
                if (AssetDatabase.LoadAssetAtPath<InfinityAsset>(configPath) != null)
                    AssetDatabase.DeleteAsset(configPath);
                InfinityParticleSystem.CreateDefaultAssetAt(configPath);
                inf.infinityAsset = AssetDatabase.LoadAssetAtPath<InfinityAsset>(configPath);
                ShurikenConverter.Sync(ps, inf, replaceOrg: false, delOrg: true);

                // The converter leaves the source Shuriken system behind (its
                // delOrg only clears force-fields/sub-emitters). Remove it so the
                // prefab is Infinity-only and does not double-render.
                Object.DestroyImmediate(ps.GetComponent<ParticleSystemRenderer>());
                Object.DestroyImmediate(ps);
            }

            string path = $"{PrefabFolder}/{name}.prefab";
            PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
            Debug.Log($"[InfinityVfxFactory] Saved {path}");
        }
    }
}
