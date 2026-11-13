using System.Collections.Generic;
using System.Linq;
using CP99Online.Net;
using UnityEngine;

namespace CP99Online.Rounds
{
    /// <summary>Things cards put into the arena: Pylon Goalie dummies and Puck Gun shots.</summary>
    internal static class CardObjects
    {
        internal static void Clear()
        {
            foreach (var p in _pylons) if (p != null && p.Root != null) Object.Destroy(p.Root);
            _pylons.Clear();
            _pylonSig = null;
            Stun.Clear();
            Invisibility.Clear();
            foreach (var s in _shots.Values) if (s != null) Object.Destroy(s.gameObject);
            _shots.Clear();
        }

        // ------------------------------------------------------------------ Pylon Goalie
        // A dummy player in front of the net. On the host (or offline) it's a heavy physics body that players and
        // the puck can bump; it goes back to its spot at every face-off. Online clients get its position from the host.

        internal sealed class Pylon
        {
            public GameObject Root;
            public Rigidbody2D Body;
            public Vector3 Home;
            public Vector2 Target; // clients: latest position from the host
        }

        private const string PylonName = "UltraPuck_Pylon";
        private const float PylonWeight = 3f;   // x a normal player's mass
        private const float PylonDrag = 3.5f;   // slows down quickly after a bump

        private static readonly List<Pylon> _pylons = new List<Pylon>();
        private static string _pylonSig;

        internal static IList<Pylon> Pylons { get { return _pylons; } }
        internal static bool IsPylon(Collider2D c) { return c != null && c.gameObject.name == PylonName; }

        /// <summary>Make sure each team has the given number of pylons in front of its net (every PC builds its own).</summary>
        internal static void SetPylons(Dictionary<int, int> perTeam)
        {
            var sig = string.Join(",", perTeam.Where(kv => kv.Value > 0).OrderBy(kv => kv.Key).Select(kv => kv.Key + ":" + kv.Value).ToArray());
            if (sig == _pylonSig && _pylons.All(p => p.Root != null)) return;
            _pylonSig = sig;
            foreach (var p in _pylons) if (p.Root != null) Object.Destroy(p.Root);
            _pylons.Clear();

            var gm = Globals.gameManager;
            if (gm == null) return;
            foreach (var kv in perTeam.OrderBy(kv => kv.Key)) // same order on every PC (positions are sent by index)
            {
                if (kv.Value <= 0 || kv.Key < 0 || kv.Key >= gm.teams.Count) continue;
                var team = gm.teams[kv.Key];
                Vector3 spot, outward;
                if (!GoalieSpot(team, out spot, out outward)) continue;
                var side = new Vector3(-outward.y, outward.x, 0f);
                var n = Mathf.Min(kv.Value, 4);
                for (var i = 0; i < n; i++)
                {
                    var p = MakePylon(team, spot + side * ((i - (n - 1) / 2f) * 1.1f));
                    if (p != null) _pylons.Add(p);
                }
                NetLog.Info("Rounds: " + n + " pylon(s) in front of team " + (kv.Key + 1) + "'s net at " + spot);
            }
        }

        /// <summary>Host / offline, after a face-off reset: pylons go back to their spots.</summary>
        internal static void ResetPylons()
        {
            foreach (var p in _pylons)
            {
                if (p.Root == null) continue;
                p.Root.transform.position = p.Home;
                if (p.Body != null) { p.Body.position = p.Home; p.Body.velocity = Vector2.zero; p.Body.angularVelocity = 0f; }
            }
        }

        /// <summary>Client: pylon positions from the host.</summary>
        internal static void ClientPylonPositions(IList<Vector2> positions)
        {
            for (var i = 0; i < _pylons.Count && i < positions.Count; i++) _pylons[i].Target = positions[i];
        }

