using System;
using HarmonyLib;
using Sandbox.Game.Entities.Character;

namespace CometWorks.EntityViewer.Magnetar.Replay;

// Transition-only hooks; no per-frame or per-projectile patch. State changes are compacted before enqueue.
internal static class ReplayActionPatches
{
    private const string Id = "cometworks.viewer.replay.actions";
    private static Harmony _harmony;
    private static Action<MyCharacter> _changed;
    public static void Start(Action<MyCharacter> changed, Action failed)
    {
        Stop(); _changed = changed; _harmony = new Harmony(Id);
        foreach (var name in new[] { "OnBeginShoot", "StopShooting", "set_ZoomMode", "set_UsingEntity" })
        {
            try
            {
                var method = AccessTools.Method(typeof(MyCharacter), name);
                if (method == null) { failed(); continue; }
                _harmony.Patch(method, prefix: new HarmonyMethod(typeof(ReplayActionPatches), nameof(Before)), postfix: new HarmonyMethod(typeof(ReplayActionPatches), nameof(After)));
            }
            catch (Exception e) { failed(); Console.WriteLine("Replay action hook unavailable: " + name + ": " + e.Message); }
        }
    }
    private static void Before(MyCharacter __instance, out int __state)
    {
        __state = -1;
        try { if (_changed != null) __state = ServerReplayRecorder.ActionState(__instance); } catch { }
    }
    private static void After(MyCharacter __instance, int __state)
    {
        try { if (__state >= 0 && __state != ServerReplayRecorder.ActionState(__instance)) _changed?.Invoke(__instance); } catch { }
    }
    public static void Stop()
    {
        _changed = null;
        try { _harmony?.UnpatchAll(Id); } catch (Exception e) { Console.WriteLine("Replay action unpatch failed: " + e.Message); } finally { _harmony = null; }
    }
}
