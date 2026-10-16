# Consolidator

Moves gil and items from your alt characters onto a single main — and hands items
back out from that main to the alts.

For each character in your list, the alt client logs in, checks what they're carrying,
travels to a world you choose, walks over to your main, trades across everything above
the amounts you set, returns to the FC, and moves on to the next one. A second client,
logged in as the main, can run the **dispenser**: it waits for alts to trade it and
puts a fixed handout into the same window, so gil goes up and handouts come down in one
visit.

## Requirements

| Plugin | Why |
| --- | --- |
| **AutoRetainer** | Character list, gil figures, and the relog between characters |
| **Lifestream** | World travel and getting back to the FC |
| **vnavmesh** | Walking from the aetheryte to your main, and to the front door |
| **Dropbox** (optional) | On the receiving character, to auto-accept incoming trades — unless you let Consolidator accept them itself |

## Setup

1. **Characters** — add characters by hand, or import them from AutoRetainer's Retainers
   and/or Deployables tabs. The list order is the run order.
2. **Give away** — what the alts hand over: gil above a floor, and/or items above a
   keep amount each.
3. **Hand out** — what the main gives back: a fixed amount (or a top-up target) per
   character, with an optional reserve that never leaves.
4. **Settings** — the receiving character, the world everyone travels to, and the
   destination (anything you'd type after `/li`).
5. Run the receiving character on a second client with Consolidator's dispenser armed
   (or Dropbox active), then start the sweep on the alt client.

Turn on **Dry run** for the first pass. It walks the entire loop but never confirms a
trade.

## What it sends

**Gil** — everything above the floor. A trade window caps at 1,000,000 gil, so richer
characters take several passes automatically.

**Items** — search by name, set a keep amount per item. Keep 9,996 Ceruleum Tanks and
trade the rest; keep 0 to send the lot. HQ is tracked separately, and an item can sit
on the list twice (NQ and HQ) with a different keep amount per row. A trade window fits
five item stacks, so large hauls batch across multiple trades.

By default only the four main bags are searched. The armoury can be included, but it's
off by default — it holds gear, and this runs unattended across every character.

## What it hands out

The **Hand out** tab defines a fixed handout for each character on the Characters list.
Per item: enabled, HQ, amount, and an optional reserve.

With **Top up** on (Ceruleum Tanks and Magitek Repair Materials only — the two item
counts AutoRetainer tracks per character), Amount becomes a target rather than a
quantity: a character holding 300 of a 2,997 target is handed 2,697.

When the bags can't cover a whole handout the dispenser stops and shows a refill
banner, and by default it turns incoming trades away, so a sweeping alt stays put and
waits instead of handing over its gil and leaving unserved. It resumes on its own once
refilled, or on the Continue button.

## Collection runs

`/cons collect` walks every enabled alt to the receiver to *be given* something rather
than to drop anything off — same queue, same order, same crash protection, with nobody
skipped for being empty.

## Notes

- **The Characters tab list order is the run order.** Sort by any column, or use the
  arrows, and the run follows exactly what you see.
- **Gil and item counts are always re-read from the game, never tracked in a
  counter.** A failed trade can't lose track of anything — it just retries.
- **Crash protection** checkpoints after each character. If the game dies, you're
  offered a resume.
- **Pause** freezes the run and suspends the current step's timeout, so you can walk
  away mid-sweep.
- **Patient retry** (off by default) waits out a busy receiver — useful when several
  accounts feed the same main. Set the give-up budget to 0 to never give up.
- Characters already at or below their floors are skipped. Optionally they can be
  skipped without even logging in, using the gil figure AutoRetainer already has.

## Commands

```
/cons                 open the window
/cons start           start a sweep
/cons collect         walk alts to the receiver to collect a handout
/cons pause
/cons resume
/cons stop            stop the sweep
/cons dispense        arm the dispenser on the receiving character
/cons dispense stop
```

## Credits

The trade mechanics — both the gil field and the `OfferItemTrade` call, which has no
addon callback and needs a signature scan — are based on
[Dropbox](https://github.com/kawaii/Dropbox).
