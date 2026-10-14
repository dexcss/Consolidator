using System;
using System.Collections.Generic;
using System.Numerics;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.Automation;
using ECommons.Automation.UIInput;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Component.GUI;
using static ECommons.GenericHelpers;

namespace Consolidator.Game;

// The outbound (giving) half of a trade. The receiving character is expected to be
// running Dropbox, which auto-accepts — we never touch the accept side.
//
// Mechanism is lifted from Dropbox's TradeTask.cs, which is the known-good recipe:
//   target player -> /trade -> wait TradeOpen
//   -> Callback.Fire(Trade, true, 2, Zero)   [opens the gil InputNumeric]
//   -> Callback.Fire(InputNumeric, true, gil)
//   -> click the Trade confirm button
// The 1,000,000 gil ceiling is a hard game limit on a single trade window, so a
// character holding more than that needs multiple passes.
public static unsafe class TradeEngine
{
    public const int MaxGilPerTrade = 1_000_000;

    // A trade window holds five item slots. Gil is separate and doesn't consume one.
    public const int MaxItemsPerTrade = 5;

    // Set from Plugin; null if the signature didn't resolve.
    public static ItemTradeHook? ItemHook;

    public static bool ItemsAvailable => ItemHook?.Available == true;

    // Offer one slot into the open trade window. If the stack is bigger than what we're
    // taking, the game pops an InputNumeric for the amount — the caller answers it next
    // via SetNumericInput, exactly as with gil.
    public static bool OfferItem(TradeSlot s)
    {
        if (ItemHook == null || !ItemHook.Available) return false;
        if (!TryGetAddonByName<AtkUnitBase>("Trade", out var addon) || !IsAddonReady(addon))
            return false;
        if (!GenericThrottle) return false;
        if (!EzThrottler.Throttle("Cons.OfferItem", 400)) return false;

        return ItemHook.Offer(s.Type, s.Slot);
    }

    // True when offering this slot will NOT pop a quantity prompt. The game shows the
    // InputNumeric popup whenever the stack has more than one item — even if we're
    // taking the whole stack. Only a genuine single-item stack goes straight in.
    //
    // (This was the "trades 1 at a time / won't confirm" bug: a stack of 200 pops the
    // prompt, we didn't answer it, and the open modal both left the item unfilled and
    // blocked the confirm.)
    public static bool GoesStraightIn(TradeSlot s) => s.InStack <= 1;

    public static bool GenericThrottle => FrameThrottler.Throttle("Cons.TaskThrottle", 4);

    // ECommons types Player.Object and Player.Name as nullable, but Player.Available
    // already guarantees both. Rather than scatter null-forgiving operators across
    // every call site, funnel access through these — the guard lives in one place.
    public static string PlayerName => Player.Available ? (Player.Name ?? "") : "";

    // ContentId of the character currently logged in; 0 when we're at character
    // select or the client hasn't reported one yet.
    //
    // This is the ONLY player identity that is unique everywhere. A name is not:
    // FFXIV happily allows the same name on two different worlds, and comparing on
    // name alone is what let a sweep run against the wrong character (see
    // Sweeper.IsLoggedInAs).
    //
    // Read off PlayerState rather than IClientState — Dalamud dropped
    // IClientState.LocalContentId, so this is the supported route on API 15.
    public static ulong LocalContentId
    {
        get
        {
            try
            {
                var ps = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState.Instance();
                return ps == null ? 0 : ps->ContentId;
            }
            catch { return 0; }
        }
    }

    // "Name@HomeWorld" for the logged-in character, matching CharEntry.Full.
    // Home world, not current world — Lifestream travel changes the latter, so only
    // the home world stays comparable to a roster row mid-run.
    public static string PlayerFull
    {
        get
        {
            var n = PlayerName;
            if (string.IsNullOrEmpty(n)) return "";
            var w = HomeWorldName;
            return string.IsNullOrEmpty(w) ? n : $"{n}@{w}";
        }
    }

    private static Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter? Me =>
        Player.Available ? Player.Object : null;

    // Player.HomeWorld / CurrentWorld come back as RowRef<World> in ECommons 3.2.x,
    // so pull the name off explicitly rather than assuming a string.
    public static string HomeWorldName => WorldName(true);
    public static string CurrentWorldName => WorldName(false);

    private static string WorldName(bool home)
    {
        try
        {
            var me = Me;
            if (me == null) return "";
            var row = home ? me.HomeWorld : me.CurrentWorld;
            var w = row.ValueNullable;
            return w == null ? "" : w.Value.Name.ToString();
        }
        catch { return ""; }
    }

