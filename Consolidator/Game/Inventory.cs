using System;
using System.Collections.Generic;
using System.Linq;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace Consolidator.Game;

// One stack of one item, in one slot, with how much of it we intend to hand over.
public class TradeSlot
{
    public InventoryType Type;
    public ushort Slot;
    public uint ItemId;
    public string Name = "";
    public long InStack;      // how many are in this slot
    public long Take;         // how many of them we're trading
    public bool Hq;
}

public static unsafe class Inventory
{
    // The four normal player bags. Loose items live here — this is the safe default.
    private static readonly InventoryType[] Bags =
    {
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
    };

    // The armoury. Opt-in, because unlike the bags this holds *gear* — including gear
    // a character may be relying on. Trading out of here is a genuinely different
    // risk to trading a stack of Ceruleum Tanks, so it's off unless asked for.
    //
    // SoulCrystal is deliberately excluded: soul crystals aren't tradeable and there's
    // no reason to ever reach into that container.
    private static readonly InventoryType[] Armoury =
    {
        InventoryType.ArmoryMainHand,
        InventoryType.ArmoryOffHand,
        InventoryType.ArmoryHead,
        InventoryType.ArmoryBody,
        InventoryType.ArmoryHands,
        InventoryType.ArmoryWaist,
        InventoryType.ArmoryLegs,
        InventoryType.ArmoryFeets,
        InventoryType.ArmoryEar,
        InventoryType.ArmoryNeck,
        InventoryType.ArmoryWrist,
        InventoryType.ArmoryRings,
    };

    // Which containers we're allowed to take from, given the current settings.
    private static IEnumerable<InventoryType> Containers(bool includeArmoury)
        => includeArmoury ? Bags.Concat(Armoury) : Bags;

    public static string ItemName(uint id)
    {
        try
        {
            var row = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>()?.GetRowOrDefault(id);
            return row?.Name.ToString() ?? $"#{id}";
        }
        catch { return $"#{id}"; }
    }

    // Untradeable items can't go into a trade window at all — attempting it just
    // wedges the state machine. Cached, since this is checked per slot per scan.
    private static readonly Dictionary<uint, bool> tradeableCache = new();

    public static bool IsTradeable(uint id)
    {
        if (tradeableCache.TryGetValue(id, out var hit)) return hit;

        var ok = false;
        try
        {
            var row = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>()?.GetRowOrDefault(id);
            ok = row != null && !row.Value.IsUntradable;
        }
        catch { ok = false; }

        tradeableCache[id] = ok;
        return ok;
    }

    public static long CountOf(uint itemId, bool hq, bool includeArmoury)
    {
        long total = 0;
        try
        {
            var im = InventoryManager.Instance();
            if (im == null) return 0;

            foreach (var bag in Containers(includeArmoury))
            {
                var cont = im->GetInventoryContainer(bag);
                if (cont == null) continue;

                for (var i = 0; i < cont->Size; i++)
                {
                    var item = cont->Items[i];
                    if (item.ItemId != itemId) continue;
                    if (item.Flags.HasFlag(InventoryItem.ItemFlags.HighQuality) != hq) continue;
                    total += item.Quantity;
                }
            }
        }
        catch { /* inventory churn */ }
        return total;
    }

    // Work out exactly which slots to hand over for one configured item, honouring its
    // keep-amount. Returns them smallest-stack-first so we clear partial stacks before
    // breaking into full ones.
    public static List<TradeSlot> PlanFor(DepositItem cfg, bool includeArmoury)
    {
        var plan = new List<TradeSlot>();

        try
        {
            var im = InventoryManager.Instance();
            if (im == null) return plan;

            var found = new List<TradeSlot>();

            foreach (var bag in Containers(includeArmoury))
            {
                var cont = im->GetInventoryContainer(bag);
                if (cont == null) continue;

                for (var i = 0; i < cont->Size; i++)
                {
                    var item = cont->Items[i];
                    if (item.ItemId != cfg.ItemId) continue;
                    if (item.Quantity == 0) continue;

                    var isHq = item.Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
                    if (isHq != cfg.Hq) continue;

                    // Belt and braces: the item search already filters untradeables at
                    // add-time, but a config can be hand-edited and armoury gear is a
                    // bad thing to get wrong. Check the sheet again at trade-time.
                    if (!IsTradeable(item.ItemId)) continue;

                    found.Add(new TradeSlot
                    {
                        Type = bag,
                        Slot = (ushort)i,
                        ItemId = cfg.ItemId,
                        Name = cfg.Label,
                        InStack = item.Quantity,
                        Hq = isHq,
                    });
                }
            }

            var total = found.Sum(x => x.InStack);
            var toSend = total - cfg.KeepAmount;
            if (toSend <= 0) return plan;

            // Biggest stacks first, so we hand over as few slots as possible — a trade
            // window only fits 5 items.
            foreach (var slot in found.OrderByDescending(x => x.InStack))
            {
                if (toSend <= 0) break;

                var take = Math.Min(slot.InStack, toSend);
                slot.Take = take;
                toSend -= take;
                plan.Add(slot);
            }
        }
        catch { /* inventory churn */ }

        return plan;
    }
}
