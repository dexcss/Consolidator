using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Configuration;

namespace Consolidator;

[Serializable]
public class CharEntry
{
    // 0 for a hand-added character we've never matched to an AR CID.
    public ulong Cid;
    public string Name = "";
    public string World = "";
    public bool Enabled = true;

    // Provenance, purely for display: how did this row get here?
    public string Source = "Manual";   // "Manual" | "Retainers" | "Deployables" | "Both"

    // Region/DC, derived from the World via the game's sheet. Cached so we don't
    // hit the sheet every frame while drawing the table.
    public string Region = "";
    public string DataCenter = "";

    // AR already tracks gil per character (OfflineCharacterData.Gil) and refreshes
    // it whenever it logs in to run submarines — so this stays current on its own
    // without us ever having to visit the character.
    public long LastSeenGil;
    public DateTime LastSeenGilAt = DateTime.MinValue;

    // When Consolidator last finished this character.
    public DateTime LastRunAt = DateTime.MinValue;
    public long LastRunSent;

    // Manual ordering. Lower runs first. Only used when the roster is sorted by
    // "Order"; the other sorts are views and don't disturb this.
    public int Order;

    public string Full => string.IsNullOrWhiteSpace(World) ? Name : $"{Name}@{World}";
}

// A thing to hand over. Gil is modelled as an item with Id == 0 so the
// "trade everything except KeepAmount" rule is expressed identically for both.
[Serializable]
public class DepositItem
{
    public uint ItemId;          // 0 == gil
    public string Label = "";    // display name, resolved from sheet or user-typed
    public long KeepAmount;      // leave this much behind on each character
    public bool Enabled = true;
    public bool Hq;              // v2 (items); ignored for gil

    public bool IsGil => ItemId == 0;
}

// A run that was interrupted (crash, force-quit, /xlplugins reload). Persisted so
// we can offer to pick up where we left off instead of starting over.
//
// Note we deliberately do NOT persist the SweepState. Resuming into, say,
// ConfirmTrade would be meaningless on a fresh client — there's no trade window.
// And after a crash mid-trade you cannot know whether the server committed it.
// Instead we restart the current character from the top; because gil is always
// re-read from the game rather than tracked in a counter, the balance check works
// out what's actually left. Self-correcting, and it needs no state fidelity.
[Serializable]
public class RunState
{
    public bool Active;                       // a run was in progress when we last saved
    public DateTime StartedAt;
    public DateTime SavedAt;
    public int Index;                         // character we were on
    public List<CharEntry> Queue = new();     // the queue as it was at Start()
    public List<ulong> Completed = new();     // CIDs finished cleanly
    public List<string> CompletedNames = new();
    public long TotalSent;                    // gil confirmed moved so far

    public void Clear()
    {
        Active = false;
        Index = 0;
        Queue.Clear();
        Completed.Clear();
        CompletedNames.Clear();
        TotalSent = 0;
    }
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    // --- Crash protection ---
    // Save progress after each character so an interrupted run can be resumed.
    public bool CrashProtection = true;
    public RunState Run = new();

    // --- Destination ---
    // Passed verbatim to Lifestream.ExecuteCommand, i.e. exactly what you'd type
    // after "/li". Lifestream's own fuzzy matcher resolves it, so this accepts
    // "ul'dah", "limsa", a world name, an aethernet shard, etc.
    public string TargetWorld = "Sophia";
    public string Destination = "ul'dah";

    // --- Receiving character ---
    // Must match the Sender format AR/Dropbox use: "First Last@World".
    public string MainName = "";
    public string MainWorld = "";
    public string MainFull => string.IsNullOrWhiteSpace(MainWorld)
        ? MainName
        : $"{MainName}@{MainWorld}";

    // --- What to hand over ---
    public List<DepositItem> Items = new()
    {
        new DepositItem { ItemId = 0, Label = "Gil", KeepAmount = 10000, Enabled = true },
    };

    // --- Roster ---
    // Starts empty and stays that way until you add or import. Nothing is ever
    // auto-populated behind your back.
    public List<CharEntry> Characters = new();

    // Roster sort column. 0 = manual run order (the default, and the one the
    // sweep actually follows). Others are views only.
    public int SortColumn = 0;
    public bool SortAscending = true;

    // Which AR tabs the Import button pulls from.
    public bool ImportFromRetainers;
    public bool ImportFromDeployables = true;

    // Also take items out of the armoury, not just the four main bags.
    //
    // Off by default and deliberately so: the armoury holds gear, and an unattended
    // run across dozens of characters is exactly the wrong place to discover you've
    // traded away equipment you were still using.
    public bool IncludeArmoury;

