using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using Consolidator.Game;

namespace Consolidator;

public enum SweepState
{
    Idle,
    Relog,
    WaitLogin,
    ChangeWorld,
    WaitWorld,
    Travel,
    WaitTravel,
    LocateMain,
    Approach,
    OpenTrade,
    WaitTradeOpen,
    EnterGil,
    OfferItems,
    EnterItemQty,
    ConfirmTrade,
    WaitTradeClose,
    ReturnFc,
    WaitFc,
    WalkToDoor,
    EnterHouse,
    Logout,
    NextCharacter,
    Done,
}

public class SweepLogLine
{
    public DateTime At = DateTime.Now;
    public string Character = "";
    public string Message = "";
    public bool IsError;
}

// Drives the whole run, one character at a time, one state per framework tick.
// Every state has a deadline; blowing the deadline logs the reason and skips to the
// next character rather than wedging the queue (unless StopOnError).
public class Sweeper
{
    private readonly Configuration cfg;

    public SweepState State { get; private set; } = SweepState.Idle;
    public bool Running { get; private set; }
    public bool Paused { get; private set; }

    // Seconds left on the current patient-retry backoff, or 0 if we're not waiting.
    public int WaitingSeconds =>
        patientWaitUntil > DateTime.Now
            ? (int)(patientWaitUntil - DateTime.Now).TotalSeconds
            : 0;
    public int Index { get; private set; }
    public List<CharEntry> Queue { get; private set; } = new();
    public List<SweepLogLine> Log { get; } = new();

    private DateTime deadline = DateTime.MaxValue;
    private long gilToSend;          // total still owed on this character
    private int thisTrade;           // gil in the current trade window
    private int tradeFailures;       // consecutive trades where nothing moved
    private const int MaxTradeFailures = 3;

    // Patient-retry state: when we first started waiting on this character, and when
    // the current backoff expires.
    private DateTime patientSince = DateTime.MinValue;
    private DateTime patientWaitUntil = DateTime.MinValue;

    private readonly Random jitterRng = new();
    private DateTime receiverLostAt = DateTime.MinValue;

    // How many times each character has been sent to the back of the queue, so a
    // permanently-unreachable receiver can't cause an endless loop.
    private readonly Dictionary<string, int> requeues = new();

    // Move the current character to the end of the queue. Returns false once they've
    // been requeued too many times, in which case the caller should fail them.
    private bool Requeue()
    {
        var c = Queue[Index];
        var key = c.Full;

        requeues.TryGetValue(key, out var count);
        if (count >= Math.Max(0, cfg.MaxRequeues)) return false;

        requeues[key] = count + 1;

        // Pull them out and put them on the end. Index stays where it is, which now
        // points at whoever was next — so there's no need to advance it.
        Queue.RemoveAt(Index);
        Queue.Add(c);

        AddLog(c.Full,
            $"Receiver stayed busy. Moving to the back of the queue " +
            $"({count + 1} of {cfg.MaxRequeues}) — will retry after the others.",
            true);

        ResetPerCharacter();
        Goto(SweepState.Relog, cfg.LoginTimeout);
        return true;
    }

    private void ResetPerCharacter()
    {
        gilToSend = 0;
        thisTrade = 0;
        charSent = 0;
        moveIssued = false;
        doorMoveIssued = false;
        usingLifestreamPath = false;
        logoutSent = false;
        tradeFailures = 0;
        settleStart = DateTime.MinValue;
        patientSince = DateTime.MinValue;
        patientWaitUntil = DateTime.MinValue;
        receiverLostAt = DateTime.MinValue;
        batch.Clear();
        batchPos = 0;
        itemsBefore = 0;
    }
    private bool multiModeWasOn;
    private string CurrentName => Index >= 0 && Index < Queue.Count
        ? $"{Queue[Index].Name}@{Queue[Index].World}"
        : "";

    public Sweeper(Configuration cfg) => this.cfg = cfg;

    // ---------- control ----------

    public string? Validate()
    {
        if (!PluginIpc.IsAutoRetainerAvailable()) return "AutoRetainer is not installed or loaded.";
        if (!PluginIpc.IsLifestreamAvailable()) return "Lifestream is not installed or loaded.";
        if (!PluginIpc.IsVnavAvailable()) return "vnavmesh is not installed or loaded (needed to walk to the receiver).";
        if (string.IsNullOrWhiteSpace(cfg.MainName) || string.IsNullOrWhiteSpace(cfg.MainWorld))
            return "Receiving character (name + world) is not set.";
        if (cfg.Characters.Count == 0)
            return "No characters in the list. Add or import some on the Characters tab.";
        if (!cfg.Characters.Any(c => c.Enabled)) return "No characters are enabled.";
        if (!cfg.Items.Any(i => i.Enabled))
            return "Nothing is enabled to send — turn on gil and/or add items.";
        return null;
    }

