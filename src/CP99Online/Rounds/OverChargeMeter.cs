using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace CP99Online.Rounds
{
    /// <summary>
    /// Over Charge rings on the charge meter. Past a full charge (3) the game's white ring is a closed circle; on top
    /// of it an orange ring fills the same way (from the bottom up both sides) for the first +100%, then a blue ring
    /// over that for the next +100%. One card = half an orange ring, two = full orange, four = full blue.
    /// Reads BallHandling.ballCharge, which clients get from the host's snapshot, so everyone sees everyone's rings.
    /// </summary>
    [HarmonyPatch(typeof(BallChargeMeter), "Update")]
    internal static class OverChargeMeter
    {
        internal static readonly Color Orange = new Color(1f, 0.55f, 0.1f, 1f);
        internal static readonly Color Blue = new Color(0.25f, 0.6f, 1f, 1f);

        private static readonly Dictionary<BallChargeMeter, LineRenderer[]> _rings = new Dictionary<BallChargeMeter, LineRenderer[]>();
        private static bool _logged;
        private const float RingWidth = 0.2f; // as a fraction of the ring's radius

        private static void Postfix(BallChargeMeter __instance)
        {
            try { Draw(__instance); }
            catch (System.Exception ex) { if (!_logged) { _logged = true; Net.NetLog.Warn("Over Charge meter: " + ex); } }
        }

        private static void Draw(BallChargeMeter m)
        {
            var baseLine = m.lineRenderer;
            var h = m.ballHandlingScript;
            if (baseLine == null || h == null) return;
            var over = h.ballCharge - CardEffects.GameFullCharge; // in charge units: 3 = +100%
            var replaying = Globals.gameManager != null && Globals.gameManager.replayManager != null && Globals.gameManager.replayManager.replaying;
            var show = baseLine.enabled && over > 0.02f && !replaying;

            LineRenderer[] rings;
            if (!_rings.TryGetValue(m, out rings) || rings[0] == null || rings[1] == null)
            {
                if (!show) return; // only build them once someone actually over-charges
                rings = new[] { MakeRing(m, Orange, 1), MakeRing(m, Blue, 2) };
                foreach (var dead in _rings.Keys.Where(k => k == null).ToList()) _rings.Remove(dead);
                _rings[m] = rings;
            }

            var full = CardEffects.GameFullCharge;
            SetArc(rings[0], m, show ? Mathf.Clamp01(over / full) : 0f);
            SetArc(rings[1], m, show ? Mathf.Clamp01((over - full) / full) : 0f);
        }

        /// <summary>Same shape as the game's ring: starts at the bottom and grows up both sides; 1 = a closed circle.</summary>
        private static void SetArc(LineRenderer lr, BallChargeMeter m, float fill)
        {
            if (fill <= 0.001f) { if (lr.enabled) lr.enabled = false; return; }
            if (!lr.enabled) lr.enabled = true;
            var n = Mathf.Max(2, m.numSegments);
            lr.SetVertexCount(n);
            float start = -90f - 180f * fill, end = -90f + 180f * fill;
            for (var i = 0; i < n; i++)
            {
                var a = Mathf.Lerp(start, end, i / (float)(n - 1)) * Mathf.Deg2Rad;
                lr.SetPosition(i, new Vector3(Mathf.Cos(a) * m.circleRadius, Mathf.Sin(a) * m.circleRadius, 0f));
            }
        }

        /// <summary>
        /// A new line beside the game's meter line, recoloured and drawn just in front. (Not a copy of the game's
        /// object: that would bring the game's own meter script along, which draws a full circle.)
        /// </summary>
        private static LineRenderer MakeRing(BallChargeMeter m, Color color, int layer)
        {
            var like = m.lineRenderer;
            var src = like.gameObject;
            var lr = new GameObject().AddComponent<LineRenderer>();
            var w = m.circleRadius * RingWidth; // the game's ring is about this thick (measured from screenshots)
            lr.SetWidth(w, w);
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            var t = lr.transform;
            t.SetParent(src.transform, false);
            t.localPosition = new Vector3(0f, 0f, -0.002f * layer); // in front of the white ring
            t.localRotation = Quaternion.identity;
            t.localScale = Vector3.one;
            lr.gameObject.name = "UltraPuckOverCharge" + layer;
            lr.gameObject.layer = src.layer;
            lr.sortingLayerID = like.sortingLayerID;
            lr.sortingOrder = like.sortingOrder + layer;
            lr.useWorldSpace = false;
            // The game's ring uses Unlit/Transparent, which ignores colour (its green is in the texture), so draw the
            // over charge rings with the sprite shader instead: plain white tinted by the line's vertex colour.
            var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Particles/Alpha Blended") ?? Shader.Find("Unlit/Color");
            if (shader != null)
            {
                var mat = new Material(shader) { name = "UltraPuckOverCharge" };
                if (mat.HasProperty("_Color")) mat.color = shader.name == "Unlit/Color" ? color : Color.white;
                if (mat.HasProperty("_TintColor")) mat.SetColor("_TintColor", new Color(0.5f, 0.5f, 0.5f, 0.5f)); // particle shaders double it
                mat.renderQueue = like.sharedMaterial != null ? like.sharedMaterial.renderQueue + layer : 3000 + layer;
                lr.sharedMaterial = mat;
            }
            lr.SetColors(color, color);
            if (!_logged)
            {
                _logged = true;
                Net.NetLog.Info("Over Charge meter: rings added to '" + src.name + "' (radius " + m.circleRadius.ToString("F2") + ", segments " + m.numSegments + ", shader " + (lr.sharedMaterial != null ? lr.sharedMaterial.shader.name : "none") + ")");
            }
            return lr;
        }
    }
}
