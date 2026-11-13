using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using CP99Online.Net;
using HarmonyLib;
using UnityEngine;

namespace CP99Online.Maps
{
    /// <summary>
    /// Builds a map into the game's "0000_Custom" arena using the arena's own CustomArenaSpawner pieces: its 24
    /// tile materials (12 floor colours, 12 darker wall colours), its goal / spawn / boost / bumper / spike prefabs and
    /// its collider prefab (for layer settings and physics material).
    /// </summary>
    internal static class MapBuilder
    {
        private const string MeshName = "UltraPuckMapMesh";
        private const int MaxVerts = 60000;

        /// <summary>The geometry of the map built last (tests).</summary>
        internal static MapGeometry Current { get { return _lastGeo; } }

        private static MapData _lastMap;
        private static MapGeometry _lastGeo;
        private static bool _loggedPrefab;

        private static MapGeometry Geometry(MapData map)
        {
            if (map == _lastMap && _lastGeo != null) return _lastGeo;
            var t = Time.realtimeSinceStartup;
            _lastGeo = MapGeometry.Build(map);
            _lastMap = map;
            NetLog.Info("Maps: built \"" + map.Name + "\" in " + ((Time.realtimeSinceStartup - t) * 1000f).ToString("0") + " ms: "
                + _lastGeo.Faces.Count + " faces, " + _lastGeo.WallOutlines.Count + " wall outlines, " + _lastGeo.FloorOutlines.Count + " floor outlines, "
                + _lastGeo.GroundBoxes.Count + " ground / " + _lastGeo.WallFill.Count + " wall / " + _lastGeo.ForcefieldFill.Count + " forcefield boxes");
            return _lastGeo;
        }

        // ------------------------------------------------------------------ mesh

        internal static void ClearMesh(CustomArenaSpawner sp)
        {
            for (var i = sp.transform.childCount - 1; i >= 0; i--)
            {
                var c = sp.transform.GetChild(i);
                if (c.name == MeshName) UnityEngine.Object.Destroy(c.gameObject);
            }
        }

        internal static void SpawnMesh(CustomArenaSpawner sp, MapData map)
        {
            var geo = Geometry(map);
            ClearMesh(sp);
            sp.meshBuilder.InitializeData();  // sets the 24 material colours
            sp.meshBuilder.CreateFinalMesh(); // and leaves the spawner's own mesh empty
            sp.transform.position = new Vector3(0f, 0f, 0.5f);
            var mats = sp.GetComponent<Renderer>().sharedMaterials;

            var verts = new List<Vector3>(); var uvs = new List<Vector2>();
            var tris = new List<int>[mats.Length];
            for (var i = 0; i < tris.Length; i++) tris[i] = new List<int>();
            var chunks = 0;
            Action flush = () =>
            {
                if (verts.Count == 0) return;
                var go = new GameObject(MeshName) { layer = sp.gameObject.layer };
                go.transform.parent = sp.transform;
                go.transform.localPosition = Vector3.zero;
                go.transform.localRotation = Quaternion.identity;
                go.transform.localScale = Vector3.one;
                var mesh = new Mesh { name = "UltraPuckMap" };
                mesh.vertices = verts.ToArray();
                mesh.uv = uvs.ToArray();
                mesh.subMeshCount = mats.Length;
                for (var i = 0; i < tris.Length; i++) mesh.SetTriangles(tris[i].ToArray(), i);
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterials = mats;
                var src = sp.GetComponent<Renderer>();
                r.shadowCastingMode = src.shadowCastingMode;
                r.receiveShadows = src.receiveShadows;
                verts.Clear(); uvs.Clear();
                foreach (var t in tris) t.Clear();
                chunks++;
            };
            foreach (var f in geo.Faces)
            {
                if (f.Sub >= tris.Length) continue;
                if (verts.Count + 4 > MaxVerts) flush();
                var b = verts.Count;
                verts.Add(V(f.A)); verts.Add(V(f.B)); verts.Add(V(f.C));
                uvs.Add(U(f.UA)); uvs.Add(U(f.UB)); uvs.Add(U(f.UC));
                var t = tris[f.Sub];
                t.Add(b); t.Add(b + 1); t.Add(b + 2);
                if (f.Count == 4)
                {
                    verts.Add(V(f.D)); uvs.Add(U(f.UD));
                    t.Add(b + 2); t.Add(b + 3); t.Add(b);
                }
            }
            flush();
        }

