using System;
using System.Collections.Generic;
using System.Numerics;
using System.Linq;
using Dalamud.Plugin;

namespace Consolidator.Game;

public class ArCharInfo
{
    public ulong Cid;
    public string Name = "";
    public string World = "";

    // AR enrolls a character in two independent lists:
    //   Enabled          -> shows in the Retainers tab
    //   WorkshopEnabled  -> shows in the Deployables tab
    // Each has a matching exclusion flag that AR's own UI honours when building
    // those lists (see DeployablesTab.cs: "if (x.ExcludeWorkshop) continue;").
    public bool Enabled;
    public bool ExcludeRetainer;
    public bool WorkshopEnabled;
    public bool ExcludeWorkshop;

    // AR refreshes this every time it logs in to run subs, so it stays current
    // without us visiting the character ourselves.
    public long Gil;

    public bool InRetainersTab => Enabled && !ExcludeRetainer;
    public bool InDeployablesTab => WorkshopEnabled && !ExcludeWorkshop;
}

// Thin wrappers around AutoRetainer and Lifestream IPC. We call the raw IPC
// subscriber strings directly (verified against AutoRetainer's Modules/IPC.cs and
// Lifestream's IPC/IPCProvider.cs) so we don't bundle their assemblies. Every call
// is guarded: if the other plugin isn't installed, these no-op / return defaults.
public static class PluginIpc
{
    private static IDalamudPluginInterface Pi => Plugin.PluginInterface;

    // ================= AutoRetainer =================