    public void Start()
    {
        var err = Validate();
        if (err != null) { AddLog("", err, true); return; }

        // The Characters tab list order IS the run order — however it's sorted there
        // is how it runs here. Take it as-is rather than re-ordering behind the
        // user's back.
        Queue = cfg.Characters.Where(c => c.Enabled).ToList();
        Index = 0;
        Running = true;
        Paused = false;
        Log.Clear();
        totalSent = 0;
        requeues.Clear();

        if (cfg.CrashProtection)
        {
            cfg.Run.Clear();
            cfg.Run.Active = true;
            cfg.Run.StartedAt = DateTime.Now;
            cfg.Run.Queue = Queue.Select(c => new CharEntry
            {
                Cid = c.Cid, Name = c.Name, World = c.World,
                Enabled = c.Enabled, Source = c.Source,
            }).ToList();
            SaveRun();
        }

        // AR multimode would fight us for control of the character. Park it.
        multiModeWasOn = PluginIpc.GetMultiModeEnabled();
        if (multiModeWasOn) PluginIpc.SetMultiModeEnabled(false);

        AddLog("", $"Starting sweep of {Queue.Count} character(s) -> {cfg.MainFull}."
                   + (cfg.NoOp ? " [DRY RUN — no trades will be confirmed]" : ""));

        // State this outright: if gil is off but gil still moves, the two lines
        // disagreeing tells us immediately whether it's config or code.
        var sending = new List<string>();
        if (GilEnabled) sending.Add($"gil (keeping {GilFloor:N0})");
        var itemNames = cfg.Items.Where(i => i.Enabled && !i.IsGil).Select(i => i.Label).ToList();
        if (itemNames.Count > 0) sending.Add(string.Join(", ", itemNames));

        AddLog("", sending.Count > 0
            ? $"Sending: {string.Join(" + ", sending)}."
            : "Sending: nothing is enabled.");

        // If the item list somehow still holds more than one gil row, say so loudly
        // rather than letting the toggle silently do nothing.
        var gilRows = cfg.Items.Count(i => i.IsGil);
        if (gilRows != 1)
            AddLog("", $"Item list has {gilRows} gil entries — expected exactly 1.", true);

        Goto(SweepState.Relog, cfg.LoginTimeout);
    }

    // True when a previous run was interrupted and can be picked up.
    public bool CanResume =>
        !Running && cfg.CrashProtection && cfg.Run.Active && cfg.Run.Queue.Count > 0
        && cfg.Run.Index < cfg.Run.Queue.Count;

    // Restart from the character we were on. That character is redone from the top:
    // we can't know whether their last trade committed before the crash, and since
    // gil is always re-read rather than tracked, redoing them is harmless — if they
    // already hit the floor, they're simply skipped.
    public void ResumeInterrupted()
    {
        if (!CanResume) return;

        var err = Validate();
        if (err != null) { AddLog("", err, true); return; }

        Queue = cfg.Run.Queue.ToList();
        Index = cfg.Run.Index;
        totalSent = cfg.Run.TotalSent;
        Running = true;
        Paused = false;
        Log.Clear();

        multiModeWasOn = PluginIpc.GetMultiModeEnabled();
        if (multiModeWasOn) PluginIpc.SetMultiModeEnabled(false);

        AddLog("", $"Resuming interrupted run at {Index + 1}/{Queue.Count} " +
                   $"({cfg.Run.Completed.Count} already done, {totalSent:N0} gil moved).");
        AddLog("", $"Redoing {Queue[Index].Full} from the start — their last trade " +
                   "may or may not have gone through.");

        Goto(SweepState.Relog, cfg.LoginTimeout);
    }

