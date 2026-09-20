using System;
using System.IO;
using Den.Tools;              // MatrixAsset
using Den.Tools.Matrices;     // Matrix
using HagenDa.Networking;
using UnityEditor;
using UnityEngine;

namespace HagenDa.Networking.EditorTools
{
    /// <summary>
    /// 把 <see cref="MapGenConfig"/> 合成为一张宏观设计掩码,写成 MapMagic 的
    /// <see cref="MatrixAsset"/> 供图里的 Import 节点读取。
    ///
    /// 坐标:矩阵 <c>[x, z]</c> 中 x 对应世界 X、z 对应世界 Z,栅格覆盖 MapMagic 的
    /// 单块瓦片局部空间 <c>[0, size]</c>(即世界 <c>[-half, +half]</c>)。
    /// 归一化值 0..1 由 MapMagic 乘以 <c>globals.height</c> 得到米。
    ///
    /// 合成顺序:
    ///   1. 谷底基准 + 分形起伏
    ///   2. 边界不可通行山脊
    ///   3. 通路走廊压平
    ///   4. 中央地标台地
    ///   5. 按 symmetry 对以上做对称平均(竞技公平)
    ///   6. 据点/安全区平台压平(在对称之后,保证锚点位置不被镜像污染)
    /// </summary>
    public static class MapMaskComposer
    {
        private const string PreviewFolder = "docs/map/mapmagic";

        /// <summary>
        /// 生成掩码并写入 <paramref name="config"/>.maskPath。返回创建/复用的资产。
        /// </summary>
        public static MatrixAsset Compose(MapGenConfig config, bool writePreviewPng = true)
            => Compose(config, out _, writePreviewPng);

