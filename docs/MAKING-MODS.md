# Making an UltraPuck mod

An UltraPuck mod is a small C# `.dll`. Players put it in the **`Mods` folder** inside the game folder
(next to `ClusterPuck99.exe`) and restart the game. It then appears in **Main menu > Mods** (and in the F8 menu).
From there they can turn it on and off and change its settings.

The built-in **FPS+**, **Smooth Motion**, **Better Boost**, **Dash Meter** and **Rounds Mode** mods use the same system. Online play is part of UltraPuck itself,
so it isn't in the list and can't be turned off.

## Quick start

1. Install the [.NET SDK](https://dotnet.microsoft.com/download), plus ClusterPuck 99 with UltraPuck installed.
2. Copy [`examples/ExampleMod`](../examples/ExampleMod) and rename the files.
   If the game isn't in the default Steam folder, change `GameDir` in the `.csproj`.
3. Run `dotnet build -c Release`. This copies the `.dll` into the game's `Mods` folder.
4. Start the game and open **Mods**. Your mod is in the list.

## The mod class

```csharp
using UltraPuck;

public class BigBoostMod : UltraMod
{
    public override string Id   { get { return "yourname.bigboost"; } } // unique; used to save on/off + settings
    public override string Name { get { return "Big Boost"; } }         // shown in the Mods menu (keep it short)
    public override string Author      { get { return "You"; } }
    public override string Version     { get { return "1.0"; } }
    public override string Description { get { return "Boosts are twice as strong."; } }
    public override bool EnabledByDefault { get { return false; } }
}
```

UltraPuck creates one instance of every non-abstract `UltraMod` class in each `.dll` in the `Mods` folder.
The class needs a constructor with no parameters.

### Hooks you can override

| Method | When it runs |
| --- | --- |
| `OnLoad()` | Once at startup, whether the mod is on or off. Add your settings here. |
| `OnEnable()` | When the mod is turned on, including at startup if it was left on. **By default it applies every `[HarmonyPatch]` class in your `.dll`.** |
| `OnDisable()` | When the mod is turned off. **By default it removes those patches.** |
| `OnUpdate()` | Every frame while the mod is on. |
| `OnGUI()` | IMGUI drawing (`GUI`, `GUILayout`) while the mod is on. |
| `OnSceneLoaded(name)` | After a scene loads while the mod is on: `MainMenu`, `PlayerSelection`, `GameSelection`, or an arena. |

You also get:

- `Harmony`: your mod's own Harmony instance.
- `Log`: writes to `BepInEx/LogOutput.log`.
- `Config`: the file `BepInEx/config/ultrapuck.mods.cfg`.
- `Enabled`: whether the mod is on right now.

If your mod throws an exception, UltraPuck turns it off for that session and shows the error on its page,
so the game keeps running.

## Settings

Add settings in `OnLoad`. They appear on the mod's page in the Mods menu, and selecting one moves it to the
next option. Values are saved automatically.

```csharp
private ConfigEntry<bool> _trail;
private ConfigEntry<int> _power;

public override void OnLoad()
{
    _trail = AddToggle("Trail", "Boost Trail", true);                         // On / Off
    _power = AddChoice("Power", "Boost Power", 2, new[] { 1, 2, 3, 5 },
                       v => v + "x");                                          // cycles 1x, 2x, 3x, 5x
}
```

Read them with `_trail.Value` and `_power.Value`.

For a longer list, use a page: the mod's page shows one "Label >" item that opens its own list. Rounds Mode's
Toggle Cards page is built this way:

```csharp
AddPage("Toggle Colours", () => colours.Select(c =>
    ModSetting.Toggle(c.Name, () => c.Enabled, v => c.Enabled = v)).ToList());
```

A page of On/Off settings also gets "Turn All On" and "Turn All Off" items.

## Changing the game: Harmony patches

To find what to change, open the game's code in a decompiler such as
[dnSpy](https://github.com/dnSpyEx/dnSpy) or [ILSpy](https://github.com/icsharpcode/ILSpy). The code is in
`ClusterPuck99_Data/Managed/Assembly-CSharp.dll`. Then patch it with [Harmony](https://harmony.pardeike.net/):

```csharp
[HarmonyPatch(typeof(GameManager), nameof(GameManager.ScoreGoal))]
internal static class GoalPatch
{
    private static void Postfix() { /* runs after every goal */ }
}
```

Because `OnEnable` and `OnDisable` apply and remove these patches, turning your mod off in the menu really
turns it off. If one `.dll` holds several mods, override `OnEnable` and call
`Harmony.CreateClassProcessor(typeof(GoalPatch)).Patch()` for each mod's own patches.

## Online play

The host runs the match and everyone else sees the result. A mod that changes gameplay (physics, speed, rules)
therefore takes effect when **the host** has it on. A mod that only changes what you see, such as an overlay,
camera or colours, works for each player on their own.

## Making cards for Rounds Mode

When Rounds Mode is on, everyone picks a card after each goal. A card can be a stat change, a custom effect,
or both. Register your cards in your mod's `OnLoad`:

```csharp
using UltraPuck;
using UnityEngine;

public class GlassCannonCard : UltraCard
{
    public override string Id          { get { return "yourname.glasscannon"; } } // unique, same on every PC
    public override string Name        { get { return "Glass Cannon"; } }
    public override string Description { get { return CardText.Stat(1.5f, "Shot Power", true) + "\n" +
                                                       CardText.Stat(1f / 0.7f, "Knockback Taken", false); } }
    public override Color Color        { get { return new Color(0.9f, 0.4f, 0.9f); } }
    public override int MaxPerPlayer   { get { return 2; } }     // 0 = no limit
    public override float Rarity       { get { return 0.5f; } }  // offered half as often

    public override void Apply(CardStats stats)                  // once per copy the player owns
    {
        stats.ShotPower *= 1.5f;
        stats.Weight *= 0.7f;
    }
}

public class MyMod : UltraMod
{
    // ... Id, Name ...
    public override void OnLoad() { Cards.Register(new GlassCannonCard()); }
}
```

`CardStats` has these multipliers (1 = no change):
- `MoveSpeed`, `ShotPower`, `ChargeSpeed`, `DashCooldown`, `PickupRange` and `Size`.
- `Weight`: heavier means you take less knockback and your dash checks give more. Movement and dash speed are corrected for it.
- `OpponentSpeed`, which slows every opponent.

It also has `ExtraDashes`, `SpawnCloser` (0.2 = 20% closer to the puck at each face-off), `Pylons` and `PuckGun`.
For anything else, override `ApplyToBall(BallData ball, int copies)`. It runs on the player's ball when they
get the card and after every field reset.

### Card text: standard terms and colours

Write descriptions with `CardText`, so every card uses the same words and colours. Green means good for the
card's owner, red means bad, and gold means neutral. For example, `CardText.Stat(1.15f, "Move Speed", true)`
shows a green "+15% Move Speed". Use `CardText.Good`, `Bad` and `Neutral` for other text. The players' stats
tooltip (hover a name in the corner list) uses the same terms:

| Term | Meaning | Good direction |
| --- | --- | --- |
| Move Speed | How fast you move | + |
| Knockback Taken | How far hits (dashes, checks, Puck Gun) push you. It's 1 / `CardStats.Weight`, so Weight 2 = −50% | − |
| Knockback Given | How far your dash checks push others. It's `CardStats.Weight`, so Weight 2 = +100% | + |
| Size | Ball size | neutral |
| Shot Power | How hard your quick and charged shots hit the puck (also raises the puck's speed limit) | + |
| Charge-up Speed | How fast a charge shot fills | + |
| Dash Charges | Dashes you can store | + |
| Dash Recharge | Time for one dash charge to refill (normally 2 s) | − |
| Pick-up Range | How far away you can grab a loose puck | + |
| Face-off Distance | How far from the puck you start each face-off | − |
| Opponent Move Speed | Every opponent's Move Speed | − |
| Stun | Seconds an opponent can't control their player (they still slide) | + |
| Pylon Goalie | Dummy players at your net | + |

Online, the host runs the game, so effects only happen on the host. Everyone playing together needs the
same card mods, so each card shows the right name and description.

## Registering from a BepInEx plugin

If your mod is already a BepInEx plugin, it can join the Mods menu without going in the `Mods` folder:

```csharp
UltraPuck.ModManager.Register(new MyMod());   // e.g. in your plugin's Awake
```

## Sharing your mod

Send people the `.dll`. Installing it means putting it in the game's `Mods` folder.
**Main menu > Mods > Open Mods Folder** opens that folder.