        private static Vector3 V(V3 v) { return new Vector3(v.X, v.Y, v.Z); }
        private static Vector2 U(V2 v) { return new Vector2(v.X, v.Y); }

        // ------------------------------------------------------------------ objects

        internal static void SpawnElements(CustomArenaSpawner sp, MapData map, bool displayOnly, bool showSpawnPoints)
        {
            var gm = Globals.gameManager;
            var arena = displayOnly || gm == null ? null : gm.currentArena;
            Transform holder = null;
            if (arena != null)
            {
                holder = new GameObject("UltraPuckMapObjects").transform;
                holder.parent = arena.transform; // fixed place in the hierarchy, so boost / bumper indices match online
            }
            var hasSpawn = new bool[2];
            foreach (var o in map.Objects)
            {
                var pos = new Vector3(o.X, o.Y, 0.45f);
                var rot = Quaternion.Euler(0f, 0f, o.Angle);
                GameObject go = null;
                var team = o.Team == 2 ? 1 : 0;
                switch (o.Kind)
                {
                    case ObjKind.Bumper: go = Make(displayOnly ? sp.bumperPropPrefab : sp.bumperElementPrefab, pos, rot, holder); break;
                    case ObjKind.Spike: go = Make(displayOnly ? sp.spikePropPrefab : sp.spikeElementPrefab, pos, rot, holder); break;
                    case ObjKind.Boost: go = Make(displayOnly ? sp.boosterPropPrefab : sp.boosterElementPrefab, pos, rot, holder); break;
                    case ObjKind.Goal:
                        if (displayOnly || arena == null) go = Make(team == 0 ? sp.goal1PropPrefab : sp.goal2PropPrefab, pos, rot, null);
                        else
                        {
                            go = Make(sp.goalElementPrefab, pos, rot, (team == 0 ? arena.goals0Parent : arena.goals1Parent).transform);
                            arena.goalNodes[team].transform.position = pos;
                        }
                        break;
                    case ObjKind.Spawn:
                        if (!displayOnly && arena != null)
                        {
                            go = Make(sp.spawnElementPrefab, new Vector3(o.X, o.Y, 0f), Quaternion.identity, arena.spawnPoints[team].transform);
                            hasSpawn[team] = true;
                        }
                        if (showSpawnPoints)
                        {
                            if (go != null) sp.elements.Add(go);
                            go = Make(team == 0 ? sp.spawn1PropPrefab : sp.spawn2PropPrefab, pos, rot, null);
                        }
                        break;
                }
                if (go != null) sp.elements.Add(go);
            }
            if (!displayOnly && arena != null)
                for (var team = 0; team < 2; team++)
                    if (!hasSpawn[team]) sp.elements.Add(Make(sp.spawnElementPrefab, Vector3.zero, Quaternion.identity, arena.spawnPoints[team].transform));
        }

        private static GameObject Make(GameObject prefab, Vector3 pos, Quaternion rot, Transform parent)
        {
            if (prefab == null) return null;
            var go = UnityEngine.Object.Instantiate(prefab, pos, rot) as GameObject;
            if (go != null && parent != null) go.transform.parent = parent;
            return go;
        }

        // ------------------------------------------------------------------ colliders

        internal static void SpawnColliders(CustomArenaSpawner sp, MapData map)
        {
            var geo = Geometry(map);
            LogPrefab(sp.boxColliderPrefab);
            Colliders(sp, "UltraPuckMapGround", "Ground", geo.GroundBoxes, null);
            Colliders(sp, "UltraPuckMapForcefield", "Forcefield", geo.ForcefieldFill, geo.FloorOutlines);
            Colliders(sp, "UltraPuckMapWalls", "Wall", geo.WallFill, geo.WallOutlines);
            PathNodes(geo);
        }

        /// <summary>
        /// The custom arena comes with just two pathfinding nodes, so bots can't find their way around walls. Replace
        /// them with a grid over the floor; GameManager links them right after this (CalculatePathfindingMesh).
        /// </summary>
        private static void PathNodes(MapGeometry geo)
        {
            var gm = Globals.gameManager;
            var pm = gm != null && gm.currentArena != null ? gm.currentArena.pathfindingManager : null;
            if (pm == null) return;
            var pts = new List<V2>();
            var spacing = geo.PathNodes(150, pts);
            if (pts.Count == 0) return;
            for (var i = pm.transform.childCount - 1; i >= 0; i--) UnityEngine.Object.DestroyImmediate(pm.transform.GetChild(i).gameObject);
            foreach (var p in pts)
            {
                var go = new GameObject("Node");
                go.transform.parent = pm.transform;
                go.transform.position = new Vector3(p.X, p.Y, 0f);
                go.AddComponent<PathfindingNode>().localConnections = new List<PathfindingConnection>();
            }
            pm.maxDistance = Mathf.Max(pm.maxDistance, spacing * 1.6f);
            NetLog.Info("Maps: " + pts.Count + " bot path points, " + spacing.ToString("0.0") + " apart");
        }