        /// <summary>
        /// 生成掩码。除宏观掩码外,还输出 <paramref name="padMask"/>(据点/安全区平台
        /// 的权重图,0..1)。
        ///
        /// 为什么要单独给平台权重:细节噪声是在**图里**叠加的,而掩码阶段已经先把
        /// 平台压平了 —— 噪声会把平台重新揉皱(实测平台坡度 12.8°,站不稳、放不了
        /// 建筑)。所以图里需要这张权重图,在噪声之后再把平台区域"压回"掩码高度。
        /// </summary>
        public static MatrixAsset Compose(MapGenConfig config, out Matrix padMask,
            bool writePreviewPng = true)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));

            int res = Mathf.Clamp(config.resolution, 33, 2049);
            var matrix = new Matrix(0, 0, res, res);

            float halfX = config.HalfX;
            float halfZ = config.HalfZ;

            // 两个方向都要用,别混:
            //   invX  : 像素 → 世界 (x * invX * sizeX - halfX),分母是 res-1
            //   scaleX: 世界 → 像素 ((wx + halfX) * scaleX),分子是 res-1
            float invX = 1f / Mathf.Max(1, res - 1);
            float invZ = 1f / Mathf.Max(1, res - 1);
            float scaleX = (res - 1) / Mathf.Max(1f, config.sizeX);
            float scaleZ = (res - 1) / Mathf.Max(1f, config.sizeZ);

            // ---- 1..4 宏观场(可对称) ----
            var macro = new float[res * res];
            for (int z = 0; z < res; z++)
            {
                float wz = z * invZ * config.sizeZ - halfZ;
                for (int x = 0; x < res; x++)
                {
                    float wx = x * invX * config.sizeX - halfX;
                    macro[z * res + x] = MacroField(config, wx, wz);
                }
            }

            // ---- 5 对称平均 ----
            if (config.symmetry != MapSymmetry.None)
            {
                var sym = new float[res * res];
                for (int z = 0; z < res; z++)
                {
                    float wz = z * invZ * config.sizeZ - halfZ;
                    for (int x = 0; x < res; x++)
                    {
                        float wx = x * invX * config.sizeX - halfX;
                        SymmetricPair(config.symmetry, wx, wz, out float tx, out float tz);

                        // 世界 → 像素必须是 (res-1)/size,否则所有点都会挤到图像一角,
                        // 镜像平均就会把外圈山脊的高度抹到整张图上。
                        int sx = Mathf.Clamp(Mathf.RoundToInt((tx + halfX) * scaleX), 0, res - 1);
                        int sz = Mathf.Clamp(Mathf.RoundToInt((tz + halfZ) * scaleZ), 0, res - 1);

                        sym[z * res + x] = 0.5f * (macro[z * res + x] + macro[sz * res + sx]);
                    }
                }
                macro = sym;
            }

            // ---- 6 据点/安全区平台 ----
            var padWeights = new float[res * res];
            ApplyZonePads(config, macro, padWeights, res, scaleX, scaleZ, halfX, halfZ);

            padMask = new Matrix(0, 0, res, res);
            for (int z = 0; z < res; z++)
                for (int x = 0; x < res; x++)
                    padMask[x, z] = Mathf.Clamp01(padWeights[z * res + x]);

            // ---- 写回矩阵 ----
            for (int z = 0; z < res; z++)
                for (int x = 0; x < res; x++)
                    matrix[x, z] = Mathf.Clamp01(macro[z * res + x]);

            // ---- 写资产 ----
            MatrixAsset asset = LoadOrCreateMatrixAsset(config.maskPath);
            asset.source = MatrixAsset.Source.New;
            asset.matrix = matrix;
            asset.preview = BuildPreviewTexture(matrix);
            EditorUtility.SetDirty(asset);

            MatrixAsset padAsset = LoadOrCreateMatrixAsset(PadMaskPath(config));
            padAsset.source = MatrixAsset.Source.New;
            padAsset.matrix = padMask;
            padAsset.preview = BuildPreviewTexture(padMask);
            EditorUtility.SetDirty(padAsset);

            AssetDatabase.SaveAssets();

            if (writePreviewPng) WritePreviewPng(config, matrix, res);

            return asset;
        }

        /// <summary>平台权重图资产路径。</summary>
        public static string PadMaskPath(MapGenConfig config) =>
            System.IO.Path.ChangeExtension(config.maskPath, null) + "_pads.asset";

        // ------------------------------------------------------------------
        // 宏观场
        // ------------------------------------------------------------------

        private static float MacroField(MapGenConfig c, float wx, float wz)
        {
            // 1 基准 + 起伏
            float h = c.baseHeight;

            float n = Fbm(wx, wz, c.hillScale, c.hillDetail, c.seed, 5);
            n = SharpCurve(n, c.hillSharpness);
            h += (n - 0.5f) * 2f * c.hillAmplitude;

            // 2 边界山脊
            float ox = Mathf.Max(0f, Mathf.Abs(wx) - c.PlayHalfX);
            float oz = Mathf.Max(0f, Mathf.Abs(wz) - c.PlayHalfZ);
            float outDist = Mathf.Max(ox, oz);
            if (outDist > 0f)
            {
                float t = c.ringTransition <= 0.01f
                    ? 1f
                    : Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(outDist / c.ringTransition));
                float ridgeNoise = Fbm(wx, wz, 60f, 0.5f, c.seed + 977, 3) * 0.10f;
                float ringH = c.ringHeight + (ridgeNoise - 0.05f);
                h = Mathf.Lerp(h, ringH, t);
            }

            // 3 通路走廊
            if (c.lanes != null)
            {
                for (int i = 0; i < c.lanes.Count; i++)
                {
                    MapLaneDef lane = c.lanes[i];
                    if (lane == null || lane.width <= 0f) continue;

                    float d = DistanceToSegment(wx, wz, lane.from, lane.to);
                    float half = lane.width * 0.5f;
                    float w = 1f - Mathf.SmoothStep(0f, 1f,
                        Mathf.Clamp01((d - half) / Mathf.Max(0.01f, lane.transition)));
                    if (w <= 0f) continue;

                    float target = lane.levelNormalized + lane.heightBias;
                    h = Mathf.Lerp(h, target, w * Mathf.Clamp01(lane.flatten));
                }
            }

            // 4 中央地标
            if (c.centerFeature)
            {
                float d = Mathf.Sqrt(wx * wx + wz * wz);
                float w = 1f - Mathf.SmoothStep(0f, 1f,
                    Mathf.Clamp01((d - c.centerRadius) / Mathf.Max(0.01f, c.centerTransition)));
                if (w > 0f)
                {
                    float target = c.baseHeight + c.centerHeight;
                    h = Mathf.Lerp(h, target, w);
                }
            }

            return h;
        }

        private static void SymmetricPair(MapSymmetry s, float wx, float wz, out float tx, out float tz)
        {
            switch (s)
            {
                case MapSymmetry.Rotate180:   tx = -wx; tz = -wz; break;
                case MapSymmetry.MirrorX:     tx = -wx; tz = wz;  break;
                case MapSymmetry.MirrorZ:     tx = wx;  tz = -wz; break;
                case MapSymmetry.MirrorDiagonal: tx = -wz; tz = -wx; break;
                default: tx = wx; tz = wz; break;
            }
        }

        // ------------------------------------------------------------------
        // 据点 / 安全区平台
        // ------------------------------------------------------------------

        private static void ApplyZonePads(MapGenConfig c, float[] field, float[] padWeights, int res,
            float scaleX, float scaleZ, float halfX, float halfZ)
        {
            if (!c.flattenZonePads || c.anchors == null) return;

            float invX = 1f / Mathf.Max(1, res - 1);
            float invZ = 1f / Mathf.Max(1, res - 1);

            for (int a = 0; a < c.anchors.Count; a++)
            {
                MapAnchor anchor = c.anchors[a];
                if (anchor == null) continue;

                float padHeight;
                float padRadius;

                switch (anchor.role)
                {
                    case AnchorRole.CapturePoint:
                        padHeight = c.capturePadHeight;
                        padRadius = Mathf.Max(anchor.radius, c.captureRadius);
                        break;
                    case AnchorRole.GarrisonRed:
                    case AnchorRole.GarrisonBlue:
                        padHeight = c.garrisonPadHeight;
                        padRadius = Mathf.Max(anchor.radius, c.garrisonRadius);
                        break;
                    default:
                        continue;   // 出生点/其它锚点不压平
                }

                float cx = anchor.position.x;
                float cz = anchor.position.z;
                float trans = Mathf.Max(1f, c.padTransition);

                int minX = Mathf.Clamp(Mathf.FloorToInt((cx - padRadius - trans + halfX) * scaleX), 0, res - 1);
                int maxX = Mathf.Clamp(Mathf.CeilToInt((cx + padRadius + trans + halfX) * scaleX), 0, res - 1);
                int minZ = Mathf.Clamp(Mathf.FloorToInt((cz - padRadius - trans + halfZ) * scaleZ), 0, res - 1);
                int maxZ = Mathf.Clamp(Mathf.CeilToInt((cz + padRadius + trans + halfZ) * scaleZ), 0, res - 1);

                for (int z = minZ; z <= maxZ; z++)
                {
                    float wz = z * invZ * c.sizeZ - halfZ;
                    for (int x = minX; x <= maxX; x++)
                    {
                        float wx = x * invX * c.sizeX - halfX;

                        float w;
                        if (c.padShape == MapPadShape.Rectangle)
                        {
                            float dx = Mathf.Max(0f, Mathf.Abs(wx - cx) - c.padExtent.x);
                            float dz = Mathf.Max(0f, Mathf.Abs(wz - cz) - c.padExtent.y);
                            float d = Mathf.Max(dx, dz);
                            w = 1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(d / trans));
                        }
                        else
                        {
                            float d = Mathf.Sqrt((wx - cx) * (wx - cx) + (wz - cz) * (wz - cz));
                            w = 1f - Mathf.SmoothStep(0f, 1f,
                                Mathf.Clamp01((d - padRadius) / trans));
                        }

                        if (w <= 0f) continue;
                        int idx = z * res + x;
                        field[idx] = Mathf.Lerp(field[idx], padHeight, w);
                        // 记录平台权重(多个平台取并集),供图里在噪声之后压回平台
                        padWeights[idx] = Mathf.Max(padWeights[idx], w);
                    }
                }
            }
        }

        // ------------------------------------------------------------------
        // 噪声(确定性,不依赖 UnityEngine.Random / Perlin)
        // ------------------------------------------------------------------

        private static float Fbm(float x, float z, float scale, float detail, int seed, int octaves)
        {
            float s = Mathf.Max(1f, scale);
            float sum = 0f, amp = 1f, norm = 0f, freq = 1f;
            float gain = Mathf.Lerp(0.35f, 0.65f, Mathf.Clamp01(detail));

            for (int o = 0; o < octaves; o++)
            {
                sum += ValueNoise(x / s * freq, z / s * freq, seed + o * 7919) * amp;
                norm += amp;
                amp *= gain;
                freq *= 2f;
            }
            return norm > 0f ? sum / norm : 0.5f;
        }

        private static float ValueNoise(float x, float z, int seed)
        {
            int x0 = Mathf.FloorToInt(x), z0 = Mathf.FloorToInt(z);
            float fx = x - x0, fz = z - z0;
            float ux = fx * fx * (3f - 2f * fx);
            float uz = fz * fz * (3f - 2f * fz);

            float v00 = Hash01(x0, z0, seed);
            float v10 = Hash01(x0 + 1, z0, seed);
            float v01 = Hash01(x0, z0 + 1, seed);
            float v11 = Hash01(x0 + 1, z0 + 1, seed);

            float a = Mathf.Lerp(v00, v10, ux);
            float b = Mathf.Lerp(v01, v11, ux);
            return Mathf.Lerp(a, b, uz);
        }

        private static float Hash01(int x, int z, int seed)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393) + (uint)(z * 668265263) + (uint)seed * 2654435761u;
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return (h & 0xFFFFFFu) / (float)0xFFFFFF;
            }
        }

        /// <summary>把 fbm 的均值拉向极端,>1 出山脊,<1 出圆丘。</summary>
        private static float SharpCurve(float n, float sharpness)
        {
            if (Mathf.Abs(sharpness - 1f) < 0.001f) return n;
            float centered = (n - 0.5f) * 2f;           // -1..1
            float sign = Mathf.Sign(centered);
            float mag = Mathf.Pow(Mathf.Abs(centered), sharpness);
            return Mathf.Clamp01(0.5f + 0.5f * sign * mag);
        }

        private static float DistanceToSegment(float px, float pz, Vector2 a, Vector2 b)
        {
            float vx = b.x - a.x, vz = b.y - a.y;
            float len2 = vx * vx + vz * vz;
            if (len2 < 0.0001f)
                return Mathf.Sqrt((px - a.x) * (px - a.x) + (pz - a.y) * (pz - a.y));

            float t = Mathf.Clamp01(((px - a.x) * vx + (pz - a.y) * vz) / len2);
            float cx = a.x + t * vx;
            float cz = a.y + t * vz;
            return Mathf.Sqrt((px - cx) * (px - cx) + (pz - cz) * (pz - cz));
        }

        // ------------------------------------------------------------------
        // 资产
        // ------------------------------------------------------------------

        private static MatrixAsset LoadOrCreateMatrixAsset(string path)
        {
            var existing = AssetDatabase.LoadAssetAtPath<MatrixAsset>(path);
            if (existing != null) return existing;

            EnsureFolder(Path.GetDirectoryName(path).Replace('\\', '/'));
            var asset = ScriptableObject.CreateInstance<MatrixAsset>();
            AssetDatabase.CreateAsset(asset, path);
            return asset;
        }

        internal static void EnsureFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder) || AssetDatabase.IsValidFolder(folder)) return;

            string parent = Path.GetDirectoryName(folder).Replace('\\', '/');
            string leaf = Path.GetFileName(folder);
            if (!string.IsNullOrEmpty(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }

        /// <summary>
        /// 预览着色:按**高度带**上色而不是画 gamma 斜坡。原来的紫红斜坡完全看不出
        /// 层次,没法据此判断地形对不对。这里用可辨认的分层色阶,便于肉眼验收。
        ///
        /// 色阶按**有效战斗区**的高度范围拉伸:若按全局 0..1 拉伸,外圈山脊(0.87)
        /// 会把战斗区(0.05–0.35)压成一坨纯绿,通路/台地完全看不出来。
        /// </summary>
        private static Texture2D BuildPreviewTexture(Matrix m)
        {
            int w = m.rect.size.x, h = m.rect.size.z;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var px = new Color32[w * h];

            // 取中心 80% 区域(约等于去掉了外圈山脊)的高度范围
            int m0 = Mathf.RoundToInt(w * 0.10f), m1 = Mathf.RoundToInt(w * 0.90f);
            float lo = float.MaxValue, hi = float.MinValue;
            for (int z = m0; z < m1; z++)
                for (int x = m0; x < m1; x++)
                {
                    float v = m[x, z];
                    if (v < lo) lo = v;
                    if (v > hi) hi = v;
                }
            if (hi - lo < 0.01f) { lo = 0f; hi = 1f; }
            float inv = 1f / (hi - lo);

            // 低地(绿) → 台地(黄) → 高台(橙) → 岩(棕) → 山脊(灰白)
            var stops = new[]
            {
                new Color32(58, 92, 48, 255),    // 低地
                new Color32(104, 128, 62, 255),
                new Color32(176, 168, 84, 255),  // 台地
                new Color32(198, 138, 74, 255),  // 高台
                new Color32(150, 112, 88, 255),  // 岩
                new Color32(226, 226, 230, 255), // 山脊
            };

            for (int z = 0; z < h; z++)
                for (int x = 0; x < w; x++)
                {
                    float v = Mathf.Clamp01((m[x, z] - lo) * inv);
                    float f = v * (stops.Length - 1);
                    int i = Mathf.Clamp(Mathf.FloorToInt(f), 0, stops.Length - 2);
                    px[z * w + x] = Color32.Lerp(stops[i], stops[i + 1], f - i);
                }

            tex.SetPixels32(px);
            tex.Apply();
            return tex;
        }

        private static void WritePreviewPng(MapGenConfig c, Matrix m, int res)
        {
            try
            {
                string root = Directory.GetParent(Application.dataPath)?.FullName ?? ".";
                string dir = Path.Combine(root, PreviewFolder);
                Directory.CreateDirectory(dir);

                var tex = BuildPreviewTexture(m);
                byte[] png = tex.EncodeToPNG();
                UnityEngine.Object.DestroyImmediate(tex);

                string file = Path.Combine(dir, $"mask_{c.name}_{res}.png");
                File.WriteAllBytes(file, png);
                Debug.Log($"[MapMask] 掩码预览已写入 {file}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[MapMask] 掩码预览写入失败(不影响生成): {e.Message}");
            }
        }
    }
}
