using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace CP99Online
{
    /// <summary>
    /// Debug aid: with env CP99_DUMP=1, writes the object tree of each loaded scene (components, positions,
    /// TextMesh text, menu links) to BepInEx/plugins/CP99Online/scene-&lt;name&gt;.txt. Does nothing otherwise.
    /// </summary>
    internal class SceneDump : MonoBehaviour
    {
        private static readonly bool Enabled = Environment.GetEnvironmentVariable("CP99_DUMP") == "1";
        private string _pending;
        private float _at;

        internal static string Dir;

        private void OnLevelWasLoaded(int level)
        {
            if (!Enabled) return;
            _pending = Application.loadedLevelName;
            _at = Time.realtimeSinceStartup + 3f;
        }

        private void Update()
        {
            if (!Enabled || _pending == null || Time.realtimeSinceStartup < _at) return;
            var name = _pending;
            _pending = null;
            var sb = new StringBuilder();
            foreach (var go in FindObjectsOfType<GameObject>())
                if (go.transform.parent == null) Dump(go.transform, 0, sb);
            sb.AppendLine().Append("Fonts:");
            foreach (var f in Resources.FindObjectsOfTypeAll<Font>()) sb.Append(' ').Append(f.name).Append(f.dynamic ? "(dynamic)" : "");
            sb.AppendLine();
            File.WriteAllText(Path.Combine(Dir, "scene-" + name + ".txt"), sb.ToString());
            Net.NetLog.Info("Dumped scene " + name);
        }

        private static void Dump(Transform t, int depth, StringBuilder sb)
        {
            sb.Append(' ', depth * 2).Append(t.name)
              .Append(t.gameObject.activeSelf ? "" : " [inactive]")
              .Append(" lp=").Append(t.localPosition.ToString("F2"))
              .Append(" ls=").Append(t.localScale.ToString("F2"));
            foreach (var c in t.GetComponents<Component>())
            {
                if (c == null || c is Transform) continue;
                sb.Append(" {").Append(c.GetType().Name);
                var tm = c as TextMesh;
                if (tm != null) sb.Append(" \"").Append(tm.text.Replace("\n", "\\n")).Append("\" size=").Append(tm.characterSize).Append(" font=").Append(tm.fontSize)
                    .Append(" face=").Append(tm.font != null ? tm.font.name : "-").Append(" style=").Append(tm.fontStyle);
                var mf = c as MeshFilter;
                if (mf != null && mf.sharedMesh != null) sb.Append(" mesh=").Append(mf.sharedMesh.name);
                var mr = c as MeshRenderer;
                if (mr != null && mr.sharedMaterial != null) sb.Append(" mat=").Append(mr.sharedMaterial.name).Append(" shader=").Append(mr.sharedMaterial.shader.name);
                var ml0 = c as MenuList;
                if (ml0 != null) sb.Append(" tint=").Append(ml0.tintColor.ToString("F2"));
                var le = c as ListElement;
                if (le != null) sb.Append(" up=").Append(N(le.connectedUp)).Append(" down=").Append(N(le.connectedDown)).Append(" menu=").Append(N(le.connectedMenu)).Append(" n=").Append(le.listNum);
                var ml = c as MenuList;
                if (ml != null) sb.Append(" prev=").Append(N(ml.previousMenu)).Append(" def=").Append(N(ml.defaultElement)).Append(" noClose=").Append(ml.dontCloseOnA);
                sb.Append('}');
            }
            sb.Append('\n');
            for (var i = 0; i < t.childCount; i++) Dump(t.GetChild(i), depth + 1, sb);
        }

        private static string N(Component c) { return c == null ? "-" : c.name; }
    }
}
