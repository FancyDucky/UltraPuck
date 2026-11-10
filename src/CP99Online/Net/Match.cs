using System.Collections.Generic;
using UnityEngine;

namespace CP99Online.Net
{
    /// <summary>
    /// The online match running in the current Arena3D scene. Slots are indexes into the player
    /// list, which is identical (same order) on host and clients.
    /// </summary>
    internal static class Match
    {
        public static bool Active;
        public static byte Id;
        public static List<PlayerData> Players;
        /// <summary>Client id that controls each slot. 0 = host machine (local player or AI).</summary>
        public static List<byte> Owners;
        public static int LocalSlot = -1;
        public static bool ClientPending;

        public static BallData[] Balls = new BallData[0];
        public static ReplayStorage[] Storage = new ReplayStorage[0];
        public static ReplayStorage PuckStorage;
        public static readonly Dictionary<BallData, int> SlotOf = new Dictionary<BallData, int>();

        public static Boost[] Boosts = new Boost[0];
        public static Bumper[] Bumpers = new Bumper[0];
        public static readonly Dictionary<Boost, int> BoostIndex = new Dictionary<Boost, int>();
        public static readonly Dictionary<Bumper, int> BumperIndex = new Dictionary<Bumper, int>();

        /// <summary>PlayerData entries this mod added to PlayerSelectionManager_V2.players.</summary>
        public static readonly HashSet<PlayerData> OnlinePlayers = new HashSet<PlayerData>();

        public static void End()
        {
            Active = false;
            ClientPending = false;
            Balls = new BallData[0];
            Storage = new ReplayStorage[0];
            PuckStorage = null;
            SlotOf.Clear();
            Boosts = new Boost[0];
            Bumpers = new Bumper[0];
            BoostIndex.Clear();
            BumperIndex.Clear();
        }

        public static int Slot(BallData b)
        {
            int s;
            return b != null && SlotOf.TryGetValue(b, out s) ? s : -1;
        }

        public static BallData Ball(int slot)
        {
            return slot >= 0 && slot < Balls.Length ? Balls[slot] : null;
        }

        public static bool IsRemote(int slot)
        {
            return slot >= 0 && Owners != null && slot < Owners.Count && Owners[slot] != 0;
        }

        /// <summary>
        /// Maps slots to spawned balls. SpawnPlayers walks the player list in order and appends each
        /// ball to teams[player.team].members, so the k-th player of team t is members[start_t + k].
        /// </summary>
        public static bool Bind(GameManager gm, int[] memberCountsBefore)
        {
            SlotOf.Clear();
            Balls = new BallData[Players.Count];
            Storage = new ReplayStorage[Players.Count];
            var next = (int[])memberCountsBefore.Clone();
            for (var i = 0; i < Players.Count; i++)
            {
                var t = Players[i].team;
                if (t < 0 || t >= gm.teams.Count || next[t] >= gm.teams[t].members.Count)
                {
                    NetLog.Warn("Could not map slot " + i + " to a ball");
                    return false;
                }
                var ball = gm.teams[t].members[next[t]++];
                Balls[i] = ball;
                Storage[i] = ball.GetComponent<ReplayStorage>();
                SlotOf[ball] = i;
            }
            PuckStorage = gm.puck.GetComponent<ReplayStorage>();
            BuildRegistries();
            return true;
        }

        private static void BuildRegistries()
        {
            Boosts = SortByHierarchy(Object.FindObjectsOfType<Boost>());
            Bumpers = SortByHierarchy(Object.FindObjectsOfType<Bumper>());
            BoostIndex.Clear();
            BumperIndex.Clear();
            for (var i = 0; i < Boosts.Length; i++) BoostIndex[Boosts[i]] = i;
            for (var i = 0; i < Bumpers.Length; i++) BumperIndex[Bumpers[i]] = i;
        }

        /// <summary>Orders components by their sibling-index path, which is the same on every machine.</summary>
        private static T[] SortByHierarchy<T>(T[] items) where T : Component
        {
            var keys = new string[items.Length];
            for (var i = 0; i < items.Length; i++)
            {
                var t = items[i].transform;
                var key = string.Empty;
                while (t != null)
                {
                    key = t.GetSiblingIndex().ToString("D4") + "/" + key;
                    t = t.parent;
                }
                keys[i] = key;
            }
            System.Array.Sort(keys, items, System.StringComparer.Ordinal);
            return items;
        }
    }
}
