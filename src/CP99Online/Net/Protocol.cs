namespace CP99Online.Net
{
    internal enum Msg : byte
    {
        Hello = 1,      // c->h  u16 version, str name, u8 team
        Welcome,        // h->c  u8 clientId, str hostName
        Reject,         // h->c  str reason
        Lobby,          // h->c  u8 n, {u8 id, str name, u8 team}
        SetTeam,        // c->h  u8 team
        KeepAlive,      // both, unreliable
        Bye,            // both  str reason
        StartMatch,     // h->c  see NetSession.Host
        Loaded,         // c->h  u8 matchId
        Snapshot,       // h->c  unreliable world state
        Input,          // c->h  unreliable controls
        Events,         // h->c  batched gameplay effects
        Goal,           // h->c  u8 team, u8 goalType
        ResetField,     // h->c  bool suddenDeath
        SuddenDeath,    // h->c
        TimeUp,         // h->c
        MatchEnd,       // h->c  final stats
        BoxReq,         // c->h  u8 controller: give me a player box
        BoxAssign,      // h->c  u8 slot, bool restore
        BoxState,       // c->h  u8 slot, u8 state, u8 team+1, str name, u8 symbol
        BoxTable,       // h->c  8 x {u8 owner, u8 state, u8 team+1, str name, u8 symbol}, options
        Scene,          // h->c  str menu scene to be in
        Nav,            // h->c  str menu path, str element path, i32 goals, f32 game length (GameSelection)
        CardState,      // h->c  Rounds Mode: phase, offers, picks/votes and everyone's cards (see NetSession.Cards)
        CardPick,       // c->h  u8 seat, u8 offer index
        CardShot,       // h->c  Puck Gun: u8 kind (0 fired: u16 id, u8 team, u8 slot, f32 x, y, vx, vy / 1 ended: u16 id)
        Pylons,         // h->c  unreliable: u8 n, n x {f32 x, f32 y} Pylon Goalie positions
    }

    internal enum Ev : byte
    {
        Shoot = 1,  // u8 slot
        Shove,      // u8 slot
        Boost,      // u8 slot, u16 boost index
        Bumper,     // u8 slot (255 = puck), u16 bumper index
        Check,      // u8 checker slot, u8 victim slot
        Spikes,     // u8 slot
        Fall,       // u8 slot, f32 x, f32 y, f32 rotZ, f32 vx, f32 vy
        Kill,       // u8 slot, bool blood
        Ping,       // u8 slot
    }

    internal static class Proto
    {
        public const ushort Version = 5;
        public const int MaxClients = 7;
        public const float Timeout = 10f;
        public const float SendRate = 1f / 60f;
        public const byte PuckSlot = 255;

        // Snapshot flag bits
        public const byte FReplaying = 1, FRoundOver = 2, FMatchOver = 4, FSuddenDeath = 8, FPaused = 16;
        public const byte BAlive = 1, BFallen = 2, BMesh = 4;

        // Input held bits
        public const byte HCharge = 1, HSprint = 2;

        public static ushort PackAngle(float deg)
        {
            var d = deg % 360f;
            if (d < 0f) d += 360f;
            return (ushort)(d / 360f * 65535f);
        }

        public static float UnpackAngle(ushort v) { return v / 65535f * 360f; }

        public static short PackUnit(float v)
        {
            if (v > 1f) v = 1f; else if (v < -1f) v = -1f;
            return (short)(v * 32767f);
        }

        public static float UnpackUnit(short v) { return v / 32767f; }
    }
}