    public static long GetCurrentGil()
    {
        try
        {
            var im = InventoryManager.Instance();
            if (im == null) return 0;
            return im->GetInventoryItemCount(1); // 1 == gil item id
        }
        catch { return 0; }
    }

    // Is the named player ("First Last@World") loaded and targetable nearby?
    public static IPlayerCharacter? FindPlayer(string nameWithWorld)
    {
        try
        {
            foreach (var o in Svc.Objects)
            {
                if (o is not IPlayerCharacter pc) continue;
                if (!pc.IsTargetable) continue;

                var w = pc.HomeWorld.ValueNullable;
                var world = w == null ? "" : w.Value.Name.ToString();
                var full = $"{pc.Name.TextValue}@{world}";

                if (string.Equals(full, nameWithWorld, StringComparison.OrdinalIgnoreCase))
                    return pc;
            }
        }
        catch { /* object table churn */ }
        return null;
    }

    public static bool IsTradeOpen => Svc.Condition[ConditionFlag.TradeOpen];

    // ---------------- reading addons / identifying the other party ----------------

    // Every non-empty string an addon is currently displaying.
    //
    // Walks the node tree rather than reaching for node IDs. Text living inside a
    // component node (Type >= 1000) is invisible to GetNodeById, so those have to be
    // recursed into explicitly or the interesting text is silently missed.
    public static List<string> AddonText(string addonName)
    {
        var found = new List<string>();
        try
        {
            if (!TryGetAddonByName<AtkUnitBase>(addonName, out var addon)) return found;
            if (!IsAddonReady(addon)) return found;
            HarvestText(&addon->UldManager, found, 0);
        }
        catch { /* node churn */ }
        return found;
    }

    private static void HarvestText(AtkUldManager* uld, List<string> into, int depth)
    {
        if (uld == null || depth > 4) return;

        for (var i = 0; i < uld->NodeListCount; i++)
        {
            var node = uld->NodeList[i];
            if (node == null) continue;

            // Component node: its children live in the component's own node list.
            if ((ushort)node->Type >= 1000)
            {
                var comp = ((AtkComponentNode*)node)->Component;
                if (comp != null) HarvestText(&comp->UldManager, into, depth + 1);
                continue;
            }

            if (node->Type != NodeType.Text) continue;

            var text = ((AtkTextNode*)node)->NodeText.ToString();
            if (!string.IsNullOrWhiteSpace(text)) into.Add(text);
        }
    }

    // Players other than us within range, as "Name@HomeWorld" plus the object.
    // Self is excluded by address — our own name appears in the trade window too,
    // and matching it would make us our own trade partner.
    public static List<(string Full, IPlayerCharacter Pc)> NearbyPlayers(float maxDistance)
    {
        var list = new List<(string, IPlayerCharacter)>();
        try
        {
            var me = Me;
            if (me == null) return list;

            foreach (var o in Svc.Objects)
            {
                if (o is not IPlayerCharacter pc) continue;
                if (pc.Address == me.Address) continue;
                if (Vector3.Distance(me.Position, pc.Position) > maxDistance) continue;

                var w = pc.HomeWorld.ValueNullable;
                var world = w == null ? "" : w.Value.Name.ToString();
                var name = pc.Name.TextValue;
                if (string.IsNullOrWhiteSpace(name)) continue;

                list.Add((string.IsNullOrEmpty(world) ? name : $"{name}@{world}", pc));
            }
        }
        catch { /* object table churn */ }
        return list;
    }

    // Trade requests and the trade window only ever name the other player, never
    // their world — so the name on its own cannot identify anybody on a roster that
    // has the same name on two worlds. Resolve it through the object table instead:
    // the person trading us has to be standing right there, so the nearby player
    // whose name the addon is showing is the one, and they come with a home world
    // attached.
    //
    // Returns "" when nobody matches OR when two nearby players share the matched
    // name. Refusing to guess is the point: everything downstream of this hands over
    // goods.
    private static string MatchNamedPlayer(List<string> addonText, float maxDistance,
                                           IReadOnlyCollection<string>? restrictTo = null)
    {
        if (addonText.Count == 0) return "";

        var allowed = restrictTo == null
            ? null
            : new HashSet<string>(restrictTo, StringComparer.OrdinalIgnoreCase);

        var hits = new List<string>();
        foreach (var (full, pc) in NearbyPlayers(maxDistance))
        {
            if (allowed != null && !allowed.Contains(full)) continue;

            var name = pc.Name.TextValue;
            if (addonText.Any(t => t.Contains(name, StringComparison.OrdinalIgnoreCase)))
                hits.Add(full);
        }

        var distinct = hits.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return distinct.Count == 1 ? distinct[0] : "";
    }

