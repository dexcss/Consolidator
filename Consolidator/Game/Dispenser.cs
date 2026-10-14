using System;
using System.Collections.Generic;
using System.Linq;
using ECommons.DalamudServices;
using ECommons.GameHelpers;

namespace Consolidator.Game;

public enum DispenseState
{
    Idle,
    Watch,        // sitting there, waiting for someone to open a trade
    WaitOpen,     // request accepted, waiting for the window
    Identify,     // work out who this is and what they're owed
    Offer,        // put the stacks in
    Qty,          // answer the quantity prompt
    Confirm,      // lock + "Trade these items?"
    Verify,       // re-read stock, credit what actually left
    Followup,     // more owed than one window holds — open another
}

// The giving half of the operation, and the mirror image of Sweeper.
//
// Sweeper runs on an alt and walks it to the main. Dispenser runs on the MAIN and
// never moves: it waits for whitelisted alts to come to it, and puts a fixed handout
// into the trade window they opened. That split isn't a design preference, it's
// forced — only the client logged in as the giving character can place that
// character's items into a trade.
//
// Because the alt's gil and the main's handout share one window, a sweep and a
// dispense settle together in a single trade. That is what "do both at once" means
// here, and it's why Dropbox has to be off on this character: Dropbox locks the
// trade as soon as it opens, which is before we've had a chance to put anything in.
public class Dispenser
{
    private readonly Configuration cfg;
    private readonly Sweeper sweeper;

    public DispenseState State { get; private set; } = DispenseState.Idle;
    public bool Running { get; private set; }
    public List<SweepLogLine> Log { get; } = new();

    // Who the current trade window is with. "" when we don't know, which is a hard
    // stop on dispensing rather than a reason to guess.
    public string Partner { get; private set; } = "";

    // Out of stock. Not a halt of the machine: trades are still accepted and
    // confirmed while this is set, because with Dropbox off nothing else is
    // receiving the alts' gil. Only the handing-out stops.
    public bool NeedsRefill { get; private set; }
    public string RefillReason { get; private set; } = "";

    // What each character has actually been handed, keyed by character, item and
    // quality. Measured from inventory after every window, never predicted — same
    // rule the gil side runs on, and for the same reason: a cancelled or refused
    // trade then corrects itself instead of being recorded as delivered.
    private readonly Dictionary<(string Full, uint ItemId, bool Hq), long> given = new();

    // Characters we've stopped trying to serve this session (three windows in a row
    // where nothing moved), so one stuck character can't hold the queue forever.
    private readonly HashSet<string> abandoned = new();

    private readonly Dictionary<(uint ItemId, bool Hq), long> before = new();
    private readonly List<TradeSlot> plan = new();
    private int planPos;

    // Consecutive windows that closed without moving anything, per character. Kept
    // out of the per-window reset on purpose: the whole point is to count across
    // windows, so that a character whose bags are full is eventually left alone.
    private readonly Dictionary<string, int> strikes = new();

    private DateTime deadline = DateTime.MaxValue;
    private DateTime followupUntil = DateTime.MinValue;
    private DateTime windowOpenedAt = DateTime.MinValue;

    // A freshly opened Trade window has no text in its nodes for the first few
    // frames, so "I can't see a name" has to mean "not yet" before it means "never".
    private const double IdentifyGraceSeconds = 3.0;

    // A window we've decided not to touch (partner unknown, or not on the list).
    // Held until the window closes so we don't re-evaluate it every frame.
    private bool ignoringWindow;
    private bool warnedUnknown;

    // When each character was last turned away, so a waiting alt retrying every
    // few seconds doesn't fill the log with the same line.
    private readonly Dictionary<string, DateTime> declinedAt = new();

    // Set when the user stops the dispenser by hand, so auto-start doesn't
    // immediately turn it back on. Cleared on the next character change.
    private bool manuallyStopped;

    public Dispenser(Configuration cfg, Sweeper sweeper)
    {
        this.cfg = cfg;
        this.sweeper = sweeper;
    }

    // ---------- identity ----------

    // Are we the character that's supposed to be handing things out?
    //
    // Name AND world, never name alone: this roster has the same name on more than
    // one world, and arming the dispenser on the wrong one would hand the stock to
    // whoever happened to walk up.
    public bool OnDispensingCharacter
    {
        get
        {
            if (!Player.Available) return false;
            if (string.IsNullOrWhiteSpace(cfg.MainName)) return false;
            if (!string.Equals(TradeEngine.PlayerName, cfg.MainName, StringComparison.Ordinal))
                return false;
            if (string.IsNullOrWhiteSpace(cfg.MainWorld)) return true;
            return string.Equals(TradeEngine.HomeWorldName, cfg.MainWorld,
                                 StringComparison.OrdinalIgnoreCase);
        }
    }