    public static List<ulong> GetRegisteredCharacters()
    {
        try
        {
            return Pi.GetIpcSubscriber<List<ulong>>("AutoRetainer.GetRegisteredCIDs").InvokeFunc()
                   ?? new List<ulong>();
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"GetRegisteredCIDs failed: {ex.Message}");
            return new List<ulong>();
        }
    }

    // Last error from GetArCharInfo, surfaced in the UI so a silent reflection or
    // IPC-signature mismatch doesn't just look like "no characters".
    public static string LastError = "";

    // AR hands back its own OfflineCharacterData type. Read it reflectively off the
    // boxed object so we never need a reference to AR's assembly.
    //
    // Note AR registers this as GetIpcProvider<ulong, OfflineCharacterData>. Dalamud
    // resolves subscribers by generic signature, so requesting <ulong, object> can
    // fail on the type check depending on Dalamud version. We try that first (it
    // avoids needing AR's type at all) and fall back to invoking through the
    // provider's own reflected type if it doesn't take.
    public static ArCharInfo? GetArCharInfo(ulong cid)
    {
        object? data = null;

        try
        {
            data = Pi.GetIpcSubscriber<ulong, object>("AutoRetainer.GetOfflineCharacterData")
                     .InvokeFunc(cid);
        }
        catch (Exception ex)
        {
            if (string.IsNullOrEmpty(LastError))
            {
                LastError = $"GetOfflineCharacterData failed: {ex.Message}";
                Plugin.Log.Warning(LastError);
            }
        }

        if (data == null) return null;

        try
        {
            var t = data.GetType();

            // Dalamud serializes across the IPC boundary, so when we subscribe with
            // <ulong, object> we get a Newtonsoft JObject rather than AR's real
            // OfflineCharacterData type. JObject has no public fields, so plain
            // reflection reads nothing — index it by key instead.
            if (t.Name == "JObject")
            {
                var info = new ArCharInfo
                {
                    Cid = cid,
                    Name = JStr(data, "Name"),
                    World = JStr(data, "World"),
                    Enabled = JBool(data, "Enabled"),
                    ExcludeRetainer = JBool(data, "ExcludeRetainer"),
                    WorkshopEnabled = JBool(data, "WorkshopEnabled"),
                    ExcludeWorkshop = JBool(data, "ExcludeWorkshop"),
                    Gil = JLong(data, "Gil"),
                };

                if (string.IsNullOrEmpty(info.Name) && string.IsNullOrEmpty(LastError))
                {
                    LastError = $"JObject read but Name empty. Keys: {JKeys(data)}";
                    Plugin.Log.Warning(LastError);
                }

                return info;
            }

            // Real typed object (if a future Dalamud stops serializing) — read fields.
            var typed = new ArCharInfo
            {
                Cid = cid,
                Name = GetString(t, data, "Name"),
                World = GetString(t, data, "World"),
                Enabled = GetBool(t, data, "Enabled"),
                ExcludeRetainer = GetBool(t, data, "ExcludeRetainer"),
                WorkshopEnabled = GetBool(t, data, "WorkshopEnabled"),
                ExcludeWorkshop = GetBool(t, data, "ExcludeWorkshop"),
                Gil = GetLong(t, data, "Gil"),
            };

            if (string.IsNullOrEmpty(typed.Name) && string.IsNullOrEmpty(LastError))
            {
                var fields = string.Join(", ", t.GetFields().Take(10).Select(f => f.Name));
                LastError = $"Read {t.Name} but Name was empty. Fields seen: {fields}";
                Plugin.Log.Warning(LastError);
            }

            return typed;
        }
        catch (Exception ex)
        {
            if (string.IsNullOrEmpty(LastError))
            {
                LastError = $"Reflection on OfflineCharacterData failed: {ex.Message}";
                Plugin.Log.Warning(LastError);
            }
            return null;
        }
    }

    // ---- JObject access, all by reflection so we never reference Newtonsoft ----
    // JObject implements the non-generic IDictionary-ish indexer this["key"], which
    // returns a JToken. ToString() on a JValue gives the raw scalar.

    private static object? JGet(object jobj, string key)
    {
        try
        {
            var indexer = jobj.GetType().GetProperty("Item", new[] { typeof(string) });
            return indexer?.GetValue(jobj, new object[] { key });
        }
        catch { return null; }
    }

    private static string JStr(object jobj, string key)
    {
        var tok = JGet(jobj, key);
        return tok?.ToString() ?? "";
    }

    private static long JLong(object jobj, string key)
    {
        var s = JStr(jobj, key);
        return long.TryParse(s, out var v) ? v : 0;
    }

    private static bool JBool(object jobj, string key)
    {
        var s = JStr(jobj, key);
        return bool.TryParse(s, out var b) && b;
    }

    private static string JKeys(object jobj)
    {
        try
        {
            // JObject exposes Properties() -> IEnumerable<JProperty>, each with .Name
            var props = jobj.GetType().GetMethod("Properties")?.Invoke(jobj, null);
            if (props is not System.Collections.IEnumerable e) return "(none)";

            var names = new List<string>();
            foreach (var p in e)
            {
                var n = p.GetType().GetProperty("Name")?.GetValue(p)?.ToString();
                if (n != null) names.Add(n);
                if (names.Count >= 12) break;
            }
            return names.Count == 0 ? "(none)" : string.Join(", ", names);
        }
        catch { return "(unreadable)"; }
    }

    public static bool IsAutoRetainerAvailable()
    {
        try
        {
            Pi.GetIpcSubscriber<List<ulong>>("AutoRetainer.GetRegisteredCIDs").InvokeFunc();
            return true;
        }
        catch { return false; }
    }

    public static bool GetMultiModeEnabled()
    {
        try { return Pi.GetIpcSubscriber<bool>("AutoRetainer.GetMultiModeEnabled").InvokeFunc(); }
        catch { return false; }
    }

    // AR's multi-mode will happily try to drive the character while we're mid-run.
    // We suppress it for the duration of a sweep and restore it afterwards.
    public static void SetMultiModeEnabled(bool value)
    {
        try { Pi.GetIpcSubscriber<bool, object>("AutoRetainer.SetMultiModeEnabled").InvokeAction(value); }
        catch { /* AR not present */ }
    }

    // ================= Lifestream =================

    // Lifestream.ExecuteCommand(args) is literally P.ProcessCommand("/li", args),
    // so whatever the user would type after "/li" works verbatim here.
    public static bool LifestreamExecute(string args)
    {
        try
        {
            Pi.GetIpcSubscriber<string, object>("Lifestream.ExecuteCommand").InvokeAction(args);
            return true;
        }
        catch { return false; }
    }

    public static bool LifestreamIsBusy()
    {
        try { return Pi.GetIpcSubscriber<bool>("Lifestream.IsBusy").InvokeFunc(); }
        catch { return false; }
    }

    public static bool LifestreamTeleportToFc()
    {
        try { return Pi.GetIpcSubscriber<bool>("Lifestream.TeleportToFC").InvokeFunc(); }
        catch { return false; }
    }

    // Lifestream.EnqueuePropertyShortcut(PropertyType type, HouseEnterMode? mode)
    //   PropertyType:   Auto=0, Home=1, FC=2, Apartment=3, Inn=4, Shared_Estate=5
    //   HouseEnterMode: None=0, Walk_to_door=1, Enter_house=2, Enter_workshop=3
    //
    // TeleportToFC() passes no mode, so it stops at the plot entrance. This is the
    // only call that actually walks inside. Passing a null mode makes Lifestream
    // fall back to whatever that house is configured for in its own settings.
    //
    // Both params are enums; Dalamud marshals them by underlying int.
    public static bool LifestreamGoToFc(int? enterMode)
    {
        try
        {
            Pi.GetIpcSubscriber<int, int?, object>("Lifestream.EnqueuePropertyShortcut")
              .InvokeAction(2 /* PropertyType.FC */, enterMode);
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"EnqueuePropertyShortcut failed ({ex.Message}); " +
                               "falling back to TeleportToFC (stops at the door).");
            return LifestreamTeleportToFc();
        }
    }

    public static void LifestreamAbort()
    {
        try { Pi.GetIpcSubscriber<object>("Lifestream.Abort").InvokeAction(); }
        catch { /* ignore */ }
    }

    // Lifestream has no *setter* for PathToEntrance — that list is only ever written
    // by its own House Registration UI. But it does expose the recorded path as a
    // getter, plus a mover and a plot-entrance lookup, which is enough to use its
    // pathing when one exists and fall back to vnavmesh when it doesn't.

    // Returns the recorded PathToEntrance for this character's FC house, if any.
    public static List<Vector3> LifestreamGetFcEntrancePath(ulong cid)
    {
        try
        {
            var res = Pi.GetIpcSubscriber<ulong, object>("Lifestream.GetHousePathData").InvokeFunc(cid);
            if (res == null) return new List<Vector3>();

            // Returns a (Private, FC) tuple; we want Item2.
            var fc = res.GetType().GetField("Item2")?.GetValue(res);
            if (fc == null) return new List<Vector3>();

            var path = fc.GetType().GetField("PathToEntrance")?.GetValue(fc);
            if (path is List<Vector3> list) return list;

            // May come back as a JArray of JObjects if Dalamud serialized it.
            if (path is System.Collections.IEnumerable e)
            {
                var outp = new List<Vector3>();
                foreach (var p in e)
                {
                    var t = p.GetType();
                    float F(string n) =>
                        float.TryParse(
                            (t.GetField(n)?.GetValue(p) ?? t.GetProperty(n)?.GetValue(p))?.ToString(),
                            out var f) ? f : 0f;
                    outp.Add(new Vector3(F("X"), F("Y"), F("Z")));
                }
                return outp;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug($"GetHousePathData failed: {ex.Message}");
        }
        return new List<Vector3>();
    }

    // Walk a recorded path using Lifestream's own follower.
    public static bool LifestreamMove(List<Vector3> path)
    {
        try
        {
            Pi.GetIpcSubscriber<List<Vector3>, object>("Lifestream.Move").InvokeAction(path);
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug($"Lifestream.Move failed: {ex.Message}");
            return false;
        }
    }

    // Lifestream's own lookup for where a plot's entrance is.
    public static Vector3? LifestreamGetPlotEntrance(uint territory, int plot)
    {
        try
        {
            return Pi.GetIpcSubscriber<uint, int, Vector3?>("Lifestream.GetPlotEntrance")
                     .InvokeFunc(territory, plot);
        }
        catch { return null; }
    }

    public static bool IsLifestreamAvailable()
    {
        try
        {
            Pi.GetIpcSubscriber<bool>("Lifestream.IsBusy").InvokeFunc();
            return true;
        }
        catch { return false; }
    }

    // ================= vnavmesh =================
    // Lifestream gets us to the aetheryte; it does not free-roam. Closing the last
    // few yalms to the receiving character is vnavmesh's job.

    public static bool VnavIsReady()
    {
        try { return Pi.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady").InvokeFunc(); }
        catch { return false; }
    }

    // Walks to a point. fly=false keeps us on the ground.
    public static bool VnavMoveTo(Vector3 dest)
    {
        try
        {
            return Pi.GetIpcSubscriber<Vector3, bool, bool>("vnavmesh.SimpleMove.PathfindAndMoveTo")
                     .InvokeFunc(dest, false);
        }
        catch { return false; }
    }

    public static bool VnavIsRunning()
    {
        try { return Pi.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning").InvokeFunc(); }
        catch { return false; }
    }

    public static bool VnavPathfindInProgress()
    {
        try { return Pi.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress").InvokeFunc(); }
        catch { return false; }
    }

    public static void VnavStop()
    {
        try { Pi.GetIpcSubscriber<object>("vnavmesh.Path.Stop").InvokeAction(); }
        catch { /* ignore */ }
    }

    public static bool IsVnavAvailable()
    {
        try
        {
            Pi.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady").InvokeFunc();
            return true;
        }
        catch { return false; }
    }

    // ================= reflection helpers =================

    private static string GetString(Type t, object o, string field)
    {
        try
        {
            var v = t.GetField(field)?.GetValue(o) ?? t.GetProperty(field)?.GetValue(o);
            return v?.ToString() ?? "";
        }
        catch { return ""; }
    }

    private static long GetLong(Type t, object o, string field)
    {
        try
        {
            var v = t.GetField(field)?.GetValue(o) ?? t.GetProperty(field)?.GetValue(o);
            return v == null ? 0 : Convert.ToInt64(v);
        }
        catch { return 0; }
    }

    private static bool GetBool(Type t, object o, string field)
    {
        try
        {
            var v = t.GetField(field)?.GetValue(o) ?? t.GetProperty(field)?.GetValue(o);
            return v is bool b && b;
        }
        catch { return false; }
    }
}