    // A trade request has to come from someone standing next to us, so matching is
    // kept to a short radius — it keeps an unrelated dialog that happens to mention
    // a roster name (a party invite from across the zone, say) from being mistaken
    // for a trade request.
    public const float PartnerRange = 15.0f;

    // Who the open trade window is with, or "" if that can't be established.
    public static string TradePartner(float maxDistance = PartnerRange)
        => !IsTradeOpen ? "" : MatchNamedPlayer(AddonText("Trade"), maxDistance);

    // An unanswered trade request from one of the given characters, or "".
    //
    // The request dialog is a plain SelectYesno, so the only thing distinguishing it
    // from any other Yes/No prompt is that it names the requester. That's also what
    // makes this language-independent: we look for a name we already know rather
    // than for English prompt text.
    public static string IncomingTradeRequest(IReadOnlyCollection<string> allowedFulls,
                                              float maxDistance = PartnerRange)
    {
        if (IsTradeOpen) return "";
        if (allowedFulls.Count == 0) return "";
        return MatchNamedPlayer(AddonText("SelectYesno"), maxDistance, allowedFulls);
    }

    // Answer No to a trade request. Used when we have nothing to give and would
    // rather the other side waited and tried again than spent their visit on a
    // trade that does them no good.
    public static bool DeclineTradeRequest()
    {
        try
        {
            if (!TryGetAddonByName<AtkUnitBase>("SelectYesno", out var addon)) return false;
            if (!IsAddonReady(addon)) return false;
            if (!EzThrottler.Throttle("Cons.DeclineTrade", 1000)) return false;

            Callback.Fire(addon, true, 1);   // No
            return true;
        }
        catch { return false; }
    }

    public static bool AcceptTradeRequest()
    {
        try
        {
            if (!TryGetAddonByName<AtkUnitBase>("SelectYesno", out var addon)) return false;
            if (!IsAddonReady(addon)) return false;
            if (!EzThrottler.Throttle("Cons.AcceptTrade", 1000)) return false;

            Callback.Fire(addon, true, 0);   // Yes
            return true;
        }
        catch { return false; }
    }

    // Resolve a world name to its data centre and region. AR doesn't store either,
    // so we look them up in the game's own sheet: World -> DataCenter -> Region.
    // Cached, since this runs for every roster row.
    private static readonly Dictionary<string, (string Dc, string Region)> worldCache = new();

    public static (string Dc, string Region) ResolveWorld(string world)
    {
        if (string.IsNullOrWhiteSpace(world)) return ("", "");
        if (worldCache.TryGetValue(world, out var hit)) return hit;

        var result = ("", "");
        try
        {
            var row = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.World>()?
                         .FirstOrDefault(x => string.Equals(
                             x.Name.ToString(), world, StringComparison.OrdinalIgnoreCase));

            if (row != null && row.Value.RowId != 0)
            {
                var dc = row.Value.DataCenter.ValueNullable;
                if (dc != null)
                {
                    var dcName = dc.Value.Name.ToString();
                    result = (dcName, RegionName(dc.Value.Region.RowId));
                }
            }
        }
        catch { /* sheet miss */ }

        worldCache[world] = result;
        return result;
    }

    // DataCenter.Region is a RowRef<WorldRegionGroup>. That sheet's name column
    // isn't reliably populated, and other plugins only ever compare its RowId, so
    // map the IDs rather than depend on a name that may come back blank.
    private static string RegionName(uint region) => region switch
    {
        1 => "Japan",
        2 => "North America",
        3 => "Europe",
        4 => "Oceania",
        5 => "China",
        6 => "Korea",
        _ => $"Region {region}",
    };

    // The numeric popup used for both gil and stack quantities.
    public static bool IsGilInputOpen =>
        TryGetAddonByName<AtkUnitBase>("InputNumeric", out var a) && IsAddonReady(a);

    // The game rejects a trade request beyond a few yalms. The object table finds
    // players far outside that, which is why we must close the distance first.
    public const float TradeRange = 4.0f;

    public static float DistanceTo(IPlayerCharacter pc)
    {
        try
        {
            var me = Me;
            if (me == null) return float.MaxValue;
            return Vector3.Distance(me.Position, pc.Position);
        }
        catch { return float.MaxValue; }
    }