    private IEnumerable<DispenseItem> Handouts =>
        cfg.DispenseItems.Where(i => i.Enabled && i.ItemId != 0 && i.Amount > 0);

    public bool HasWork => Handouts.Any() && cfg.Characters.Any(c => c.Enabled);

    private HashSet<string> Whitelist => new(
        cfg.Characters.Where(c => c.Enabled).Select(c => c.Full),
        StringComparer.OrdinalIgnoreCase);

    public long GivenTo(string full, DispenseItem it)
        => given.TryGetValue((full, it.ItemId, it.Hq), out var v) ? v : 0;

    public long OwedTo(string full)
        => abandoned.Contains(full)
            ? 0
            : Handouts.Sum(i => Math.Max(0, i.Amount - GivenTo(full, i)));

    // What's actually spendable right now: what's in the bags, less the reserve.
    public long AvailableOf(DispenseItem it)
        => Math.Max(0, Inventory.CountOf(it.ItemId, it.Hq, false) - Math.Max(0, it.Reserve));

    // The first handout we can't cover, described for the log and the banner, or ""
    // if everything `want` asks for is in stock. Scans the bags, so callers throttle.
    private string Shortfall(Func<DispenseItem, long> want)
    {
        foreach (var it in Handouts)
        {
            var need = want(it);
            if (need <= 0) continue;

            var avail = AvailableOf(it);
            if (avail < need)
                return $"{it.Label} — {avail:N0} spare in the bags, {need:N0} needed";
        }
        return "";
    }

    // Enough for one more character to be served in full?
    public string ShortfallForNext() => Shortfall(i => i.Amount);

    // Out of stock AND set to turn people away rather than use up their visit.
    public bool TurningTradesAway => NeedsRefill && cfg.DispenseDeclineWhenOut;

    private void RaiseRefill(string why)
    {
        RefillReason = why;
        if (NeedsRefill) return;

        NeedsRefill = true;
        AddLog("", cfg.DispenseDeclineWhenOut
            ? $"OUT OF STOCK — {why}. Turning trades away so nobody is used up."
            : $"OUT OF STOCK — {why}. Still taking trades; handing nothing out.", true);

        // The window may well not be open, and this is the whole point of the
        // feature — say it where it'll actually be seen.
        try { Svc.Chat.PrintError($"[Consolidator] CHARACTER OUT, REFILL — {why}"); }
        catch { /* chat unavailable */ }
    }

    // Re-check and carry on. Returns "" when it cleared, or why it didn't.
    public string ClearRefill()
    {
        var why = ShortfallForNext();
        if (why.Length > 0)
        {
            RefillReason = why;
            return $"Still short — {why}.";
        }

        if (NeedsRefill)
        {
            NeedsRefill = false;
            RefillReason = "";
            AddLog("", "Refilled — picking up where it left off.");
        }
        return "";
    }

    // Characters still due something — the only people whose trade requests we
    // answer unprompted.
    private List<string> PendingTargets()
        => cfg.Characters.Where(c => c.Enabled)
                         .Select(c => c.Full)
                         .Where(f => OwedTo(f) > 0)
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .ToList();

    public int ServedCount =>
        cfg.Characters.Where(c => c.Enabled)
           .Count(c => Handouts.Any() && OwedTo(c.Full) == 0 && !abandoned.Contains(c.Full));