    public void DiscardInterrupted()
    {
        try
        {
            cfg.Run.Clear();
            cfg.Save();
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"Couldn't clear run state: {ex.Message}");
        }
    }

    private long totalSent;
    private long charSent;    // gil moved for the character we're on
    private DateTime lastSave = DateTime.MinValue;

    // Gil is modelled as an item with Id 0. Look it up by *identity only* — never by
    // (IsGil && Enabled).
    //
    // That was the bug: the Send tab's checkbox binds to FirstOrDefault(IsGil), i.e.
    // the first gil entry, while the sweeper asked for FirstOrDefault(IsGil &&
    // Enabled). If the list ever held more than one gil row, unticking the first left
    // the sweeper happily finding the second and sending gil anyway. Filtering on
    // Enabled inside the lookup also makes "disabled" and "absent" indistinguishable,
    // which is exactly how that went unnoticed.
    private DepositItem? GilItem =>
        cfg.Items.FirstOrDefault(i => i.IsGil);

    private bool GilEnabled => GilItem?.Enabled == true;

    private long GilFloor => GilItem?.KeepAmount ?? 0;

    // How much gil this character should hand over right now. Zero when gil is off.
    private long SpareGil()
    {
        var item = GilItem;
        if (item == null || !item.Enabled) return 0;
        var spare = TradeEngine.GetCurrentGil() - item.KeepAmount;
        return spare > 0 ? spare : 0;
    }

    // Persist progress. Deliberately NOT called per trade: SavePluginConfig writes
    // through Dalamud's SQLite store, and a rich alt trades every ~3 seconds, which
    // was enough to collide the async writes and throw "database is locked".
    //
    // Character boundaries are the right granularity anyway — resume redoes the
    // whole character from the top, so a finer checkpoint buys nothing.
    //
    // A failed save must never kill the run. Losing a checkpoint costs us a redone
    // character; throwing out of Tick() costs the whole sweep.
    private void SaveRun(bool force = false)
    {
        if (!cfg.CrashProtection) return;

        // Hard floor between writes, whatever the caller does.
        if (!force && (DateTime.Now - lastSave).TotalSeconds < 5) return;

        try
        {
            cfg.Run.Index = Index;
            cfg.Run.TotalSent = totalSent;
            cfg.Run.SavedAt = DateTime.Now;
            cfg.Save();
            lastSave = DateTime.Now;
        }
        catch (Exception ex)
        {
            // Non-fatal by design.
            Plugin.Log.Warning($"Couldn't save run state: {ex.Message}");
        }
    }

    // Must never throw: this is also the handler of last resort when Tick() fails.
    public void Stop(string reason = "Stopped by user.")
    {
        if (!Running) return;
        Running = false;
        Paused = false;
        State = SweepState.Idle;

        // Keep the state so the user can still resume; Start() overwrites it anyway.
        // Forced, since this is our last chance to record where we got to.
        SaveRun(force: true);

        try { PluginIpc.LifestreamAbort(); } catch { }
        try { PluginIpc.VnavStop(); } catch { }
        try { TradeEngine.CancelTrade(); } catch { }
        try { if (multiModeWasOn) PluginIpc.SetMultiModeEnabled(true); } catch { }
        AddLog("", reason);
    }

    // Freeze the run in place. Anything currently moving is halted, and the
    // deadline is suspended so a long pause can't time the current step out.
    public void Pause()
    {
        if (!Running || Paused) return;
        Paused = true;
        pausedAt = DateTime.Now;

        // Stop walking, but leave any open trade window alone — resuming should
        // pick up exactly where it left off.
        PluginIpc.VnavStop();
        PluginIpc.LifestreamAbort();

        AddLog(CurrentName, $"Paused during {State}.");
    }

    public void Resume()
    {
        if (!Running || !Paused) return;

        // Push the deadline out by however long we sat paused.
        var elapsed = DateTime.Now - pausedAt;
        if (deadline != DateTime.MaxValue) deadline += elapsed;
        Paused = false;

        // Whatever movement we cancelled has to be re-issued.
        moveIssued = false;
        doorMoveIssued = false;
        usingLifestreamPath = false;
        settleStart = DateTime.MinValue;

        // Don't sit out a backoff that expired while we were paused.
        patientWaitUntil = DateTime.MinValue;

        AddLog(CurrentName, $"Resumed ({State}).");
    }

    private DateTime pausedAt;

    private void Goto(SweepState s, int timeoutSeconds)
    {
        State = s;
        deadline = DateTime.Now.AddSeconds(timeoutSeconds);
    }

    // Deadline for the trade states. With patient retry on, a single OpenTrade step
    // may legitimately sit there for many minutes waiting for the receiver to free
    // up — so the step timeout has to be at least as long as the patience budget,
    // or the deadline would kill the very wait we asked for.
    private int TradeStepTimeout =>
        cfg.PatientRetry
            ? Math.Max(cfg.TradeTimeout, cfg.PatientRetryMinutes * 60 + 60)
            : cfg.TradeTimeout;

    private void AddLog(string chara, string msg, bool error = false)
    {
        Log.Add(new SweepLogLine { Character = chara, Message = msg, IsError = error });
        if (Log.Count > 500) Log.RemoveAt(0);
        if (error) Plugin.Log.Warning($"[Consolidator] {chara}: {msg}");
        else Plugin.Log.Information($"[Consolidator] {chara}: {msg}");
    }

    // Abandon the current character and move on (or halt if StopOnError).
    private void Fail(string why)
    {
        AddLog(CurrentName, why, true);
        PluginIpc.LifestreamAbort();
        PluginIpc.VnavStop();
        TradeEngine.CancelTrade();
        if (cfg.StopOnError) { Stop("Halted: StopOnError is enabled."); return; }
        Goto(SweepState.NextCharacter, 10);
    }

    // ---------- tick ----------

    public void Tick()
    {
        if (!Running) return;
        if (Paused) return;

        if (DateTime.Now > deadline)
        {
            Fail($"Timed out during {State}.");
            return;
        }

        // Anything that yanks us to a loading screen mid-step is normal during
        // relog/travel; only bail if we're somewhere we shouldn't be.
        switch (State)
        {
            case SweepState.Relog: DoRelog(); break;
            case SweepState.WaitLogin: DoWaitLogin(); break;
            case SweepState.ChangeWorld: DoChangeWorld(); break;
            case SweepState.WaitWorld: DoWaitWorld(); break;
            case SweepState.Travel: DoTravel(); break;
            case SweepState.WaitTravel: DoWaitTravel(); break;
            case SweepState.LocateMain: DoLocateMain(); break;
            case SweepState.Approach: DoApproach(); break;
            case SweepState.OpenTrade: DoOpenTrade(); break;
            case SweepState.WaitTradeOpen: DoWaitTradeOpen(); break;
            case SweepState.EnterGil: DoEnterGil(); break;
            case SweepState.OfferItems: DoOfferItems(); break;
            case SweepState.EnterItemQty: DoEnterItemQty(); break;
            case SweepState.ConfirmTrade: DoConfirmTrade(); break;
            case SweepState.WaitTradeClose: DoWaitTradeClose(); break;
            case SweepState.ReturnFc: DoReturnFc(); break;
            case SweepState.WaitFc: DoWaitFc(); break;
            case SweepState.WalkToDoor: DoWalkToDoor(); break;
            case SweepState.EnterHouse: DoEnterHouse(); break;
            case SweepState.Logout: DoLogout(); break;
            case SweepState.NextCharacter: DoNextCharacter(); break;
        }
    }

    // ---------- states ----------

    private void DoRelog()
    {
        var c = Queue[Index];

        // Skip on AR's cached figure before we even spend a login on them. The live
        // check after login still runs regardless — this is purely an optimisation
        // for characters we already have good reason to believe are empty.
        if (cfg.SkipByCachedGil && ShouldSkipOnCachedGil(c, out var why))
        {
            AddLog(c.Full, why);
            Goto(SweepState.NextCharacter, 10);
            return;
        }

        AddLog(CurrentName, $"({Index + 1}/{Queue.Count}) Logging in.");

        // If we're already on this character, skip the relog but still run the
        // balance check by dropping into WaitLogin, which will pass immediately.
        if (Player.Available && TradeEngine.PlayerName == c.Name)
        {
            Goto(SweepState.WaitLogin, cfg.LoginTimeout);
            return;
        }

        // AR's relog is a chat command, not IPC — this is the path FCTracker proved.
        Svc.Commands.ProcessCommand($"/autoretainer relog {c.Name}@{c.World}");
        Goto(SweepState.WaitLogin, cfg.LoginTimeout);
    }

    // Only skip when the cached figure is both present and fresh enough to trust.
    // A missing or stale figure means we log in and check for real — that's the safe
    // direction to fail in, since wrongly skipping means leaving gil behind.
    private bool ShouldSkipOnCachedGil(CharEntry c, out string reason)
    {
        reason = "";

        // Queue holds copies (for crash recovery), so read the live roster row.
        var live = cfg.Characters.FirstOrDefault(x =>
            (x.Cid != 0 && x.Cid == c.Cid) || x.Full == c.Full) ?? c;

        if (live.LastSeenGilAt == DateTime.MinValue) return false;   // never seen

        if (cfg.CachedGilMaxAgeHours > 0)
        {
            var age = DateTime.Now - live.LastSeenGilAt;
            if (age.TotalHours > cfg.CachedGilMaxAgeHours) return false;   // too stale
        }

        // With gil off, a cached gil figure tells us nothing about whether this
        // character has items worth collecting — so never skip on it.
        if (!GilEnabled) return false;
        if (live.LastSeenGil > GilFloor) return false;                // has spare

        reason = $"Skipped without logging in — last known gil {live.LastSeenGil:N0} " +
                 $"(floor {GilFloor:N0}, seen {AgoText(live.LastSeenGilAt)}).";
        return true;
    }

    private static string AgoText(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes}m ago";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours}h ago";
        return $"{(int)d.TotalDays}d ago";
    }

    private void DoWaitLogin()
    {
        var c = Queue[Index];
        if (!Player.Available) return;                 // still on loading/char select
        if (TradeEngine.PlayerName != c.Name) return;            // not there yet
        if (Svc.Condition[ConditionFlag.BetweenAreas]) return;

        AddLog(CurrentName, "Logged in.");

        // Check the balance before we spend a world transfer and a walk on someone
        // who has nothing to give.
        var have = TradeEngine.GetCurrentGil();
        var spareGil = SpareGil();
        var spareItems = ItemsStillToSend();

        if (spareGil <= 0 && spareItems == 0)
        {
            AddLog(CurrentName, GilEnabled
                ? $"Skipping — has {have:N0} gil (floor {GilFloor:N0}) and nothing to trade."
                : "Skipping — nothing on the send list to trade.");
            Goto(SweepState.NextCharacter, 10);
            return;
        }

        var bits = new List<string>();
        if (spareGil > 0) bits.Add($"{spareGil:N0} gil");
        if (spareItems > 0) bits.Add($"{spareItems:N0} item(s)");
        AddLog(CurrentName, $"Has {have:N0} gil. To send: {string.Join(" + ", bits)}.");
        Goto(SweepState.ChangeWorld, cfg.TravelTimeout);
    }

    private void DoChangeWorld()
    {
        if (PluginIpc.LifestreamIsBusy()) return;

        if (OnTargetWorld())
        {
            AddLog(CurrentName, $"Already on {cfg.TargetWorld}.");
            Goto(SweepState.Travel, cfg.TravelTimeout);
            return;
        }

        AddLog(CurrentName, $"Travelling to {cfg.TargetWorld}.");
        // Same string you'd type after /li — Lifestream resolves worlds by name.
        PluginIpc.LifestreamExecute(cfg.TargetWorld);
        Goto(SweepState.WaitWorld, cfg.TravelTimeout);
    }

    private void DoWaitWorld()
    {
        if (!Settled()) return;

        if (!OnTargetWorld())
        {
            var current = TradeEngine.CurrentWorldName is { Length: > 0 } w ? w : "?";
            Fail($"Failed to reach {cfg.TargetWorld} (currently on {current}).");
            return;
        }

        AddLog(CurrentName, $"Now on {cfg.TargetWorld}.");
        Goto(SweepState.Travel, cfg.TravelTimeout);
    }

    private void DoTravel()
    {
        if (PluginIpc.LifestreamIsBusy()) return;

        AddLog(CurrentName, $"Heading to \"{cfg.Destination}\".");
        // Verbatim /li argument — "ul'dah", "limsa", an aethernet shard name, etc.
        PluginIpc.LifestreamExecute(cfg.Destination);
        Goto(SweepState.WaitTravel, cfg.TravelTimeout);
    }

    private void DoWaitTravel()
    {
        if (!Settled()) return;

        AddLog(CurrentName, "Arrived.");
        gilToSend = 0;
        arrivedAt = DateTime.Now;
        Goto(SweepState.LocateMain, cfg.LocateMainTimeout);
    }

    // True once Lifestream has finished and we're standing still in a real zone.
    private bool Settled()
    {
        if (PluginIpc.LifestreamIsBusy()) return false;
        if (Svc.Condition[ConditionFlag.BetweenAreas]) return false;
        if (Svc.Condition[ConditionFlag.BetweenAreas51]) return false;
        if (!Player.Available) return false;
        return true;
    }

    private bool OnTargetWorld()
    {
        if (!Player.Available) return false;
        return string.Equals(TradeEngine.CurrentWorldName, cfg.TargetWorld, StringComparison.OrdinalIgnoreCase);
    }

    private DateTime arrivedAt;

    private void DoLocateMain()
    {
        // Let the zone finish settling before we even look for her; objects stream
        // in over several frames after a teleport.
        if ((DateTime.Now - arrivedAt).TotalMilliseconds < cfg.ArrivalSettleMs) return;
        if (!Player.Interactable) return;

        var target = TradeEngine.FindPlayer(cfg.MainFull);
        if (target == null) return;   // keep scanning until deadline

        // Work out what we owe, once, on arrival.
        // Zero when gil is disabled, so the trade window never gets a gil amount
        // typed into it.
        gilToSend = SpareGil();

        var items = ItemsStillToSend();

        if (gilToSend <= 0 && items == 0)
        {
            AddLog(CurrentName, "Nothing to send.");
            Goto(SweepState.ReturnFc, cfg.FcReturnTimeout);
            return;
        }

        var sending = new List<string>();
        if (gilToSend > 0) sending.Add($"{gilToSend:N0} gil");
        if (items > 0) sending.Add($"{items:N0} item(s)");
        AddLog(CurrentName, $"Found {cfg.MainFull}. Sending {string.Join(" + ", sending)}.");
        moveIssued = false;
        Goto(SweepState.Approach, cfg.ApproachTimeout);
    }

    private bool moveIssued;

    // Lifestream drops us at the aetheryte, which can be a long way from the
    // receiver. The object table sees her from far outside trade range, so we have
    // to actually walk over before /trade will be accepted. vnavmesh does the pathing.
    private void DoApproach()
    {
        var target = TradeEngine.FindPlayer(cfg.MainFull);
        if (target == null) { Fail("Lost sight of the receiving character."); return; }

        var dist = TradeEngine.DistanceTo(target);

        // Close enough — stop moving and trade.
        if (dist <= TradeEngine.TradeRange)
        {
            if (moveIssued) PluginIpc.VnavStop();
            AddLog(CurrentName, $"In range ({dist:F1}y).");
            Goto(SweepState.OpenTrade, TradeStepTimeout);
            return;
        }

        if (!PluginIpc.IsVnavAvailable())
        {
            Fail($"{dist:F1}y away and vnavmesh isn't loaded, so I can't walk over. " +
                 "Install/enable vnavmesh, or park the receiver next to the aetheryte.");
            return;
        }

        // Fire the move once, then just wait for it to finish.
        if (!moveIssued)
        {
            if (!PluginIpc.VnavIsReady())
                return;   // mesh still building; the deadline will catch a hang

            AddLog(CurrentName, $"Walking {dist:F1}y to {cfg.MainFull}.");
            PluginIpc.VnavMoveTo(target.Position);
            moveIssued = true;
            return;
        }

        // Re-issue if vnav finished or gave up but we're still out of range
        // (she may have moved, or the path was partial).
        if (!PluginIpc.VnavIsRunning() && !PluginIpc.VnavPathfindInProgress())
        {
            if (EzThrottle("Cons.Reissue", 2000))
            {
                AddLog(CurrentName, $"Still {dist:F1}y away; re-pathing.");
                PluginIpc.VnavMoveTo(target.Position);
            }
        }
    }

    // Small local throttle so re-pathing doesn't spam every frame.
    private readonly Dictionary<string, DateTime> throttles = new();
    private bool EzThrottle(string key, int ms)
    {
        var now = DateTime.Now;
        if (throttles.TryGetValue(key, out var next) && now < next) return false;
        throttles[key] = now.AddMilliseconds(ms);
        return true;
    }

    private void DoOpenTrade()
    {
        // Backing off after a failed attempt — the receiver is most likely mid-trade
        // with another account.
        if (patientWaitUntil > DateTime.Now) return;

        // Patience is for a receiver who's *busy*, not one who isn't there. If they've
        // vanished from the object table entirely (logged out, changed zone, moved
        // instance), waiting fifteen minutes just wastes fifteen minutes — the trade
        // is never going to land. Bail out early so they get requeued or skipped now.
        if (cfg.PatientRetry && patientSince != DateTime.MinValue)
        {
            if (TradeEngine.FindPlayer(cfg.MainFull) == null)
            {
                if ((DateTime.Now - receiverLostAt).TotalSeconds > 45
                    && receiverLostAt != DateTime.MinValue)
                {
                    if (cfg.RequeueOnBusy && Requeue()) return;
                    Fail($"{cfg.MainFull} is no longer nearby — they may have logged out " +
                         "or changed instance. Not worth waiting out the retry budget.");
                    return;
                }
                if (receiverLostAt == DateTime.MinValue) receiverLostAt = DateTime.Now;
            }
            else
            {
                receiverLostAt = DateTime.MinValue;
            }
        }

        // Re-plan what's left every time we open a window: gil and item counts are
        // always read from the game, never tracked in a counter, so a failed trade
        // self-corrects instead of losing track.
        // gilToSend is already 0 when gil is disabled, but be explicit.
        thisTrade = GilEnabled
            ? (int)Math.Min(Math.Max(gilToSend, 0), TradeEngine.MaxGilPerTrade)
            : 0;

        batch.Clear();
        batchPos = 0;

        if (TradeEngine.ItemsAvailable)
        {
            foreach (var item in cfg.Items.Where(i => i.Enabled && !i.IsGil))
            {
                if (batch.Count >= TradeEngine.MaxItemsPerTrade) break;

                foreach (var slot in Inventory.PlanFor(item, cfg.IncludeArmoury))
                {
                    if (batch.Count >= TradeEngine.MaxItemsPerTrade) break;
                    batch.Add(slot);
                }
            }
        }

        // Nothing left to hand over.
        if (thisTrade <= 0 && batch.Count == 0)
        {
            AddLog(CurrentName, "Nothing left to send.");
            Goto(SweepState.ReturnFc, cfg.FcReturnTimeout);
            return;
        }

        if (TradeEngine.IsTradeOpen)
        {
            itemsBefore = ItemsStillToSend();
            settleStart = DateTime.MinValue;
            Goto(SweepState.EnterGil, cfg.TradeTimeout);
            return;
        }

        var target = TradeEngine.FindPlayer(cfg.MainFull);
        if (target == null) { settleStart = DateTime.MinValue; return; }

        // The receiver has to be *continuously* ready for a moment before we fire.
        // Checking once isn't enough: right after a zone-in she can report ready on
        // one frame and still have the server reject the request. A plain fixed
        // delay wouldn't help either — it would just fire blind at a later frame.
        if (!TradeEngine.ReadyToTrade(target))
        {
            settleStart = DateTime.MinValue;
            return;
        }

        if (settleStart == DateTime.MinValue)
        {
            settleStart = DateTime.Now;
            return;
        }

        if ((DateTime.Now - settleStart).TotalMilliseconds < cfg.TradeSettleMs) return;

        itemsBefore = ItemsStillToSend();

        if (TradeEngine.UseTradeOn(cfg.MainFull))
        {
            var what = thisTrade > 0 ? $"{thisTrade:N0} gil" : "";
            if (batch.Count > 0)
                what += (what.Length > 0 ? " + " : "") + $"{batch.Count} item stack(s)";
            AddLog(CurrentName, $"Opening trade for {what}.");
            Goto(SweepState.WaitTradeOpen, cfg.TradeTimeout);
        }
    }

    // When the receiver first became continuously ready. Reset whenever she stops
    // being ready, so a flicker restarts the countdown.
    private DateTime settleStart = DateTime.MinValue;

    private void DoWaitTradeOpen()
    {
        if (!TradeEngine.IsTradeOpen) return;
        Goto(SweepState.EnterGil, cfg.TradeTimeout);
    }

    private void DoEnterGil()
    {
        if (!TradeEngine.IsTradeOpen) { Fail("Trade window closed unexpectedly."); return; }

        // Hard guard. thisTrade should already be 0 when gil is off, but this is the
        // last point before we'd actually type a number into the trade window, and
        // sending gil the user explicitly disabled is not a recoverable mistake.
        if (!GilEnabled || thisTrade <= 0)
        {
            Goto(SweepState.OfferItems, cfg.TradeTimeout);
            return;
        }

        // The gil field only accepts input once the InputNumeric popup is up, so
        // we fire OpenGilInput until it appears, then type into it.
        if (TradeEngine.IsGilInputOpen)
        {
            if (TradeEngine.SetNumericInput(thisTrade))
            {
                AddLog(CurrentName, $"Entered {thisTrade:N0} gil.");
                Goto(SweepState.OfferItems, cfg.TradeTimeout);
            }
            return;
        }

        TradeEngine.OpenGilInput();
    }

    // Items queued for the current trade window (up to 5).
    private readonly List<TradeSlot> batch = new();
    private int batchPos;

    private void DoOfferItems()
    {
        if (!TradeEngine.IsTradeOpen) { Fail("Trade window closed unexpectedly."); return; }

        if (batchPos >= batch.Count)
        {
            Goto(SweepState.ConfirmTrade, cfg.TradeTimeout);
            return;
        }

        var slot = batch[batchPos];

        if (TradeEngine.OfferItem(slot))
        {
            // A partial stack pops a quantity prompt; a whole stack goes straight in.
            if (TradeEngine.IsWholeStack(slot))
            {
                AddLog(CurrentName, $"Added {slot.Take:N0}x {slot.Name}.");
                batchPos++;
            }
            else
            {
                Goto(SweepState.EnterItemQty, cfg.TradeTimeout);
            }
        }
    }

    private void DoEnterItemQty()
    {
        if (!TradeEngine.IsTradeOpen) { Fail("Trade window closed unexpectedly."); return; }

        var slot = batch[batchPos];

        if (TradeEngine.IsGilInputOpen)   // same InputNumeric addon as gil uses
        {
            if (TradeEngine.SetNumericInput((int)slot.Take))
            {
                AddLog(CurrentName, $"Added {slot.Take:N0}x {slot.Name} (of {slot.InStack:N0}).");
                batchPos++;
                Goto(SweepState.OfferItems, cfg.TradeTimeout);
            }
        }
    }

    private void DoConfirmTrade()
    {
        if (!TradeEngine.IsTradeOpen)
        {
            // Other side may have already closed it out.
            Goto(SweepState.WaitTradeClose, cfg.TradeTimeout);
            return;
        }

        if (TradeEngine.ConfirmTrade(cfg.NoOp))
            Goto(SweepState.WaitTradeClose, cfg.TradeTimeout);
    }

    private void DoWaitTradeClose()
    {
        if (TradeEngine.IsTradeOpen) return;

        if (cfg.NoOp)
        {
            AddLog(CurrentName, "[DRY RUN] Trade not actually sent.");
            gilToSend = 0;
            Goto(SweepState.ReturnFc, cfg.FcReturnTimeout);
            return;
        }

        // Give the server a beat to settle before we read anything back.
        if (!EzThrottle("Cons.PostTrade", 1500)) return;

        // Never assume the trade went through — a cancelled trade closes the window
        // exactly like a completed one does. Re-read gil and item counts from the
        // game and recompute what's left. If the trade failed, nothing has changed
        // and we simply try again.
        var have = TradeEngine.GetCurrentGil();
        var remaining = SpareGil();
        var itemsLeft = ItemsStillToSend();

        if (remaining <= 0 && itemsLeft == 0)
        {
            AddLog(CurrentName, GilEnabled
                ? $"Done — {have:N0} gil left (floor {GilFloor:N0})."
                : "Done.");
            gilToSend = 0;
            Goto(SweepState.ReturnFc, cfg.FcReturnTimeout);
            return;
        }

        // Did anything actually move? Compare against what we had going in.
        var gilMoved = gilToSend - remaining;
        var itemsMoved = itemsBefore - itemsLeft;

        if (gilMoved <= 0 && itemsMoved <= 0)
        {
            tradeFailures++;

            if (cfg.PatientRetry)
            {
                // Start the clock on the first failure for this character.
                if (patientSince == DateTime.MinValue)
                    patientSince = DateTime.Now;

                var waited = DateTime.Now - patientSince;
                var budget = TimeSpan.FromMinutes(Math.Max(1, cfg.PatientRetryMinutes));

                if (waited >= budget)
                {
                    // The receiver being busy is transient, so don't write this
                    // character off — put them at the back of the queue and come
                    // back once everyone else is done. By then the main is very
                    // likely free.
                    if (cfg.RequeueOnBusy && Requeue())
                        return;

                    Fail($"Trade still not going through after {waited.TotalMinutes:F0} minutes " +
                         $"({tradeFailures} attempts). Giving up on this character.");
                    return;
                }

                // Back off between attempts — the receiver is most likely mid-trade
                // with another account, and hammering them doesn't help. Jittered so
                // that two accounts retrying on the same cadence don't stay locked in
                // step with each other, colliding forever.
                var baseDelay = Math.Max(1, cfg.PatientRetryDelaySeconds);
                var jitter = jitterRng.Next(0, Math.Max(1, baseDelay / 2));
                patientWaitUntil = DateTime.Now.AddSeconds(baseDelay + jitter);

                var left = budget - waited;
                AddLog(CurrentName,
                    $"Trade didn't go through (attempt {tradeFailures}) — the receiver is " +
                    $"probably busy. Retrying in {baseDelay + jitter}s. " +
                    $"{left.TotalMinutes:F0}m left before giving up.");
            }
            else
            {
                if (tradeFailures >= MaxTradeFailures)
                {
                    Fail($"Trade failed {tradeFailures}x in a row (nothing moved). " +
                         "Is the receiving character busy, full, or declining? " +
                         "Turn on \"Keep retrying while the receiver is busy\" if you're " +
                         "running several accounts into the same character.");
                    return;
                }

                AddLog(CurrentName,
                    $"Trade didn't go through. Retrying {tradeFailures}/{MaxTradeFailures}.",
                    true);
            }
        }
        else
        {
            totalSent += gilMoved > 0 ? gilMoved : 0;
            charSent += gilMoved > 0 ? gilMoved : 0;
            tradeFailures = 0;

            // Progress resets patience — the receiver clearly isn't stuck.
            patientSince = DateTime.MinValue;
            patientWaitUntil = DateTime.MinValue;

            var parts = new List<string>();
            if (gilMoved > 0) parts.Add($"{gilMoved:N0} gil");
            if (itemsMoved > 0) parts.Add($"{itemsMoved:N0} item(s)");
            var left = new List<string>();
            if (remaining > 0) left.Add($"{remaining:N0} gil");
            if (itemsLeft > 0) left.Add($"{itemsLeft:N0} item(s)");

            AddLog(CurrentName, $"Sent {string.Join(" + ", parts)}."
                + (left.Count > 0 ? $" {string.Join(" + ", left)} still to go." : ""));
        }

        gilToSend = remaining;
        settleStart = DateTime.MinValue;
        Goto(SweepState.OpenTrade, TradeStepTimeout);
    }

    // How many individual units of the configured items are still above their floor.
    private long ItemsStillToSend()
    {
        if (!TradeEngine.ItemsAvailable) return 0;

        long total = 0;
        foreach (var item in cfg.Items.Where(i => i.Enabled && !i.IsGil))
        {
            var have = Inventory.CountOf(item.ItemId, item.Hq, cfg.IncludeArmoury);
            var spare = have - item.KeepAmount;
            if (spare > 0) total += spare;
        }
        return total;
    }

    private long itemsBefore;

    private void DoReturnFc()
    {
        if (!cfg.ReturnToFc)
        {
            Goto(SweepState.Logout, 60);
            return;
        }

        if (PluginIpc.LifestreamIsBusy()) return;

        // HouseEnterMode: None=0, Walk_to_door=1, Enter_house=2, Enter_workshop=3.
        // If the house has a recorded PathToEntrance, Lifestream will walk us in
        // itself; if not it stops at the plot and we handle the door ourselves.
        int? mode = cfg.EnterHouse ? 2 : 0;

        AddLog(CurrentName, cfg.EnterHouse
            ? "Returning to FC and heading inside."
            : "Returning to FC.");

        PluginIpc.LifestreamGoToFc(mode);
        fcIssuedAt = DateTime.Now;
        Goto(SweepState.WaitFc, cfg.FcReturnTimeout);
    }

    private DateTime fcIssuedAt;

    private void DoWaitFc()
    {
        // EnqueuePropertyShortcut only *queues* the task, so Lifestream isn't busy
        // yet on the frame right after we call it.
        if ((DateTime.Now - fcIssuedAt).TotalSeconds < 3) return;

        if (PluginIpc.LifestreamIsBusy()) return;
        if (Svc.Condition[ConditionFlag.BetweenAreas]) return;
        if (!Player.Available || !Player.Interactable) return;

        // Already inside (Lifestream managed it, or we were there) — done.
        if (TradeEngine.InHousingInterior)
        {
            AddLog(CurrentName, "Inside the FC house.");
            logoutSent = false;
            Goto(SweepState.Logout, 60);
            return;
        }

        if (!cfg.EnterHouse)
        {
            AddLog(CurrentName, "At the FC.");
            logoutSent = false;
            Goto(SweepState.Logout, 60);
            return;
        }

        // Lifestream only walks you inside if that house has a recorded
        // PathToEntrance in its own config, which is empty unless you've manually
        // registered it. Rather than make you record a path for every FC, we walk
        // to the door ourselves with vnavmesh.
        AddLog(CurrentName, "At the plot; heading for the door.");
        doorMoveIssued = false;
        Goto(SweepState.WalkToDoor, cfg.EnterHouseTimeout);
    }

    private bool doorMoveIssued;
    private bool usingLifestreamPath;

    private void DoWalkToDoor()
    {
        if (TradeEngine.InHousingInterior)
        {
            Goto(SweepState.EnterHouse, cfg.EnterHouseTimeout);
            return;
        }

        // Lifestream is the better mover when the house has a recorded
        // PathToEntrance — it's the exact route you walked when you registered it.
        // There's no IPC to *write* that path (only its own UI does), so when one
        // hasn't been recorded we path to the door with vnavmesh instead.
        if (!doorMoveIssued)
        {
            var cid = Queue[Index].Cid;
            var path = cid != 0
                ? PluginIpc.LifestreamGetFcEntrancePath(cid)
                : new System.Collections.Generic.List<System.Numerics.Vector3>();

            if (path.Count > 0)
            {
                AddLog(CurrentName, $"Following Lifestream's recorded path to the door ({path.Count} points).");
                PluginIpc.LifestreamMove(path);
                usingLifestreamPath = true;
                doorMoveIssued = true;
                return;
            }
        }

        // Lifestream is walking its path — just wait for it.
        if (usingLifestreamPath)
        {
            if (PluginIpc.LifestreamIsBusy()) return;

            var d = TradeEngine.FindEntrance(out var dd);
            if (d != null && dd <= InteractRange)
            {
                AddLog(CurrentName, $"At the door ({dd:F1}y).");
                Goto(SweepState.EnterHouse, cfg.EnterHouseTimeout);
                return;
            }

            // Path finished but we're not at the door — fall through to vnav.
            AddLog(CurrentName, "Recorded path didn't reach the door; using vnavmesh.");
            usingLifestreamPath = false;
            doorMoveIssued = false;
            return;
        }

        var door = TradeEngine.FindEntrance(out var dist);
        if (door == null)
        {
            // The entrance object may still be streaming in after the teleport.
            return;
        }

        // Interact range is short; get close before trying.
        if (dist <= InteractRange)
        {
            if (doorMoveIssued) PluginIpc.VnavStop();
            AddLog(CurrentName, $"At the door ({dist:F1}y).");
            Goto(SweepState.EnterHouse, cfg.EnterHouseTimeout);
            return;
        }

        if (!PluginIpc.IsVnavAvailable())
        {
            Fail($"Door is {dist:F1}y away and vnavmesh isn't available to walk there.");
            return;
        }

        if (!doorMoveIssued)
        {
            if (!PluginIpc.VnavIsReady()) return;
            AddLog(CurrentName, $"Walking {dist:F1}y to the door.");
            PluginIpc.VnavMoveTo(door.Position);
            doorMoveIssued = true;
            return;
        }

        // Re-path if vnav stopped short.
        if (!PluginIpc.VnavIsRunning() && !PluginIpc.VnavPathfindInProgress())
        {
            if (EzThrottle("Cons.DoorRepath", 2000))
                PluginIpc.VnavMoveTo(door.Position);
        }
    }

    private const float InteractRange = 4.0f;

    private void DoEnterHouse()
    {
        // Entering is a zone change; once we're in, we're done.
        if (TradeEngine.InHousingInterior)
        {
            AddLog(CurrentName, "Inside the FC house.");
            logoutSent = false;
            Goto(SweepState.Logout, 60);
            return;
        }

        if (Svc.Condition[ConditionFlag.BetweenAreas]) return;
        if (!Player.Interactable) return;

        // "Enter the estate hall?" confirmation.
        if (TradeEngine.ConfirmEntranceYesno()) return;

        var door = TradeEngine.FindEntrance(out var dist);
        if (door == null) return;

        if (dist > InteractRange)
        {
            // Drifted out of range — walk back.
            doorMoveIssued = false;
            Goto(SweepState.WalkToDoor, cfg.EnterHouseTimeout);
            return;
        }

        TradeEngine.InteractWithEntrance(door);
    }

    private bool logoutSent;

    // We're inside the house now. For every character except the last, AR's relog
    // performs the logout itself as part of switching, so there's nothing to do —
    // the character logs out from right here, which is the point.
    //
    // The last character has no relog to ride on, so if we want them logged out
    // inside the house we have to send it ourselves.
    private void DoLogout()
    {
        var isLast = Index >= Queue.Count - 1;

        if (!cfg.LogoutAtEnd || !isLast)
        {
            Goto(SweepState.NextCharacter, 10);
            return;
        }

        if (!logoutSent)
        {
            AddLog(CurrentName, "Logging out.");
            // /logout opens a SelectYesno; YesAlready (which you have loaded) or
            // the confirm below handles it.
            Svc.Commands.ProcessCommand("/logout");
            logoutSent = true;
            return;
        }

        // Confirm the logout dialog if it's sitting there unanswered.
        if (TradeEngine.ConfirmYesno()) return;

        // Once we're off, we're done.
        if (!Player.Available)
            Goto(SweepState.NextCharacter, 10);
    }

    private void DoNextCharacter()
    {
        // Mark the character we're leaving as done before advancing, so a crash on
        // the *next* one doesn't redo this one.
        if (Index >= 0 && Index < Queue.Count)
        {
            var done = Queue[Index];

            // Stamp the roster row (Queue holds copies for crash recovery, so write
            // through to the live entry).
            var live = cfg.Characters.FirstOrDefault(x =>
                (x.Cid != 0 && x.Cid == done.Cid) || x.Full == done.Full);
            if (live != null)
            {
                live.LastRunAt = DateTime.Now;
                live.LastRunSent = charSent;
                live.LastSeenGil = TradeEngine.GetCurrentGil();
                live.LastSeenGilAt = DateTime.Now;
            }

            if (cfg.CrashProtection)
            {
                if (done.Cid != 0 && !cfg.Run.Completed.Contains(done.Cid))
                    cfg.Run.Completed.Add(done.Cid);
                if (!cfg.Run.CompletedNames.Contains(done.Full))
                    cfg.Run.CompletedNames.Add(done.Full);
            }
        }
        charSent = 0;

        Index++;
        gilToSend = 0;
        thisTrade = 0;
        moveIssued = false;
        doorMoveIssued = false;
        usingLifestreamPath = false;
        logoutSent = false;
        batch.Clear();
        batchPos = 0;
        itemsBefore = 0;
        tradeFailures = 0;
        settleStart = DateTime.MinValue;
        patientSince = DateTime.MinValue;
        patientWaitUntil = DateTime.MinValue;

        if (Index >= Queue.Count)
        {
            Running = false;
            State = SweepState.Done;
            if (multiModeWasOn) PluginIpc.SetMultiModeEnabled(true);

            // Finished cleanly, so there's nothing to recover.
            if (cfg.CrashProtection)
            {
                try { cfg.Run.Clear(); cfg.Save(); }
                catch (Exception ex) { Plugin.Log.Warning($"Couldn't clear run state: {ex.Message}"); }
            }

            var summary = $"Sweep complete. {totalSent:N0} gil moved to {cfg.MainFull}.";
            if (requeues.Count > 0)
                summary += $" {requeues.Count} character(s) had to be retried " +
                           "because the receiver was busy.";
            AddLog("", summary);
            return;
        }

        // Checkpoint: if the game dies now, we come back to this character.
        SaveRun(force: true);

        Goto(SweepState.Relog, cfg.LoginTimeout);
    }
}
