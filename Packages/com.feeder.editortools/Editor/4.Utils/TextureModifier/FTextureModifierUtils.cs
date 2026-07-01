#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Feeder
{
    /// <summary>
    /// Stateless helpers shared by mesh palette colorizer tools: color comparison,
    /// color conversion, renderer texture/mesh access, and generated asset folders.
    /// </summary>
    public static class FTextureModifierUtils
    {
        /// <summary>Compares two colors without alpha, using a per-channel tolerance.</summary>
        public static bool ColorsClose(Color32 a, Color32 b, int tol)
        {
            return Mathf.Abs(a.r - b.r) <= tol && Mathf.Abs(a.g - b.g) <= tol && Mathf.Abs(a.b - b.b) <= tol;
        }

        /// <summary>Compares two float colors, including alpha.</summary>
        public static bool ApproxEqual(Color a, Color b)
        {
            return Mathf.Abs(a.r - b.r) < 0.0001f && Mathf.Abs(a.g - b.g) < 0.0001f &&
                   Mathf.Abs(a.b - b.b) < 0.0001f && Mathf.Abs(a.a - b.a) < 0.0001f;
        }

        /// <summary>Converts a float Color to Color32 with the provided alpha value.</summary>
        public static Color32 ToColor32(Color c, byte alpha)
        {
            return new Color32(
                (byte)Mathf.Clamp(Mathf.RoundToInt(c.r * 255f), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt(c.g * 255f), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt(c.b * 255f), 0, 255),
                alpha);
        }

        /// <summary>Sanitizes a string for use as an asset file name.</summary>
        public static string MakeSafe(string s)
        {
            if (string.IsNullOrEmpty(s)) return "Mesh";
            foreach (char ch in Path.GetInvalidFileNameChars())
                s = s.Replace(ch, '_');
            return s.Replace(' ', '_');
        }

        /// <summary>Assigns a texture to _BaseMap and _MainTex when the material supports them.</summary>
        public static void SetMapTexture(Material m, Texture2D t)
        {
            if (m == null) return;
            if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", t);
            if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", t);
        }

        /// <summary>Gets the material's primary texture, preferring _BaseMap, then _MainTex, then mainTexture.</summary>
        public static Texture GetMaterialMainTexture(Material m)
        {
            if (m == null) return null;
            if (m.HasProperty("_BaseMap")) { var t = m.GetTexture("_BaseMap"); if (t != null) return t; }
            if (m.HasProperty("_MainTex")) { var t = m.GetTexture("_MainTex"); if (t != null) return t; }
            return m.mainTexture;
        }

        /// <summary>Gets the current mesh used by a SkinnedMeshRenderer or MeshFilter-backed renderer.</summary>
        public static Mesh GetRendererMesh(Renderer r)
        {
            if (r == null) return null;
            if (r is SkinnedMeshRenderer smr) return smr.sharedMesh;
            var mf = r.GetComponent<MeshFilter>();
            return mf != null ? mf.sharedMesh : null;
        }

        /// <summary>Ensures a generated asset folder exists.</summary>
        public static void EnsureGeneratedFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder) || AssetDatabase.IsValidFolder(folder)) return;

            folder = folder.Replace('\\', '/').Trim('/');
            if (!folder.StartsWith("Assets", StringComparison.Ordinal))
                throw new InvalidOperationException("Generated folder must be inside Assets.");

            string[] parts = folder.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }
    }
}
#endif