    // A player can appear in the object table and even report IsTargetable before
    // the client has finished streaming them in. Firing /trade at that point gets
    // silently rejected by the server, which is what made the first trade of each
    // character fail while the retry succeeded.
    //
    // RenderFlags == 0 means fully rendered/visible; anything else means the object
    // is still being loaded or is hidden.
    public static bool IsFullyRendered(IPlayerCharacter pc)
    {
        try
        {
            var obj = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)pc.Address;
            if (obj == null) return false;
            return obj->RenderFlags == 0;
        }
        catch { return false; }
    }

    // Everything the game needs to be true before a trade request will stick:
    // we're loaded and interactable, and so is the target.
    public static bool ReadyToTrade(IPlayerCharacter pc)
    {
        if (Me == null) return false;
        if (!Player.Interactable) return false;
        if (!IsScreenReady()) return false;
        if (!pc.IsTargetable) return false;
        if (!IsFullyRendered(pc)) return false;
        return true;
    }

    // Target the player, then fire /trade. Returns true once the request is sent.
    // Caller must have closed to within TradeRange first.
    public static bool UseTradeOn(string nameWithWorld)
    {
        var target = FindPlayer(nameWithWorld);
        if (target == null) return false;
        if (DistanceTo(target) > TradeRange) return false;
        if (!ReadyToTrade(target)) return false;

        if (Svc.Targets.Target?.Address == target.Address)
        {
            if (GenericThrottle && EzThrottler.Throttle("Cons.TradeOpen", 3000))
            {
                Chat.SendMessage("/trade");
                return true;
            }
        }
        else
        {
            if (GenericThrottle)
                Svc.Targets.Target = target;
        }
        return false;
    }

    // Opens the gil entry box on the Trade addon (atk value 2 == the gil field).
    public static bool OpenGilInput()
    {
        if (TryGetAddonByName<AtkUnitBase>("Trade", out var addon) && IsAddonReady(addon))
        {
            if (GenericThrottle)
            {
                Callback.Fire(addon, true, 2, Callback.ZeroAtkValue);
                return true;
            }
        }
        return false;
    }

    public static bool SetNumericInput(int num)
    {
        if (num < 0 || num > MaxGilPerTrade) return false;
        if (TryGetAddonByName<AtkUnitBase>("InputNumeric", out var addon) && IsAddonReady(addon))
        {
            if (GenericThrottle)
            {
                Callback.Fire(addon, true, num);
                return true;
            }
        }
        return false;
    }

    // Clicks our side's Trade/confirm button. Honours NoOp (dry run).
    // The button is NodeList[3] on the Trade addon — taken verbatim from Dropbox,
    // which is the known-good reference for this window.
    public static bool ConfirmTrade(bool noOp)
    {
        if (!TryGetAddonByName<AtkUnitBase>("Trade", out var addon) || !IsAddonReady(addon))
            return false;

        var btn = (AtkComponentButton*)addon->UldManager.NodeList[3]->GetComponent();
        if (btn == null || !btn->IsEnabled) return false;

        if (!EzThrottler.Throttle("Cons.Confirm", 1000)) return false;

        if (noOp)
        {
            Plugin.Log.Information("[NoOp] Would have confirmed trade.");
            return true;
        }

        btn->ClickAddonButton(addon);
        return true;
    }

    // A SelectYesno sitting open while no trade window is up — the residue of an
    // interrupted trade. It's modal and blocks the next /trade, so we detect and
    // clear it before opening a new trade.
    public static bool HasStaleTradeDialog =>
        !IsTradeOpen
        && TryGetAddonByName<AtkUnitBase>("SelectYesno", out var a)
        && IsAddonReady(a);

    public static void DismissStaleDialog()
    {
        try
        {
            if (!EzThrottler.Throttle("Cons.DismissStale", 500)) return;
            if (TryGetAddonByName<AtkUnitBase>("SelectYesno", out var addon) && IsAddonReady(addon))
                Callback.Fire(addon, true, 1);   // No
        }
        catch { /* ignore */ }
    }

    // Tear down a trade cleanly. Critically, this also dismisses any lingering
    // "Trade these items?" SelectYesno — if a timeout fires mid-confirm, that dialog
    // is left open and modal, silently blocking every trade for the rest of the run
    // ("wouldn't confirm anymore"). Cancelling the Trade window alone doesn't clear
    // it, so we answer No to the dialog first.
    public static void CancelTrade()
    {
        try
        {
            if (TryGetAddonByName<AtkUnitBase>("SelectYesno", out var yn) && IsAddonReady(yn))
                Callback.Fire(yn, true, 1);   // No — back out of the confirmation
        }
        catch { /* ignore */ }

        try
        {
            if (TryGetAddonByName<AtkUnitBase>("Trade", out var addon) && IsAddonReady(addon))
                Callback.Fire(addon, true, -1);
        }
        catch { /* ignore */ }
    }

    // True when we're actually inside a house/apartment interior, as opposed to
    // standing on the plot outside. Verified against FFXIVClientStructs'
    // HousingManager, rather than matching a hardcoded list of territory IDs.
    public static bool InHousingInterior
    {
        get
        {
            try
            {
                var hm = HousingManager.Instance();
                if (hm == null) return false;
                return hm->IsInside() || hm->IsInWorkshop();
            }
            catch { return false; }
        }
    }

    // ---------------- housing entrance ----------------

    // The estate entrance is a targetable object; Lifestream matches it by name,
    // so we do the same. Covers the client languages it supports.
    private static readonly string[] EntranceNames =
    {
        "Entrance", "Eingang", "Entrée",
        "ハウスへ入る", "进入房屋", "進入房屋", "주택으로 들어가기",
    };

    private static readonly string[] ConfirmEntranceText =
    {
        "Enter the estate hall?",
        "Das Gebäude betreten?",
        "Entrer dans la maison ?",
        "「ハウス」へ入りますか？",
        "要进入这间房屋吗？",
        "要進入這間房屋嗎？",
        "'주택'으로 들어가시겠습니까?",
    };

    public static IGameObject? FindEntrance(out float distance)
    {
        distance = float.MaxValue;
        IGameObject? best = null;

        try
        {
            var self = Me;
            if (self == null) return null;
            var me = self.Position;

            foreach (var o in Svc.Objects)
            {
                if (!o.IsTargetable) continue;
                var name = o.Name.TextValue;
                if (!EntranceNames.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var d = Vector3.Distance(me, o.Position);
                if (d < distance)
                {
                    distance = d;
                    best = o;
                }
            }
        }
        catch { /* object table churn */ }

        return best;
    }

    // Target and interact with the entrance object. Interact range is short, so the
    // caller must have walked over first.
    public static bool InteractWithEntrance(IGameObject entrance)
    {
        try
        {
            if (Svc.Targets.Target?.Address != entrance.Address)
            {
                if (GenericThrottle) Svc.Targets.Target = entrance;
                return false;
            }

            if (!EzThrottler.Throttle("Cons.Entrance", 2000)) return false;

            TargetSystem.Instance()->InteractWithObject(
                (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)entrance.Address, false);
            return true;
        }
        catch { return false; }
    }

    // The "Enter the estate hall?" prompt. Matched by text so we don't click an
    // unrelated Yes/No that happens to be open.
    public static bool ConfirmEntranceYesno()
    {
        try
        {
            if (!TryGetAddonByName<AtkUnitBase>("SelectYesno", out var addon)) return false;
            if (!IsAddonReady(addon)) return false;
            if (!EzThrottler.Throttle("Cons.EntranceYes", 1000)) return false;

            Callback.Fire(addon, true, 0);
            return true;
        }
        catch { return false; }
    }

    // The "Trade these items?" confirmation the sender gets after locking the trade.
    // Separate from the entrance one so it can honour NoOp — a dry run must not click
    // Yes on a real trade. Returns true if a dialog was present and handled.
    public static bool ConfirmTradeYesno(bool noOp)
    {
        try
        {
            if (!TryGetAddonByName<AtkUnitBase>("SelectYesno", out var addon)) return false;
            if (!IsAddonReady(addon)) return false;
            if (!EzThrottler.Throttle("Cons.TradeYes", 1000)) return false;

            if (noOp)
            {
                Plugin.Log.Information("[NoOp] Would have confirmed the trade dialog.");
                // Cancel out instead, so a dry run doesn't actually trade.
                Callback.Fire(addon, true, 1);
                return true;
            }

            Callback.Fire(addon, true, 0);
            return true;
        }
        catch { return false; }
    }

    // Clicks Yes on a SelectYesno if one is up. Used for the logout confirmation.
    // Returns true if it acted this frame.
    public static bool ConfirmYesno()
    {
        try
        {
            if (!TryGetAddonByName<AtkUnitBase>("SelectYesno", out var addon)) return false;
            if (!IsAddonReady(addon)) return false;
            if (!EzThrottler.Throttle("Cons.Yesno", 1000)) return false;

            Callback.Fire(addon, true, 0);
            return true;
        }
        catch { return false; }
    }
}
