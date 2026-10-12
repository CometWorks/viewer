using System;
using HarmonyLib;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.GameSystems;
using VRage.Game.ModAPI;

namespace CometWorks.EntityViewer.Magnetar.Replay;

// Transition-only hooks; no per-frame or per-projectile patch. State changes are compacted before enqueue.
internal static class ReplayActionPatches
{
    private const string Id = "cometworks.viewer.replay.actions";
    private static Harmony _harmony;
    private static Action<MyCharacter> _changed;
    private static Action<object, MyDamageInformation> _damage;
    public static void Start(Action<MyCharacter> changed, Action<object, MyDamageInformation> damage, Action failed)
    {
        Stop(); _changed = changed; _damage = damage; _harmony = new Harmony(Id);
        // The native damage API has no unregister. A removable postfix leaves no dormant callbacks.
        try { _harmony.Patch(AccessTools.Method(typeof(MyDamageSystem), nameof(MyDamageSystem.RaiseAfterDamageApplied)), postfix: new HarmonyMethod(typeof(ReplayActionPatches), nameof(AfterDamage))); }
        catch (Exception e) { failed(); Console.WriteLine("Replay damage hook unavailable: " + e.Message); }
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
    private static void AfterDamage(object target, MyDamageInformation info)
    {
        try { _damage?.Invoke(target, info); } catch { }
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
        _changed = null; _damage = null;
        try { _harmony?.UnpatchAll(Id); } catch (Exception e) { Console.WriteLine("Replay action unpatch failed: " + e.Message); } finally { _harmony = null; }
    }
}
