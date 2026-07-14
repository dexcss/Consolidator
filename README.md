# Consolidator

Sweeps gil and items from your alt characters onto a single main character.

For each character in your list, Consolidator logs in, checks what they're carrying,
travels to a world you choose, walks over to your main, trades across everything above
the amounts you set, returns to the FC, and moves on to the next one.

## Requirements

| Plugin | Why |
| --- | --- |
| **AutoRetainer** | Character list, gil figures, and the relog between characters |
| **Lifestream** | World travel and getting back to the FC |
| **vnavmesh** | Walking from the aetheryte to your main, and to the front door |
| **Dropbox** | On the **receiving** character, to auto-accept the incoming trades |

Dropbox runs on the main in a second client with `Active` switched on. Consolidator never
touches the receiving side — it only sends.

## Setup

1. **Characters** — add characters by hand, or import them from AutoRetainer's Retainers
   and/or Deployables tabs.
2. **Send** — turn on gil and set a floor to leave behind; search for any items you want
   collected and set a keep amount for each.
3. **Settings** — set the receiving character, the world everyone travels to, and the
   destination (anything you'd type after `/li`).
4. Log the receiving character in on a second client with **Dropbox active**.
5. **Run** → Start.

Turn on **Dry run** for the first pass. It walks the entire loop but never confirms a
trade.

## What it sends

**Gil** — everything above the floor. A trade window caps at 1,000,000 gil, so richer
characters take several passes automatically.

**Items** — search by name, set a keep amount per item. Keep 9,996 Ceruleum Tanks and
trade the rest; keep 0 to send the lot. HQ is tracked separately. A trade window fits five
item stacks, so large hauls batch across multiple trades.

By default only the four main bags are searched. The armoury can be included, but it's
off by default — it holds gear, and this runs unattended across every character.

## Notes

- **The Characters tab list order is the run order.** Sort by any column, or use the
  arrows, and the run follows exactly what you see.
- **Gil and item counts are always re-read from the game, never tracked in a counter.**
  A failed trade can't lose track of anything — it just retries.
- **Crash protection** checkpoints after each character. If the game dies, you're offered
  a resume.
- **Pause** freezes the run and suspends the current step's timeout, so you can walk away
  mid-sweep.
- Characters already at or below their floors are skipped. Optionally they can be skipped
  without even logging in, using the gil figure AutoRetainer already has.

## Commands

```
/cons           open the window
/cons start
/cons pause
/cons resume
/cons stop
```

## Credits

The trade mechanics — both the gil field and the `OfferItemTrade` call, which has no addon
callback and needs a signature scan — are based on
[Dropbox](https://github.com/PunishXIV/Dropbox).