        /// <summary>One object per layer: the prefab's settings, box pieces plus edge outlines (smooth to bounce off).</summary>
        private static void Colliders(CustomArenaSpawner sp, string name, string layer, List<Box> boxes, List<List<V2>> outlines)
        {
            var go = UnityEngine.Object.Instantiate(sp.boxColliderPrefab, new Vector3(0f, 0f, 0.5f), Quaternion.identity) as GameObject;
            go.name = name;
            go.SetActive(false);
            go.layer = LayerMask.NameToLayer(layer);
            var template = go.GetComponent<BoxCollider2D>();
            var mat = template != null ? template.sharedMaterial : null;
            var trigger = template != null && template.isTrigger;
            if (template != null) UnityEngine.Object.DestroyImmediate(template);
            foreach (var b in boxes)
            {
                var c = go.AddComponent<BoxCollider2D>();
                c.offset = new Vector2((b.X0 + b.X1) / 2f, (b.Y0 + b.Y1) / 2f);
                c.size = new Vector2(b.X1 - b.X0, b.Y1 - b.Y0);
                c.sharedMaterial = mat;
                c.isTrigger = trigger;
            }
            if (outlines != null)
                foreach (var loop in outlines)
                {
                    var e = go.AddComponent<EdgeCollider2D>();
                    var pts = new Vector2[loop.Count + 1];
                    for (var i = 0; i < loop.Count; i++) pts[i] = new Vector2(loop[i].X, loop[i].Y);
                    pts[loop.Count] = pts[0];
                    e.points = pts;
                    e.sharedMaterial = mat;
                    e.isTrigger = trigger;
                }
            go.SetActive(true);
        }

        private static void LogPrefab(GameObject prefab)
        {
            if (_loggedPrefab || prefab == null) return;
            _loggedPrefab = true;
            var comps = string.Join(", ", prefab.GetComponents<Component>().Select(c => c.GetType().Name).ToArray());
            var box = prefab.GetComponent<BoxCollider2D>();
            int player = LayerMask.NameToLayer("Player"), ff = LayerMask.NameToLayer("Forcefield"), wall = LayerMask.NameToLayer("Wall"), ground = LayerMask.NameToLayer("Ground");
            NetLog.Info("Maps: collider prefab [" + comps + "] trigger=" + (box != null && box.isTrigger) + " material=" + (box != null && box.sharedMaterial != null ? box.sharedMaterial.name : "none")
                + "; player hits forcefield=" + !Physics2D.GetIgnoreLayerCollision(player, ff) + " wall=" + !Physics2D.GetIgnoreLayerCollision(player, wall) + " ground=" + !Physics2D.GetIgnoreLayerCollision(player, ground));
        }

        /// <summary>The game's own Arena Creator keeps its tile view (it rebuilds it after every edit).</summary>
        internal static bool Editing(CustomArenaSpawner sp)
        {
            var acm = ArenaCreatorGlobal.arenaCreatorManager;
            return acm != null && acm.arenaSpawner == sp;
        }

        internal static void ClampBackground(CustomArenaSpawner sp, CustomArena arena)
        {
            if (sp.backgroundTextures != null && sp.backgroundTextures.Length > 0)
                arena.gradient = Mathf.Clamp(arena.gradient, 0, sp.backgroundTextures.Length - 1);
        }
    }

    // ---------------------------------------------------------------------- patches

    [HarmonyPatch(typeof(CustomArenaSpawner), "SpawnMesh")]
    internal static class MapSpawnMeshPatch
    {
        private static bool Prefix(CustomArenaSpawner __instance, CustomArena customArena)
        {
            try
            {
                var map = MapBuilder.Editing(__instance) ? null : MapLibrary.MapFor(customArena);
                if (map == null) return true;
                MapBuilder.SpawnMesh(__instance, map);
                return false;
            }
            catch (Exception e) { NetLog.Warn("Maps: building the arena mesh failed: " + e); return true; }
        }
    }