    // ---------- control ----------

    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(cfg.MainName))
            return "Set the receiving character on the Settings tab first — that's who dispenses.";
        if (!TradeEngine.ItemsAvailable)
            return "Item trading is unavailable (the OfferItemTrade signature didn't resolve).";
        if (!cfg.Characters.Any(c => c.Enabled))
            return "No characters are enabled on the Characters tab — that list is the whitelist.";
        if (!Handouts.Any())
            return "Nothing to hand out. Add an item below and give it an amount.";
        return null;
    }

    public void Start()
    {
        var err = Validate();
        if (err != null) { AddLog("", err, true); return; }

        Running = true;
        manuallyStopped = false;
        NeedsRefill = false;
        RefillReason = "";
        ResetWindow();

        var what = string.Join(", ", Handouts.Select(i => $"{i.Amount:N0}x {i.Label}"));
        AddLog("", $"Dispensing {what} to each of {cfg.Characters.Count(c => c.Enabled)} " +
                   "whitelisted character(s)."
                   + (cfg.NoOp ? " [DRY RUN — no trades will be confirmed]" : ""));

        if (!cfg.DispenseAcceptRequests)
            AddLog("", "\"Accept trade requests\" is off — you'll have to click Accept yourself.", true);

        AddLog("", "Dropbox must be OFF on this character, or it will lock the trade "
                   + "before the handout is in it.");

        Goto(DispenseState.Watch);
    }

    // `manual` separates "the user pressed Stop" from "we stood down on our own".
    // Only the former should keep auto-start from arming again — otherwise standing
    // down for a sweep would quietly disable the dispenser for the rest of the
    // session.
    public void Stop(string reason = "Dispenser stopped.", bool manual = true)
    {
        if (!Running) return;
        Running = false;
        if (manual) manuallyStopped = true;
        State = DispenseState.Idle;

        try { TradeEngine.CancelTrade(); } catch { }
        ResetWindow();
        AddLog("", reason);
    }

    // Forget who's had what, without touching the settings.
    public void ResetLedger()
    {
        given.Clear();
        abandoned.Clear();
        strikes.Clear();
        declinedAt.Clear();
        AddLog("", "Ledger cleared — everyone is owed their handout again.");
    }

    private void ResetWindow()
    {
        Partner = "";
        plan.Clear();
        planPos = 0;
        before.Clear();
        ignoringWindow = false;
        warnedUnknown = false;
        followupUntil = DateTime.MinValue;
        windowOpenedAt = DateTime.MinValue;
    }

    private void Goto(DispenseState s, int timeoutSeconds = 0)
    {
        State = s;
        deadline = timeoutSeconds > 0
            ? DateTime.Now.AddSeconds(timeoutSeconds)
            : DateTime.MaxValue;
    }

    // ---------- tick ----------

    public void Tick()
    {
        // Never both at once on one client. A sweep drives this character somewhere
        // else entirely; the two would fight over the same trade window.
        if (sweeper.Running)
        {
            if (Running)
                Stop("Stood down — a sweep is running on this client.", manual: false);
            return;
        }

        if (!OnDispensingCharacter)
        {
            // A manual stop shouldn't outlive the character it was made on.
            manuallyStopped = false;

            if (!Running) return;

            if (Player.Available)
            {
                // Genuinely a different character.
                Stop($"Stopped dispensing — this isn't {cfg.MainFull}.", manual: false);
                return;
            }

            // Character select or a loading screen. Stay armed for when we're back,
            // but drop any half-finished window — it won't exist on the other side.
            if (State != DispenseState.Watch)
            {
                ResetWindow();
                Goto(DispenseState.Watch);
            }
            return;
        }

        if (!Running)
        {
            if (cfg.DispenseAutoStart && !manuallyStopped && HasWork && Validate() == null)
                Start();
            return;
        }

        // A window we decided to leave alone stays ignored until it closes, so the
        // decision isn't re-litigated (and re-logged) every frame.
        if (ignoringWindow && !TradeEngine.IsTradeOpen)
        {
            ignoringWindow = false;
            warnedUnknown = false;
        }

        if (State != DispenseState.Watch && DateTime.Now > deadline)
        {
            OnTimeout();
            return;
        }

        switch (State)
        {
            case DispenseState.Watch: DoWatch(); break;
            case DispenseState.WaitOpen: DoWaitOpen(); break;
            case DispenseState.Identify: DoIdentify(); break;
            case DispenseState.Offer: DoOffer(); break;
            case DispenseState.Qty: DoQty(); break;
            case DispenseState.Confirm: DoConfirm(); break;
            case DispenseState.Verify: DoVerify(); break;
            case DispenseState.Followup: DoFollowup(); break;
        }
    }

    private void OnTimeout()
    {
        AddLog(Partner, $"Timed out during {State}.", true);

        var wasOpen = TradeEngine.IsTradeOpen;
        if (wasOpen) TradeEngine.CancelTrade();

        ResetWindow();

        // After the reset, not before — ResetWindow clears this flag, and the
        // cancelled window takes a few frames to actually close. Without the flag
        // Watch would pick it straight back up and start offering into a trade we
        // just backed out of.
        ignoringWindow = wasOpen;

        Goto(DispenseState.Watch);
    }

    // ---------- states ----------

    private void DoWatch()
    {
        if (TradeEngine.IsTradeOpen)
        {
            if (!ignoringWindow) BeginWindow();
            return;
        }

        // Stock check. Throttled because it walks the bags, and done here rather
        // than only when someone turns up so the banner is already showing by the
        // time it matters.
        if (cfg.DispenseStopWhenOut)
        {
            if (Throttle("Cons.DispStock", 1000))
            {
                var why = ShortfallForNext();
                if (why.Length > 0) RaiseRefill(why);
                else if (NeedsRefill && cfg.DispenseAutoResume) ClearRefill();
            }
        }
        else if (NeedsRefill)
        {
            // The setting was turned off while the banner was up.
            NeedsRefill = false;
            RefillReason = "";
        }

        if (!cfg.DispenseAcceptRequests) return;

        var targets = PendingTargets();
        if (targets.Count == 0) return;

        var from = TradeEngine.IncomingTradeRequest(targets);
        if (string.IsNullOrEmpty(from)) return;

        // Out of stock and set to turn people away. Say no rather than take their
        // gil and send them off still owed — a declined trade moves nothing, which
        // is what keeps a sweeping alt waiting here instead of going home.
        if (TurningTradesAway)
        {
            if (!TradeEngine.DeclineTradeRequest()) return;

            // Same alt retrying on a few-second cadence; one line per character per
            // half-minute is enough to see what's happening.
            declinedAt.TryGetValue(from, out var last);
            if ((DateTime.Now - last).TotalSeconds > 30)
            {
                declinedAt[from] = DateTime.Now;
                AddLog(from, "Turned away — out of stock. They'll keep waiting.", true);
            }
            return;
        }

        if (!TradeEngine.AcceptTradeRequest()) return;

        Partner = from;
        AddLog(from, "Trade request accepted.");
        Goto(DispenseState.WaitOpen, 20);
    }

    private void DoWaitOpen()
    {
        if (TradeEngine.IsTradeOpen) BeginWindow();
    }

    // Snapshot stock the moment a window opens, before anything can move. Everything
    // we later claim to have handed over is measured against this.
    private void BeginWindow()
    {
        before.Clear();
        foreach (var it in Handouts)
            before[(it.ItemId, it.Hq)] = Inventory.CountOf(it.ItemId, it.Hq, false);

        plan.Clear();
        planPos = 0;
        windowOpenedAt = DateTime.Now;
        Goto(DispenseState.Identify, 45);
    }

    private void DoIdentify()
    {
        if (!TradeEngine.IsTradeOpen) { Goto(DispenseState.Verify, 30); return; }

        // Re-derive the partner from the window itself rather than trusting whoever
        // we accepted a request from — the two should agree, and if they don't, the
        // window is the authority on who is actually standing in it.
        var seen = TradeEngine.TradePartner();
        if (!string.IsNullOrEmpty(seen)) Partner = seen;

        if (string.IsNullOrEmpty(Partner))
        {
            // Give the window a moment to finish drawing before concluding anything.
            if ((DateTime.Now - windowOpenedAt).TotalSeconds < IdentifyGraceSeconds)
                return;

            // Still nameless. Can't identify them, so can't hand them anything —
            // leave the window alone entirely rather than guess.
            if (!warnedUnknown)
            {
                warnedUnknown = true;
                AddLog("", "A trade is open but I can't tell who with — leaving it alone.", true);
            }
            ignoringWindow = true;
            Goto(DispenseState.Watch);
            return;
        }

        if (!Whitelist.Contains(Partner))
        {
            AddLog(Partner, "Not on the Characters list — leaving this trade alone.", true);
            ignoringWindow = true;
            Goto(DispenseState.Watch);
            return;
        }

        // On the list but square with us. Still see the trade through, so whatever
        // they're handing over lands: with Dropbox off, nobody else will.
        var owed = OwedTo(Partner);
        if (owed <= 0)
        {
            AddLog(Partner, abandoned.Contains(Partner)
                ? "Given up on this one earlier — accepting the trade, dispensing nothing."
                : "Already had their handout — accepting the trade, dispensing nothing.");
            Goto(DispenseState.Confirm, 120);
            return;
        }

        // Not enough for all of what they're owed. Take the trade so their gil
        // still lands, but hand over nothing: half-serving someone who is about to
        // walk away is worse than serving them properly after a refill, and it
        // keeps the ledger meaning one simple thing.
        if (cfg.DispenseStopWhenOut)
        {
            var short_ = Shortfall(i => Math.Max(0, i.Amount - GivenTo(Partner, i)));
            if (short_.Length > 0)
            {
                RaiseRefill(short_);

                if (cfg.DispenseDeclineWhenOut)
                {
                    // Back out. Nothing moves, so the other side retries rather
                    // than counting this visit as done.
                    AddLog(Partner,
                        "Backing out — not enough stock for their full handout. "
                        + "They'll wait and retry.", true);
                    TradeEngine.CancelTrade();
                    ResetWindow();
                    ignoringWindow = true;
                    Goto(DispenseState.Watch);
                    return;
                }

                AddLog(Partner,
                    "Skipped for now — not enough stock for their full handout. "
                    + "Taking the trade so their gil still lands.", true);
                Goto(DispenseState.Confirm, 120);
                return;
            }
        }

        foreach (var it in Handouts)
        {
            if (plan.Count >= TradeEngine.MaxItemsPerTrade) break;

            var want = it.Amount - GivenTo(Partner, it);
            if (want <= 0) continue;

            var slots = Inventory.PlanGive(it.ItemId, it.Hq, it.Label, want, it.Reserve,
                                           TradeEngine.MaxItemsPerTrade - plan.Count);

            if (slots.Count == 0)
            {
                var have = Inventory.CountOf(it.ItemId, it.Hq, false);
                AddLog(Partner,
                    $"Out of {it.Label} — {have:N0} in the bags, {it.Reserve:N0} reserved, "
                    + $"{want:N0} still owed.", true);
                continue;
            }

            plan.AddRange(slots);
        }

        if (plan.Count == 0)
        {
            AddLog(Partner, "Nothing available to hand over — accepting the trade anyway.");
            Goto(DispenseState.Confirm, 120);
            return;
        }

        AddLog(Partner, $"Handing over {plan.Sum(s => s.Take):N0} in {plan.Count} stack(s).");
        Goto(DispenseState.Offer, 60);
    }

    private void DoOffer()
    {
        if (!TradeEngine.IsTradeOpen) { Goto(DispenseState.Verify, 30); return; }

        if (planPos >= plan.Count)
        {
            Goto(DispenseState.Confirm, 120);
            return;
        }

        var slot = plan[planPos];

        if (TradeEngine.OfferItem(slot))
        {
            // Any stack holding more than one pops a quantity prompt, even when
            // we're handing over the whole thing.
            if (TradeEngine.GoesStraightIn(slot)) planPos++;
            else Goto(DispenseState.Qty, 30);
        }
    }

    private void DoQty()
    {
        if (!TradeEngine.IsTradeOpen) { Goto(DispenseState.Verify, 30); return; }

        // Defensive: an index out of range here would escape into Dalamud's
        // framework update rather than merely failing a trade.
        if (planPos < 0 || planPos >= plan.Count) { Goto(DispenseState.Confirm, 120); return; }

        var slot = plan[planPos];

        if (TradeEngine.IsGilInputOpen && TradeEngine.SetNumericInput((int)slot.Take))
        {
            planPos++;
            Goto(DispenseState.Offer, 60);
        }
    }

    private void DoConfirm()
    {
        if (!TradeEngine.IsTradeOpen) { Goto(DispenseState.Verify, 30); return; }

        // Stay here until the window actually closes rather than advancing on the
        // first click. Changing either side's offer clears both locks, and the other
        // side is an alt still loading its own gil in — so the lock routinely has to
        // be re-clicked, and a state that moved on after one click would just sit
        // waiting for a close that never comes.
        if (TradeEngine.ConfirmTradeYesno(cfg.NoOp)) return;
        TradeEngine.ConfirmTrade(cfg.NoOp);
    }

    private void DoVerify()
    {
        if (TradeEngine.IsTradeOpen) return;

        // Let the server settle before reading anything back.
        if (!Throttle("Cons.DispPost", 1500)) return;

        if (cfg.NoOp)
        {
            AddLog(Partner, "[DRY RUN] Trade not actually sent.");
            ResetWindow();
            Goto(DispenseState.Watch);
            return;
        }

        // No identified partner means nothing can be credited to anybody. Can only
        // happen if the window closed before it could be read; the goods, if any,
        // have gone either way, so just reset rather than write a bogus ledger row.
        if (string.IsNullOrEmpty(Partner))
        {
            ResetWindow();
            Goto(DispenseState.Watch);
            return;
        }

        long moved = 0;
        foreach (var it in Handouts)
        {
            var had = before.TryGetValue((it.ItemId, it.Hq), out var b) ? b : 0;
            var now = Inventory.CountOf(it.ItemId, it.Hq, false);
            var delta = had - now;
            if (delta <= 0) continue;

            // Cap the credit at what was actually owed. Without this, stock leaving
            // for any other reason during the window would be recorded against them.
            var owedThis = Math.Max(0, it.Amount - GivenTo(Partner, it));
            var credit = Math.Min(delta, owedThis);
            if (credit <= 0) continue;

            given[(Partner, it.ItemId, it.Hq)] = GivenTo(Partner, it) + credit;
            moved += credit;
        }

        var planned = plan.Sum(s => s.Take);

        if (moved <= 0 && planned > 0)
        {
            strikes.TryGetValue(Partner, out var n);
            n++;
            strikes[Partner] = n;

            if (n >= 3)
            {
                abandoned.Add(Partner);
                AddLog(Partner,
                    "Three trades closed without anything moving — giving up on this one. "
                    + "Their bags are probably full.", true);
                ResetWindow();
                Goto(DispenseState.Watch);
                return;
            }

            AddLog(Partner, $"Trade closed but nothing moved (attempt {n} of 3).", true);
        }
        else if (moved > 0)
        {
            strikes.Remove(Partner);
            var left = OwedTo(Partner);
            AddLog(Partner, $"Handed over {moved:N0}."
                + (left > 0 ? $" {left:N0} still to go." : " Done."));
        }

        plan.Clear();
        planPos = 0;

        // More owed than one window held. They're standing right there, so open
        // another rather than making them come back.
        //
        // Not while out of stock, though: the follow-up would open window after
        // window for the length of the budget, each one hitting the same shortfall
        // and handing over nothing.
        if (OwedTo(Partner) > 0 && cfg.DispenseFollowupSeconds > 0 && !NeedsRefill)
        {
            followupUntil = DateTime.Now.AddSeconds(cfg.DispenseFollowupSeconds);
            Goto(DispenseState.Followup, cfg.DispenseFollowupSeconds + 30);
            return;
        }

        ResetWindow();
        Goto(DispenseState.Watch);
    }

    private void DoFollowup()
    {
        if (TradeEngine.IsTradeOpen) { BeginWindow(); return; }

        // Ran dry partway through topping someone up.
        if (NeedsRefill)
        {
            ResetWindow();
            Goto(DispenseState.Watch);
            return;
        }

        // They may be opening one themselves — they still have gil to send. Let
        // theirs win rather than racing it.
        var incoming = TradeEngine.IncomingTradeRequest(new[] { Partner });
        if (!string.IsNullOrEmpty(incoming))
        {
            if (TradeEngine.AcceptTradeRequest()) Goto(DispenseState.WaitOpen, 20);
            return;
        }

        var them = TradeEngine.FindPlayer(Partner);
        if (them == null || DateTime.Now >= followupUntil)
        {
            var owed = OwedTo(Partner);
            if (owed > 0)
                AddLog(Partner, them == null
                    ? $"Left before I could finish — {owed:N0} not handed over."
                    : $"Ran out of follow-up time — {owed:N0} not handed over.", true);

            ResetWindow();
            Goto(DispenseState.Watch);
            return;
        }

        // Throttled inside UseTradeOn, and it refuses out of range or while they're
        // still streaming in.
        TradeEngine.UseTradeOn(Partner);
    }

    // ---------- plumbing ----------

    private readonly Dictionary<string, DateTime> throttles = new();

    private bool Throttle(string key, int ms)
    {
        var now = DateTime.Now;
        if (throttles.TryGetValue(key, out var last) && (now - last).TotalMilliseconds < ms)
            return false;
        throttles[key] = now;
        return true;
    }

    private void AddLog(string chara, string msg, bool error = false)
    {
        Log.Add(new SweepLogLine { Character = chara, Message = msg, IsError = error });
        if (Log.Count > 500) Log.RemoveAt(0);
        if (error) Plugin.Log.Warning($"[Consolidator/dispense] {chara}: {msg}");
        else Plugin.Log.Information($"[Consolidator/dispense] {chara}: {msg}");
    }
}