    // --- Behaviour ---
    public bool ReturnToFc = true;

    // Skip characters whose *cached* gil (AR's figure, from the last time it ran
    // subs on them) is already at or below the floor — without logging in at all.
    //
    // This is distinct from the live check we always do after login: that one reads
    // real gil and is always correct. This one trades accuracy for speed, since it
    // avoids the relog entirely, but it's only as fresh as AR's last visit. Off by
    // default because a stale figure means silently skipping a character that has
    // gil to give.
    public bool SkipByCachedGil;

    // Don't trust a cached figure older than this. 0 = no age limit.
    public int CachedGilMaxAgeHours = 24;

    // Keep retrying a trade that isn't going through, rather than giving up after a
    // few attempts.
    //
    // The case this exists for: several accounts all feeding the same main. The
    // receiving character is frequently mid-trade with someone else, so a rejection
    // isn't a failure at all — it's just contention, and waiting is the right answer.
    //
    // It's a *time* budget rather than an attempt count, because attempts are cheap
    // and meaningless here; what matters is how long we're willing to sit on one
    // character before deciding something is genuinely wrong. Unbounded retrying
    // would let a real problem (main logged out, inventory full) stall the queue
    // forever without ever saying so.
    public bool PatientRetry;
    public int PatientRetryMinutes = 15;

    // Wait this long between attempts while being patient. Hammering the trade
    // request every 1.5s for fifteen minutes is neither necessary nor polite.
    // Jittered at use, so two accounts retrying on the same cadence can't stay
    // locked in step with each other indefinitely.
    public int PatientRetryDelaySeconds = 20;

    // When a character exhausts its patience budget, put them at the back of the
    // queue rather than skipping them outright.
    //
    // This is what stops contention silently eating characters. A busy receiver is a
    // transient condition — by the time we've worked through everyone else they're
    // very likely free, so a character that couldn't get a trade in should be retried
    // later rather than written off.
    public bool RequeueOnBusy = true;

    // How many times one character may be sent to the back of the queue before we
    // accept they're genuinely not going to work.
    public int MaxRequeues = 2;

    // Walk inside the house after teleporting, rather than logging out on the plot.
    // Off by default: entering means walking to the door and interacting, which is
    // more that can go wrong than simply teleporting and logging out.
    public bool EnterHouse;

    // Log out at the end of each character rather than relogging straight from
    // wherever we're standing.
    public bool LogoutAtEnd = true;
    // Dry run: do everything except actually confirm a trade.
    public bool NoOp = false;
    public bool StopOnError = false;

    // --- Timeouts (seconds) ---
    public int LoginTimeout = 120;
    public int TravelTimeout = 180;
    public int LocateMainTimeout = 60;
    public int ApproachTimeout = 120;

    // How long the receiving character must be continuously loaded, rendered and
    // in range before we fire /trade. Guards against trading a half-streamed-in
    // player right after a zone change, which the server silently rejects.
    public int TradeSettleMs = 1500;

    // Extra settle time after arriving at the destination, before we start looking
    // for the receiver at all.
    public int ArrivalSettleMs = 2000;
    public int TradeTimeout = 60;
    public int FcReturnTimeout = 180;
    public int EnterHouseTimeout = 120;

    // Collapse the item list to exactly one gil row, and drop anything malformed.
    // Duplicate gil rows caused a nasty bug: the UI bound to the first, the sweeper
    // found the second, and unticking "Send gil" appeared to do nothing.
    public bool Normalise()
    {
        var changed = false;

        var gils = Items.Where(i => i.IsGil).ToList();
        if (gils.Count == 0)
        {
            Items.Insert(0, new DepositItem
            {
                ItemId = 0, Label = "Gil", KeepAmount = 10000, Enabled = true,
            });
            changed = true;
        }
        else if (gils.Count > 1)
        {
            // Keep the first; if ANY of them was disabled, honour that — erring
            // towards not sending gil is the safe direction.
            var keep = gils[0];
            if (gils.Any(g => !g.Enabled)) keep.Enabled = false;

            foreach (var dupe in gils.Skip(1)) Items.Remove(dupe);
            changed = true;
        }

        // Deduplicate real items too, on (id, hq).
        var seen = new HashSet<(uint, bool)>();
        foreach (var it in Items.Where(i => !i.IsGil).ToList())
        {
            if (!seen.Add((it.ItemId, it.Hq)))
            {
                Items.Remove(it);
                changed = true;
            }
        }

        return changed;
    }

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