    [HarmonyPatch(typeof(CustomArenaSpawner), "SpawnElements")]
    internal static class MapSpawnElementsPatch
    {
        private static bool Prefix(CustomArenaSpawner __instance, CustomArena customArena, bool displayOnly, bool showSpawnPoints)
        {
            try
            {
                var map = MapBuilder.Editing(__instance) ? null : MapLibrary.MapFor(customArena);
                if (map == null) return true;
                MapBuilder.SpawnElements(__instance, map, displayOnly, showSpawnPoints);
                return false;
            }
            catch (Exception e) { NetLog.Warn("Maps: placing arena objects failed: " + e); return true; }
        }
    }

    [HarmonyPatch(typeof(CustomArenaSpawner), "SpawnColliders")]
    internal static class MapSpawnCollidersPatch
    {
        private static bool Prefix(CustomArenaSpawner __instance, CustomArena customArena)
        {
            try
            {
                var map = MapBuilder.Editing(__instance) ? null : MapLibrary.MapFor(customArena);
                if (map == null) return true;
                MapBuilder.SpawnColliders(__instance, map);
                return false;
            }
            catch (Exception e) { NetLog.Warn("Maps: building arena colliders failed: " + e); return true; }
        }
    }

    [HarmonyPatch(typeof(CustomArenaSpawner), "ClearMesh")]
    internal static class MapClearMeshPatch
    {
        private static void Prefix(CustomArenaSpawner __instance) { MapBuilder.ClearMesh(__instance); }
    }

    [HarmonyPatch(typeof(CustomArenaSpawner), "AssignBackgroundColor")]
    internal static class MapBackgroundPatch
    {
        private static void Prefix(CustomArenaSpawner __instance, CustomArena customArena) { MapBuilder.ClampBackground(__instance, customArena); }
    }

    /// <summary>LevelList.ListLevels: right after it makes its list of custom arenas, add the UltraPuck maps.</summary>
    [HarmonyPatch(typeof(LevelList), "ListLevels")]
    internal static class MapLevelListPatch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = instructions.ToList();
            var ctor = AccessTools.Constructor(typeof(List<CustomArena>), Type.EmptyTypes);
            var add = AccessTools.Method(typeof(MapLibrary), nameof(MapLibrary.AddToLevelList));
            for (var i = 0; i + 1 < codes.Count; i++)
            {
                if (codes[i].opcode != OpCodes.Newobj || !Equals(codes[i].operand, ctor)) continue;
                var load = LoadFor(codes[i + 1]);
                if (load == null) break;
                codes.Insert(i + 2, load);
                codes.Insert(i + 3, new CodeInstruction(OpCodes.Call, add));
                return codes;
            }
            NetLog.Warn("Maps: couldn't hook the arena list; UltraPuck maps won't be listed");
            return codes;
        }

        private static CodeInstruction LoadFor(CodeInstruction store)
        {
            var op = store.opcode;
            if (op == OpCodes.Stloc_0) return new CodeInstruction(OpCodes.Ldloc_0);
            if (op == OpCodes.Stloc_1) return new CodeInstruction(OpCodes.Ldloc_1);
            if (op == OpCodes.Stloc_2) return new CodeInstruction(OpCodes.Ldloc_2);
            if (op == OpCodes.Stloc_3) return new CodeInstruction(OpCodes.Ldloc_3);
            if (op == OpCodes.Stloc_S) return new CodeInstruction(OpCodes.Ldloc_S, store.operand);
            if (op == OpCodes.Stloc) return new CodeInstruction(OpCodes.Ldloc, store.operand);
            return null;
        }
    }
}

namespace CP99Online.Maps
{
    /// <summary>
    /// The game warns that bots don't work on custom arenas (and removes them) because those had no pathfinding
    /// points. UltraPuck builds a grid of them (MapBuilder.PathNodes), so keep the bots.
    /// </summary>
    [HarmonyPatch(typeof(LevelListElement), "Update")]
    internal static class MapKeepBotsPatch
    {
        private static void Postfix(LevelListElement __instance)
        {
            if (!__instance.currentSelectedState || __instance.arenaDummy == null || __instance.arenaDummy.levelName != "Custom") return;
            var mm = MenuGlobals.menuManager;
            if (mm != null && mm.aiNotification != null && mm.aiNotification.activeSelf) mm.aiNotification.SetActive(false);
        }
    }
}