        /// <summary>The game's goalie position in front of a team's net, and the direction out of the net.</summary>
        private static bool GoalieSpot(TeamData team, out Vector3 spot, out Vector3 outward)
        {
            spot = outward = Vector3.zero;
            var node = team.goalieNodes;
            var goal = team.goalNode;
            if (node == null && goal == null) return false;
            if (node != null)
            {
                spot = node.transform.position;
                if (node.transform.childCount > 0)
                {
                    var sum = Vector3.zero;
                    foreach (Transform c in node.transform) sum += c.position;
                    spot = sum / node.transform.childCount;
                }
            }
            var goalPos = goal != null ? goal.transform.position : spot;
            var other = team.otherGoalNode != null ? team.otherGoalNode.transform.position : Vector3.zero;
            outward = other - goalPos;
            outward.z = 0f;
            outward = outward.sqrMagnitude > 0.01f ? outward.normalized : Vector3.up;
            if (node == null) spot = goalPos + outward * 2f;
            return true;
        }

        private static Pylon MakePylon(TeamData team, Vector3 at)
        {
            var template = team.members.FirstOrDefault(m => m != null && m.ballMesh != null);
            if (template == null) return null;
            var root = new GameObject(PylonName);
            root.layer = template.gameObject.layer;
            var home = new Vector3(at.x, at.y, template.transform.position.z);
            root.transform.position = home;

            // A normal (card-free) player's size, even if the team has Heavyweight.
            var baseScale = CardEffects.BaseScale(template);
            var col = root.AddComponent<CircleCollider2D>();
            var tc = template.GetComponent<CircleCollider2D>();
            col.radius = tc != null ? tc.radius * Mathf.Max(baseScale.x, baseScale.y) : 0.4f;

            var rb = root.AddComponent<Rigidbody2D>();
            var trb = template.GetComponent<Rigidbody2D>();
            if (CardGame.IsAuthority)
            {
                // Heavy, slides a little when bumped, then stops.
                rb.gravityScale = trb != null ? trb.gravityScale : 0f;
                rb.mass = (trb != null ? trb.mass : 0.5f) * PylonWeight;
                rb.drag = PylonDrag;
                rb.angularDrag = 5f;
                rb.fixedAngle = true;
            }
            else rb.isKinematic = true; // clients just follow the host's position

            var mesh = (GameObject)Object.Instantiate(template.ballMesh);
            mesh.SetActive(true);
            foreach (var mb in mesh.GetComponentsInChildren<MonoBehaviour>(true)) Object.DestroyImmediate(mb);
            foreach (var c in mesh.GetComponentsInChildren<Collider2D>(true)) Object.DestroyImmediate(c);
            foreach (var c in mesh.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(c);
            foreach (var ps in mesh.GetComponentsInChildren<ParticleSystem>(true)) Object.DestroyImmediate(ps.gameObject);
            mesh.transform.SetParent(root.transform, false);
            mesh.transform.localPosition = template.ballMesh.transform.localPosition;
            mesh.transform.localRotation = template.ballMesh.transform.localRotation;
            mesh.transform.localScale = Vector3.Scale(template.ballMesh.transform.localScale, baseScale);
            // A bit darker so it reads as a dummy, not a player.
            foreach (var r in mesh.GetComponentsInChildren<Renderer>(true))
                foreach (var m in r.materials)
                    if (m.HasProperty("_Color")) m.color = Color.Lerp(m.color, Color.gray, 0.45f);

            var pylon = new Pylon { Root = root, Body = rb, Home = home, Target = home };
            if (!CardGame.IsAuthority) root.AddComponent<PylonFollow>().Pylon = pylon;
            return pylon;
        }

        /// <summary>Client: glide the pylon to the host's latest position.</summary>
        internal sealed class PylonFollow : MonoBehaviour
        {
            public Pylon Pylon;

            private void Update()
            {
                var p = transform.position;
                var t = Vector2.Lerp(p, Pylon.Target, Mathf.Clamp01(Time.deltaTime * 15f));
                transform.position = new Vector3(t.x, t.y, p.z);
            }
        }

        // ------------------------------------------------------------------ Puck Gun

        // Fired on every dash (so the dash cooldown limits it). A hit stuns the opponent (see Stun).
        private const float ShotSpeed = 26f, ShotRadius = 0.25f, ShotLife = 4f;
        // Extra reach against players only (not walls), so tiny differences between the host's and a client's view of
        // a player don't turn a hit on screen into a miss.
        private const float PlayerLeeway = 0.1f;
        /// <summary>Speed the puck gets when a shot hits it (the game caps the puck at 90).</summary>
        private const float PuckKnock = 30f;

        private static readonly Dictionary<ushort, PuckGunShot> _shots = new Dictionary<ushort, PuckGunShot>();
        private static ushort _nextShot = 1;

        /// <summary>Host / offline: a player with Puck Gun dashed.</summary>
        internal static void FirePuckGun(BallData b)
        {
            var dir = (Vector2)b.transform.up;
            var pos = (Vector2)b.transform.position + dir * (CardEffects.Radius(b.GetComponent<Collider2D>()) + ShotRadius + 0.05f);
            var id = _nextShot++;
            var team = Globals.gameManager.teams.IndexOf(b.team);
            Spawn(id, team, pos, dir * ShotSpeed, b, true);
            if (NetSession.HostInMatch) NetSession.Instance.HostSendShot(id, team, Match.Slot(b), pos, dir * ShotSpeed);
            NetLog.Info("Rounds: Puck Gun fired by " + (b.playerData != null && b.playerData.account != null ? b.playerData.account.name : "?"));
        }

        /// <summary>Client: the host fired a shot (visual only; the host decides what it hits).</summary>
        internal static void ClientShot(ushort id, int team, int slot, Vector2 pos, Vector2 vel)
        {
            Spawn(id, team, pos, vel, null, false);
        }

        /// <summary>
        /// Client: the host says the shot ended at <paramref name="at"/>. The client draws shots (like players)
        /// InterpDelay late, so it is usually still on its way: let it fly there, then remove it.
        /// </summary>
        internal static void ClientEndShot(ushort id, Vector2 at)
        {
            PuckGunShot s;
            if (_shots.TryGetValue(id, out s) && s != null) { s.EndAt = at; s.HasEnd = true; }
            else EndShot(id);
        }

        internal static void EndShot(ushort id)
        {
            PuckGunShot s;
            if (_shots.TryGetValue(id, out s)) { _shots.Remove(id); if (s != null) Object.Destroy(s.gameObject); }
        }

        private static void Spawn(ushort id, int team, Vector2 pos, Vector2 vel, BallData owner, bool authority)
        {
            var gm = Globals.gameManager;
            if (gm == null) return;
            var color = Color.white;
            try { color = ColorManager.GetColor(gm.teams[team].teamColor); } catch { }
            var z = owner != null ? owner.transform.position.z : (gm.puck != null ? gm.puck.transform.position.z : 0f);

            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "UltraPuck_PuckGunShot";
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.transform.position = new Vector3(pos.x, pos.y, z - 0.3f);
            go.transform.localScale = Vector3.one * ShotRadius * 2f;
            go.GetComponent<Renderer>().material.color = Color.Lerp(color, Color.white, 0.2f);
            var trail = go.AddComponent<TrailRenderer>();
            var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Particles/Alpha Blended");
            if (shader != null) trail.material = new Material(shader);
            trail.time = 0.25f;
            trail.startWidth = ShotRadius * 1.6f;
            trail.endWidth = 0f;
            trail.material.color = new Color(color.r, color.g, color.b, 0.8f);

            var shot = go.AddComponent<PuckGunShot>();
            shot.Id = id; shot.Velocity = vel; shot.Owner = owner; shot.Team = team; shot.Authority = authority;
            _shots[id] = shot;
        }

        internal sealed class PuckGunShot : MonoBehaviour
        {
            public ushort Id;
            public Vector2 Velocity;
            public BallData Owner;
            public int Team;
            public bool Authority;
            public Vector2 EndAt;
            public bool HasEnd;
            private float _age;
            private float _wait = -1f;
            private int _wallMask;

            private void Start()
            {
                foreach (var layer in new[] { "Wall", "Forcefield", "Spike" })
                {
                    var l = LayerMask.NameToLayer(layer);
                    if (l >= 0) _wallMask |= 1 << l;
                }
            }

            private void Update()
            {
                if (Authority) return; // the host moves its shots in FixedUpdate
                // Clients see players NetSession.InterpDelay in the past; hold the shot back by the same amount so it
                // lines up with them (otherwise it runs ahead and seems to pass through people the host says it missed).
                var r = GetComponent<Renderer>();
                if (_wait < 0f) { _wait = NetSession.InterpDelay; if (r != null) r.enabled = false; }
                if (_wait > 0f)
                {
                    _wait -= Time.deltaTime;
                    if (_wait > 0f) return;
                    if (r != null) r.enabled = true;
                    var trail = GetComponent<TrailRenderer>();
                    if (trail != null) trail.Clear();
                }
                var step = Velocity * Time.deltaTime;
                var pos = (Vector2)transform.position;
                if (HasEnd && Vector2.Dot(EndAt - (pos + step), Velocity) <= 0f)
                {
                    transform.position = new Vector3(EndAt.x, EndAt.y, transform.position.z);
                    _shots.Remove(Id);
                    Destroy(gameObject, 0.02f);
                    enabled = false;
                    return;
                }
                transform.position += (Vector3)step;
                _age += Time.deltaTime;
                if (_age > ShotLife + 1f) { _shots.Remove(Id); Destroy(gameObject); }
            }

            private void FixedUpdate()
            {
                if (!Authority) return;
                var gm = Globals.gameManager;
                if (gm == null || gm.replayManager.replaying) return;
                var dt = Time.fixedDeltaTime;
                _age += dt;
                var pos = (Vector2)transform.position;
                var step = Velocity * dt;
                // One cast a little wider (counts for players only) and one exact (walls, pylons), nearest first.
                var hits = Physics2D.CircleCastAll(pos, ShotRadius + PlayerLeeway, Velocity.normalized, step.magnitude)
                    .Where(h => h.collider != null && h.collider.GetComponent<BallData>() != null)
                    .Concat(Physics2D.CircleCastAll(pos, ShotRadius, Velocity.normalized, step.magnitude)
                        .Where(h => h.collider != null && h.collider.GetComponent<BallData>() == null));
                foreach (var hit in hits.OrderBy(h => h.distance))
                {
                    var c = hit.collider;
                    if (c == null || c.isTrigger) continue;
                    // Players carry extra helper colliders (e.g. "ObstructionCollider" on a *TeamObstruction layer,
                    // used by the bots): only a player's own body counts as a hit.
                    var owner = c.GetComponentInParent<BallData>();
                    if (owner != null && owner.gameObject != c.gameObject) continue;
                    if (LayerMask.LayerToName(c.gameObject.layer).Contains("Obstruction")) continue;
                    var ball = c.GetComponent<BallData>();
                    if (ball != null)
                    {
                        if (ball == Owner || !ball.alive) continue; // hits teammates too
                        Stun.Hit(ball, Velocity.normalized, Owner);
                        End(hit.centroid);
                        return;
                    }
                    var puck = c.GetComponent<Puck>();
                    if (puck != null)
                    {
                        if (Owner != null && puck.carryer == Owner.gameObject) continue; // the shooter's own puck
                        HitPuck(puck, Velocity.normalized);
                        End(hit.centroid);
                        return;
                    }
                    if (IsPylon(c) || (_wallMask & (1 << c.gameObject.layer)) != 0) // walls; the floor is ignored
                    {
                        NetLog.Debug("Puck Gun shot " + Id + " stopped by " + c.name + " (layer " + LayerMask.LayerToName(c.gameObject.layer) + ")");
                        End(hit.centroid);
                        return;
                    }
                }
                transform.position += (Vector3)step;
                if (_age > ShotLife) End(transform.position);
            }

            /// <summary>Knock the puck away (out of a carrier's hands, if someone has it).</summary>
            private static void HitPuck(Puck puck, Vector2 dir)
            {
                var carrier = puck.carryer != null ? puck.carryer.GetComponent<BallData>() : null;
                if (carrier != null) carrier.GetComponent<BallHandling>().Drop(puck.gameObject, 0.3f, false);
                var rb = puck.GetComponent<Rigidbody2D>();
                if (rb != null) rb.velocity = rb.velocity * 0.5f + dir * PuckKnock;
                NetLog.Info("Rounds: Puck Gun hit the puck" + (carrier != null ? " (knocked loose)" : string.Empty));
            }

            private void End(Vector2 at)
            {
                _shots.Remove(Id);
                if (NetSession.HostInMatch) NetSession.Instance.HostSendShotEnd(Id, at);
                Destroy(gameObject);
            }
        }
    }
}
