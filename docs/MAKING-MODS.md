# Making an UltraPuck mod

An UltraPuck mod is a small C# `.dll`. Players put it in the **`Mods` folder** inside the game folder
(next to `ClusterPuck99.exe`) and restart the game. It then appears in **Main menu > Mods** (and in the F8 menu).
From there they can turn it on and off and change its settings.

The built-in **FPS+**, **Smooth Motion** and **Better Boost** mods use the same system. Online play is part of UltraPuck itself,
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

## Registering from a BepInEx plugin

If your mod is already a BepInEx plugin, it can join the Mods menu without going in the `Mods` folder:

```csharp
UltraPuck.ModManager.Register(new MyMod());   // e.g. in your plugin's Awake
```

## Sharing your mod

Send people the `.dll`. Installing it means putting it in the game's `Mods` folder.
**Main menu > Mods > Open Mods Folder** opens that folder.
