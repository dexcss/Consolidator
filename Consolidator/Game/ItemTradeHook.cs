using System;
using ECommons.EzHookManager;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace Consolidator.Game;

// Putting an item into a trade window has no addon callback — the only way in is to
// call the game's own OfferItemTrade function directly. This is lifted from Dropbox,
// which is the known-good reference for it.
//
// Note the hook is created disabled (EzHook(..., false)): we never detour the
// function, we only borrow its address so we can call it ourselves. The detour below
// exists solely to satisfy EzHook's shape and is never invoked.
public unsafe class ItemTradeHook
{
    private delegate void OfferItemTradeDelegate(nint tradeAddress, ushort slot, InventoryType type);

    // Assigned by EzSignatureHelper.Initialize via reflection, which the compiler
    // can't see — hence the pragma. Declared non-nullable to match how ECommons
    // expects to write into it.
#pragma warning disable CS0649
    [EzHook("48 89 6C 24 ?? 48 89 74 24 ?? 57 48 83 EC 30 83 B9 ?? ?? ?? ?? ?? 41 8B F0", false)]
    private readonly EzHook<OfferItemTradeDelegate> OfferItemTradeHook = null!;
#pragma warning restore CS0649

    // False when the signature didn't resolve — gil still works, items don't, and we
    // say so plainly rather than silently doing nothing.
    public bool Available { get; private set; }
    public string? Error { get; private set; }

    public ItemTradeHook()
    {
        try
        {
            EzSignatureHelper.Initialize(this);
            Available = OfferItemTradeHook != null;
            if (!Available)
                Error = "OfferItemTrade signature didn't resolve.";
        }
        catch (Exception ex)
        {
            Available = false;
            Error = $"OfferItemTrade signature failed: {ex.Message}";
            Plugin.Log.Warning(Error);
        }
    }

    private void OfferItemTradeDetour(nint tradeAddress, ushort slot, InventoryType type)
        => throw new NotImplementedException();   // never called; hook stays disabled

    public static InventoryItem GetSlot(InventoryType type, int slot)
    {
        var im = InventoryManager.Instance();
        var cont = im->GetInventoryContainer(type);
        return cont->Items[slot];
    }

    // Offer the item in the given inventory slot into the open trade window.
    // If the stack is larger than 1, the game pops an InputNumeric for the quantity —
    // the caller must answer that next.
    public bool Offer(InventoryType type, ushort slot)
    {
        if (!Available) return false;

        try
        {
            if (GetSlot(type, slot).ItemId == 0)
            {
                Plugin.Log.Warning($"Refusing to trade an empty slot ({type}, {slot}).");
                return false;
            }

            var agent = (nint)UIModule.Instance()->GetAgentModule()->GetAgentByInternalId(AgentId.Trade);
            if (agent == 0) return false;

            OfferItemTradeHook.Delegate(agent + 40, slot, type);
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"OfferItemTrade failed: {ex.Message}");
            return false;
        }
    }

}
