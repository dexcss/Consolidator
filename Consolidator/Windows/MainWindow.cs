using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Windowing;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Colors;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using Consolidator.Game;

namespace Consolidator.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private Configuration Cfg => plugin.Config;
    private Sweeper Sweep => plugin.Sweeper;

    private static readonly string Ver =
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(4) ?? "?";

    // Version in the title, so "am I actually running the build I just made?" is
    // never a guess.
    public MainWindow(Plugin plugin) : base($"Consolidator v{Ver}###ConsolidatorMain")
    {
        this.plugin = plugin;
        Size = new Vector2(780, 620);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(600, 400),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public void Dispose() { }

    public override void Draw()
    {
        DrawStatusBar();
        ImGui.Separator();

        using var tabs = ImRaii.TabBar("##constabs");
        if (!tabs) return;

        DrawTab("Run", DrawRunTab);
        DrawTab("Characters", DrawCharactersTab);
        DrawTab("Send", DrawSendTab);
        DrawTab("Settings", DrawSettingsTab);
        DrawTab("Log", DrawLogTab);
    }

    private static void DrawTab(string label, Action draw)
    {
        using var t = ImRaii.TabItem(label);
        if (t) draw();
    }

    // ---------------- status ----------------

    private void DrawStatusBar()
    {
        var arOk = PluginIpc.IsAutoRetainerAvailable();
        var lsOk = PluginIpc.IsLifestreamAvailable();

        Dot(arOk, "AutoRetainer");
        ImGui.SameLine();
        Dot(lsOk, "Lifestream");
        ImGui.SameLine();
        Dot(PluginIpc.IsVnavAvailable(), "vnavmesh");
        ImGui.SameLine();

        if (Sweep.Running && Sweep.Paused)
        {
            ImGui.TextColored(ImGuiColors.DalamudOrange,
                $"  PAUSED — {Sweep.State}  ({Sweep.Index + 1}/{Sweep.Queue.Count})");
        }
        else if (Sweep.Running)
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow,
                $"  {Sweep.State}  ({Sweep.Index + 1}/{Sweep.Queue.Count})");
        }
        else
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "  Idle");
        }

        if (Cfg.NoOp)
        {
            ImGui.SameLine();
            ImGui.TextColored(ImGuiColors.DalamudOrange, "  [DRY RUN]");
        }
    }

    private static void Dot(bool ok, string label)
    {
        ImGui.TextColored(ok ? ImGuiColors.HealerGreen : ImGuiColors.DalamudRed, "\u25CF");
        ImGui.SameLine(0, 4);
        ImGui.TextUnformatted(label);
    }

    // ---------------- run ----------------

    private void DrawRunTab()
    {
        ImGui.Spacing();

        // Offer to pick up an interrupted run before anything else.
        if (Sweep.CanResume)
        {
            var run = Cfg.Run;
            var next = run.Index < run.Queue.Count ? run.Queue[run.Index].Full : "?";

            ImGui.PushTextWrapPos(ImGui.GetContentRegionAvail().X);
            ImGui.TextColored(ImGuiColors.DalamudOrange,
                $"An earlier run was interrupted on {run.SavedAt:MMM d, HH:mm}.");
            ImGui.TextUnformatted(
                $"{run.Index} of {run.Queue.Count} done, {run.TotalSent:N0} gil moved. " +
                $"Next up: {next}.");
            ImGui.PopTextWrapPos();

            ImGui.Spacing();
            if (ImGui.Button("Resume that run", new Vector2(150, 26)))
                Sweep.ResumeInterrupted();
            ImGui.SameLine();
            if (ImGui.Button("Discard", new Vector2(90, 26)))
                Sweep.DiscardInterrupted();

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
        }

        var err = Sweep.Validate();
        if (err != null && !Sweep.Running)
        {
            ImGui.TextColored(ImGuiColors.DalamudRed, err);
            ImGui.Spacing();
        }

        using (ImRaii.Disabled(Sweep.Running || err != null))
        {
            if (ImGui.Button(Cfg.NoOp ? "Start (dry run)" : "Start sweep", new Vector2(160, 30)))
                Sweep.Start();
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(!Sweep.Running))
        {
            if (Sweep.Paused)
            {
                if (ImGui.Button("Resume", new Vector2(110, 30)))
                    Sweep.Resume();
            }
            else
            {
                if (ImGui.Button("Pause", new Vector2(110, 30)))
                    Sweep.Pause();
            }
        }
        ImGuiComponents.HelpMarker(
            "Freezes the run where it stands. Movement stops and the current step's\n" +
            "timeout is suspended, so you can walk away and pick up where you left off.");

        ImGui.SameLine();
        using (ImRaii.Disabled(!Sweep.Running))
        {
            if (ImGui.Button("Stop", new Vector2(100, 30)))
                Sweep.Stop();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var enabled = Cfg.Characters.Count(c => c.Enabled);
        var gil = Cfg.Items.FirstOrDefault(i => i.IsGil);

        ImGui.TextUnformatted($"Characters queued: {enabled} (runs in the order shown on the Characters tab)");

        if (Cfg.SkipByCachedGil && gil != null && gil.Enabled)
        {
            var skippable = Cfg.Characters.Count(c =>
                c.Enabled
                && c.LastSeenGilAt != DateTime.MinValue
                && (Cfg.CachedGilMaxAgeHours <= 0
                    || (DateTime.Now - c.LastSeenGilAt).TotalHours <= Cfg.CachedGilMaxAgeHours)
                && c.LastSeenGil <= gil.KeepAmount);

            if (skippable > 0)
                ImGui.TextColored(ImGuiColors.DalamudGrey,
                    $"  {skippable} will be skipped without logging in (already at/below the floor).");
        }
        ImGui.TextUnformatted($"Receiving: {(string.IsNullOrWhiteSpace(Cfg.MainName) ? "(not set)" : Cfg.MainFull)}");
        ImGui.TextUnformatted($"Destination: {Cfg.TargetWorld} \u2192 \"{Cfg.Destination}\"");
        if (gil != null && gil.Enabled)
            ImGui.TextUnformatted($"Gil: send all, keeping {gil.KeepAmount:N0} per character");

        var itemCount = Cfg.Items.Count(i => i.Enabled && !i.IsGil);
        if (itemCount > 0)
            ImGui.TextUnformatted($"Items: {itemCount} configured");
        if (itemCount > 0 && !TradeEngine.ItemsAvailable)
            ImGui.TextColored(ImGuiColors.DalamudRed,
                "  Item trading is unavailable — only gil will be sent.");

        ImGui.Spacing();
        ImGui.TextColored(ImGuiColors.DalamudGrey,
            "The receiving character must be logged in on another client with\n" +
            "Dropbox active so it auto-accepts the incoming trades.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted("Recent:");

        using (var log = ImRaii.Child("##recentlog", new Vector2(-1, -1), true))
        {
            if (log)
            {
                foreach (var line in Sweep.Log)
                    DrawLogLine(line);

                // Stick to the bottom while running, unless the user scrolled up.
                if (Sweep.Running && ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 40)
                    ImGui.SetScrollHereY(1.0f);
            }
        }
    }

    // ---------------- characters ----------------

    private string addName = "";
    private string addWorld = "";
    private string importResult = "";

    private bool backfilled;

    private void DrawCharactersTab()
    {
        // Rosters saved before Order/Region existed need those filled in once.
        if (!backfilled)
        {
            backfilled = true;
            var dirty = false;

            foreach (var c in Cfg.Characters.Where(x => string.IsNullOrEmpty(x.Region)))
            {
                var (dc, region) = TradeEngine.ResolveWorld(c.World);
                c.DataCenter = dc;
                c.Region = region;
                dirty = true;
            }

            // ApplySort() only fires when ImGui flags the specs dirty, i.e. on a
            // click. Without this, a fresh load would show the list in whatever
            // order it was serialised in while the header claims something else —
            // and since the list order is the run order, that matters.
            Sort();
            dirty = true;

            if (dirty) Cfg.Save();
        }

        ImGui.Spacing();

        // ---- manual add ----
        ImGui.TextColored(ImGuiColors.DalamudViolet, "Add a character");
        ImGui.Spacing();

        ImGui.SetNextItemWidth(190);
        ImGui.InputTextWithHint("##addname", "First Last", ref addName, 64);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(130);
        ImGui.InputTextWithHint("##addworld", "World", ref addWorld, 32);
        ImGui.SameLine();

        var canAdd = !string.IsNullOrWhiteSpace(addName) && !string.IsNullOrWhiteSpace(addWorld);
        using (ImRaii.Disabled(!canAdd))
        {
            if (ImGui.Button("Add"))
            {
                AddManual(addName.Trim(), addWorld.Trim());
                addName = "";
                addWorld = "";
            }
        }

        if (Player.Available)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Add current character"))
                AddManual(TradeEngine.PlayerName, TradeEngine.HomeWorldName);
        }

        ImGui.Spacing();
        ImGui.Separator();

        // ---- import ----
        ImGui.TextColored(ImGuiColors.DalamudViolet, "Import from AutoRetainer");
        ImGui.Spacing();

        if (ImGui.Checkbox("Retainers tab", ref Cfg.ImportFromRetainers)) Cfg.Save();
        ImGui.SameLine(0, 20);
        if (ImGui.Checkbox("Deployables tab", ref Cfg.ImportFromDeployables)) Cfg.Save();

        ImGui.SameLine(0, 20);
        var canImport = Cfg.ImportFromRetainers || Cfg.ImportFromDeployables;
        using (ImRaii.Disabled(!canImport || !PluginIpc.IsAutoRetainerAvailable()))
        {
            if (ImGui.Button("Import"))
                importResult = Import();
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(!PluginIpc.IsAutoRetainerAvailable()))
        {
            if (ImGui.Button("Refresh gil"))
                importResult = RefreshFromAr();
        }
        ImGuiComponents.HelpMarker(
            "Pulls current gil figures from AutoRetainer without importing anyone new.\n" +
            "AR updates its own gil record each time it logs in to run submarines, so\n" +
            "these stay current without Consolidator having to visit the character.");

        ImGuiComponents.HelpMarker(
            "Import mirrors AutoRetainer's own tab logic:\n" +
            "  Retainers   = Enabled and not ExcludeRetainer\n" +
            "  Deployables = WorkshopEnabled and not ExcludeWorkshop\n\n" +
            "Ticking both imports the union. Existing rows are never duplicated,\n" +
            "and importing never removes anything you added by hand.");

        if (!string.IsNullOrEmpty(importResult))
        {
            ImGui.Spacing();
            var ok = importResult.StartsWith("Added");
            ImGui.PushTextWrapPos(ImGui.GetContentRegionAvail().X);
            ImGui.TextColored(ok ? ImGuiColors.HealerGreen : ImGuiColors.DalamudOrange, importResult);
            ImGui.PopTextWrapPos();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ---- list ----
        if (Cfg.Characters.Count == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey,
                "No characters yet. Add one above, or import from AutoRetainer.");
            return;
        }

        if (ImGui.SmallButton("Enable all"))
        {
            foreach (var c in Cfg.Characters) c.Enabled = true;
            Cfg.Save();
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Disable all"))
        {
            foreach (var c in Cfg.Characters) c.Enabled = false;
            Cfg.Save();
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Clear list"))
            ImGui.OpenPopup("##clearall");

        using (var popup = ImRaii.Popup("##clearall"))
        {
            if (popup)
            {
                ImGui.TextUnformatted($"Remove all {Cfg.Characters.Count} characters?");
                if (ImGui.Button("Yes, clear"))
                {
                    Cfg.Characters.Clear();
                    Cfg.Save();
                    ImGui.CloseCurrentPopup();
                }
                ImGui.SameLine();
                if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
            }
        }

        ImGui.SameLine();
        ImGui.TextColored(ImGuiColors.DalamudGrey,
            $"{Cfg.Characters.Count(c => c.Enabled)} of {Cfg.Characters.Count} enabled");

        ImGui.Spacing();

        // WYSIWYG: whatever this table shows, top to bottom, is what the run does.
        // Sorting reorders the run. The arrows are only offered on the # column,
        // since dragging rows around under an active sort would just be undone by
        // the next re-sort.
        ImGui.TextColored(ImGuiColors.DalamudGrey,
            "Characters run top to bottom, exactly as listed here. "
            + "Sort by any column to change the order, or use the arrows.");

        ImGui.Spacing();

        using var table = ImRaii.Table("##chars", 9,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY |
            ImGuiTableFlags.Sortable);
        if (!table) return;

        ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed
            | ImGuiTableColumnFlags.DefaultSort, 32);                       // 0
        ImGui.TableSetupColumn("On", ImGuiTableColumnFlags.WidthFixed, 28); // 1
        ImGui.TableSetupColumn("Character", ImGuiTableColumnFlags.WidthStretch); // 2
        ImGui.TableSetupColumn("World", ImGuiTableColumnFlags.WidthFixed, 100);  // 3
        ImGui.TableSetupColumn("Region", ImGuiTableColumnFlags.WidthFixed, 110); // 4
        ImGui.TableSetupColumn("Gil", ImGuiTableColumnFlags.WidthFixed, 100);    // 5
        ImGui.TableSetupColumn("Last run", ImGuiTableColumnFlags.WidthFixed, 100); // 6
        ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.WidthFixed, 80);  // 7
        ImGui.TableSetupColumn("##rm", ImGuiTableColumnFlags.WidthFixed
            | ImGuiTableColumnFlags.NoSort, 88);                            // 8
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        ApplySort();

        CharEntry? remove = null;
        int moveUp = -1, moveDown = -1;

        for (var i = 0; i < Cfg.Characters.Count; i++)
        {
            var c = Cfg.Characters[i];
            ImGui.TableNextRow();
            var id = c.Cid != 0 ? c.Cid.ToString() : c.Full;

            // #
            ImGui.TableNextColumn();
            ImGui.TextColored(ImGuiColors.DalamudGrey, $"{i + 1}");

            // On
            ImGui.TableNextColumn();
            var on = c.Enabled;
            if (ImGui.Checkbox($"##on{id}", ref on))
            {
                c.Enabled = on;
                Cfg.Save();
            }

            // Character
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(c.Name);

            // World
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(c.World);

            // Region
            ImGui.TableNextColumn();
            if (string.IsNullOrEmpty(c.Region))
                ImGui.TextColored(ImGuiColors.DalamudGrey, "—");
            else
            {
                ImGui.TextUnformatted(c.Region);
                if (ImGui.IsItemHovered() && !string.IsNullOrEmpty(c.DataCenter))
                    ImGui.SetTooltip(c.DataCenter);
            }

            // Gil (AR's figure, refreshed whenever it runs subs on that character)
            ImGui.TableNextColumn();
            if (c.LastSeenGil > 0)
            {
                var gilFloor = Cfg.Items.FirstOrDefault(x => x.IsGil)?.KeepAmount ?? 0;
                var hasSpare = c.LastSeenGil > gilFloor;
                ImGui.TextColored(hasSpare ? ImGuiColors.HealerGreen : ImGuiColors.DalamudGrey,
                    Abbrev(c.LastSeenGil));
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"{c.LastSeenGil:N0} gil\nSeen {Ago(c.LastSeenGilAt)}");
            }
            else
            {
                ImGui.TextColored(ImGuiColors.DalamudGrey, "—");
            }

            // Last run
            ImGui.TableNextColumn();
            if (c.LastRunAt == DateTime.MinValue)
                ImGui.TextColored(ImGuiColors.DalamudGrey, "never");
            else
            {
                ImGui.TextUnformatted(Ago(c.LastRunAt));
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"{c.LastRunAt:MMM d, HH:mm}\nSent {c.LastRunSent:N0} gil");
            }

            // Source
            ImGui.TableNextColumn();
            ImGui.TextColored(
                c.Source == "Manual" ? ImGuiColors.DalamudGrey : ImGuiColors.DalamudViolet,
                c.Source);

            // Move / remove
            ImGui.TableNextColumn();
            using (ImRaii.Disabled(i == 0))
            {
                if (ImGui.ArrowButton($"##up{id}", ImGuiDir.Up)) moveUp = i;
            }
            ImGui.SameLine(0, 2);
            using (ImRaii.Disabled(i == Cfg.Characters.Count - 1))
            {
                if (ImGui.ArrowButton($"##dn{id}", ImGuiDir.Down)) moveDown = i;
            }
            ImGui.SameLine(0, 2);
            using (ImRaii.PushId($"rm{id}"))
            {
                if (ImGuiComponents.IconButton(FontAwesomeIcon.Trash))
                    remove = c;
            }
        }

        if (moveUp > 0) Swap(moveUp, moveUp - 1);
        if (moveDown >= 0 && moveDown < Cfg.Characters.Count - 1) Swap(moveDown, moveDown + 1);

        if (remove != null)
        {
            Cfg.Characters.Remove(remove);
            Renumber();
            Cfg.Save();
        }
    }

    private const int ColOrder = 0;

    private void Swap(int a, int b)
    {
        (Cfg.Characters[a], Cfg.Characters[b]) = (Cfg.Characters[b], Cfg.Characters[a]);
        Renumber();

        // A hand-made move only sticks if we're not under an active sort, which
        // would just re-sort it away on the next frame. Switch to the manual column
        // and keep the arrangement the user is looking at.
        Cfg.SortColumn = ColOrder;
        Cfg.SortAscending = true;

        Cfg.Save();
    }

    // Rewrite Order to match current list positions, so the manual order survives
    // a round-trip through any other sort.
    private void Renumber()
    {
        for (var i = 0; i < Cfg.Characters.Count; i++)
            Cfg.Characters[i].Order = i;
    }

    private static string Abbrev(long gil) => gil switch
    {
        >= 1_000_000 => $"{gil / 1_000_000.0:0.#}M",
        >= 1_000 => $"{gil / 1_000.0:0.#}k",
        _ => gil.ToString(),
    };

    private static string Ago(DateTime t)
    {
        if (t == DateTime.MinValue) return "never";
        var d = DateTime.Now - t;
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes}m ago";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours}h ago";
        if (d.TotalDays < 30) return $"{(int)d.TotalDays}d ago";
        return t.ToString("MMM d");
    }

    private void AddManual(string name, string world)
    {
        if (Exists(name, world)) return;
        Cfg.Characters.Add(new CharEntry
        {
            Cid = 0,
            Name = name,
            World = world,
            Enabled = true,
            Source = "Manual",
        });
        Sort();
        Cfg.Save();
    }

    private bool Exists(string name, string world) =>
        Cfg.Characters.Any(x =>
            string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.World, world, StringComparison.OrdinalIgnoreCase));

    // Pulls from whichever AR tabs are ticked, mirroring AR's own filters.
    // Additive only: never removes, never duplicates.
    private string Import()
    {
        PluginIpc.LastError = "";

        var cids = PluginIpc.GetRegisteredCharacters();
        var read = 0;      // OfflineCharacterData we could actually read
        var matched = 0;   // passed the tab filter
        var added = 0;
        var already = 0;

        foreach (var cid in cids)
        {
            var info = PluginIpc.GetArCharInfo(cid);
            if (info == null) continue;
            read++;

            var inRet = Cfg.ImportFromRetainers && info.InRetainersTab;
            var inDep = Cfg.ImportFromDeployables && info.InDeployablesTab;
            if (!inRet && !inDep) continue;
            matched++;

            var source = inRet && inDep ? "Both" : inRet ? "Retainers" : "Deployables";

            // Match on CID first, falling back to name@world so a hand-added row
            // gets adopted (and labelled) rather than duplicated.
            var existing = Cfg.Characters.FirstOrDefault(x => x.Cid != 0 && x.Cid == cid)
                        ?? Cfg.Characters.FirstOrDefault(x =>
                               string.Equals(x.Name, info.Name, StringComparison.OrdinalIgnoreCase) &&
                               string.Equals(x.World, info.World, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                existing.Cid = cid;
                existing.Name = info.Name;
                existing.World = info.World;
                existing.Source = source;
                ApplyArData(existing, info);
                already++;
                continue;
            }

            var entry = new CharEntry
            {
                Cid = cid,
                Name = info.Name,
                World = info.World,
                Enabled = true,
                Source = source,
                Order = Cfg.Characters.Count + added,
            };
            ApplyArData(entry, info);
            Cfg.Characters.Add(entry);
            added++;
        }

        Sort();
        Cfg.Save();

        // Report every stage, so a zero at any point says exactly what broke:
        // no CIDs = IPC down; CIDs but no reads = reflection/signature mismatch;
        // reads but no matches = the tab filter excluded everyone.
        if (added > 0 || already > 0)
            return $"Added {added}, updated {already}.";

        var why = $"{cids.Count} CIDs, {read} read, {matched} matched.";
        if (!string.IsNullOrEmpty(PluginIpc.LastError))
            why += $"\n{PluginIpc.LastError}";
        return why;
    }

    // Copy the bits AR already knows about onto a roster row.
    private static void ApplyArData(CharEntry e, ArCharInfo info)
    {
        if (info.Gil > 0 || e.LastSeenGil == 0)
        {
            e.LastSeenGil = info.Gil;
            e.LastSeenGilAt = DateTime.Now;
        }

        var (dc, region) = TradeEngine.ResolveWorld(e.World);
        e.DataCenter = dc;
        e.Region = region;
    }

    // Pull fresh gil figures from AR without re-importing anyone. AR updates its
    // Gil field every time it logs in to run submarines, so this is usually current
    // without Consolidator ever having visited the character.
    private string RefreshFromAr()
    {
        var updated = 0;

        foreach (var c in Cfg.Characters)
        {
            if (c.Cid == 0) continue;   // hand-added, AR doesn't know them
            var info = PluginIpc.GetArCharInfo(c.Cid);
            if (info == null) continue;

            ApplyArData(c, info);
            updated++;
        }

        Cfg.Save();
        return updated == 0
            ? "Nothing to refresh."
            : $"Refreshed {updated} character(s).";
    }

    // Reads the header the user clicked and reorders the underlying list, so the
    // sort persists into the run queue rather than being cosmetic.
    private unsafe void ApplySort()
    {
        var specs = ImGui.TableGetSortSpecs();
        if (specs.Handle == null) return;
        if (!specs.SpecsDirty) return;

        if (specs.SpecsCount > 0)
        {
            var s = specs.Specs[0];
            Cfg.SortColumn = s.ColumnIndex;
            Cfg.SortAscending = s.SortDirection == ImGuiSortDirection.Ascending;
            Sort();
            Cfg.Save();
        }

        specs.SpecsDirty = false;
    }

    private void Sort()
    {
        var asc = Cfg.SortAscending;

        IOrderedEnumerable<CharEntry> q = Cfg.SortColumn switch
        {
            // 0 = manual order — this is the one the run actually follows.
            0 => asc ? Cfg.Characters.OrderBy(x => x.Order)
                     : Cfg.Characters.OrderByDescending(x => x.Order),
            1 => asc ? Cfg.Characters.OrderByDescending(x => x.Enabled)
                     : Cfg.Characters.OrderBy(x => x.Enabled),
            2 => asc ? Cfg.Characters.OrderBy(x => x.Name)
                     : Cfg.Characters.OrderByDescending(x => x.Name),
            3 => asc ? Cfg.Characters.OrderBy(x => x.World)
                     : Cfg.Characters.OrderByDescending(x => x.World),
            // Region, then DC, then world — so a region sort groups sensibly.
            4 => asc ? Cfg.Characters.OrderBy(x => x.Region).ThenBy(x => x.DataCenter)
                     : Cfg.Characters.OrderByDescending(x => x.Region).ThenByDescending(x => x.DataCenter),
            // Richest first by default; that's the useful direction.
            5 => asc ? Cfg.Characters.OrderByDescending(x => x.LastSeenGil)
                     : Cfg.Characters.OrderBy(x => x.LastSeenGil),
            // Never-run first by default, so stale characters surface.
            6 => asc ? Cfg.Characters.OrderBy(x => x.LastRunAt)
                     : Cfg.Characters.OrderByDescending(x => x.LastRunAt),
            7 => asc ? Cfg.Characters.OrderBy(x => x.Source)
                     : Cfg.Characters.OrderByDescending(x => x.Source),
            _ => Cfg.Characters.OrderBy(x => x.Order),
        };

        // Stable tie-break so equal keys don't shuffle between frames.
        Cfg.Characters = q.ThenBy(x => x.World).ThenBy(x => x.Name).ToList();

        // The list order IS the run order — what you see on this tab is what runs.
        // So every sort commits its positions, not just the manual one.
        Renumber();
    }

    // ---------------- settings ----------------

    private void DrawSettingsTab()
    {
        ImGui.Spacing();

        ImGui.TextColored(ImGuiColors.DalamudViolet, "Receiving character");
        ImGui.Spacing();

        ImGui.SetNextItemWidth(200);
        if (ImGui.InputText("Name##main", ref Cfg.MainName, 64)) Cfg.Save();
        ImGui.SameLine();
        ImGui.SetNextItemWidth(140);
        if (ImGui.InputText("World##main", ref Cfg.MainWorld, 32)) Cfg.Save();

        if (Player.Available)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Use current"))
            {
                Cfg.MainName = TradeEngine.PlayerName;
                Cfg.MainWorld = TradeEngine.HomeWorldName;
                Cfg.Save();
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextColored(ImGuiColors.DalamudViolet, "Destination");
        ImGui.Spacing();

        ImGui.SetNextItemWidth(200);
        if (ImGui.InputText("World##target", ref Cfg.TargetWorld, 32)) Cfg.Save();
        ImGuiComponents.HelpMarker("World every alt travels to before trading. e.g. Sophia");

        ImGui.SetNextItemWidth(200);
        if (ImGui.InputText("Destination", ref Cfg.Destination, 64)) Cfg.Save();
        ImGuiComponents.HelpMarker(
            "Passed straight to Lifestream, exactly as if you typed it after /li.\n" +
            "So \"ul'dah\" behaves the same as typing /li ul'dah.\n" +
            "Accepts city names, aethernet shard names, etc.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextColored(ImGuiColors.DalamudViolet, "Behaviour");
        ImGui.Spacing();

        if (ImGui.Checkbox("Return to FC after each character", ref Cfg.ReturnToFc)) Cfg.Save();

        if (Cfg.ReturnToFc)
        {
            ImGui.Indent();

            if (ImGui.Checkbox("Walk inside the house before logging out", ref Cfg.EnterHouse))
                Cfg.Save();
            ImGuiComponents.HelpMarker(
                "Off: teleport to the FC and log out on the plot.\n\n" +
                "On: also walk to the door and go inside. If you've recorded a path\n" +
                "for this house in Lifestream, that path is used; otherwise the door\n" +
                "is found and walked to with vnavmesh.");

            if (ImGui.Checkbox("Log out at the end", ref Cfg.LogoutAtEnd)) Cfg.Save();
            ImGuiComponents.HelpMarker(
                "Switching characters already logs the previous one out, so this only\n" +
                "affects the final character in the run — without it they're left\n" +
                "standing there still logged in.");

            ImGui.Unindent();
        }

        if (ImGui.Checkbox("Skip characters whose last known gil is at or below the floor",
                           ref Cfg.SkipByCachedGil)) Cfg.Save();
        ImGuiComponents.HelpMarker(
            "Uses the gil figure AutoRetainer already has, so empty characters are\n" +
            "skipped without logging in at all — much faster on a big roster.\n\n" +
            "The tradeoff: that figure is only as fresh as AR's last visit. If a\n" +
            "character earned gil since, this would skip them anyway. The live check\n" +
            "after login is unaffected and still always correct.\n\n" +
            "Off by default. Turn it on once your gil figures look current.");

        if (Cfg.SkipByCachedGil)
        {
            ImGui.Indent();
            ImGui.SetNextItemWidth(120);
            if (ImGui.InputInt("Ignore figures older than (hours)", ref Cfg.CachedGilMaxAgeHours))
            {
                Cfg.CachedGilMaxAgeHours = Math.Max(0, Cfg.CachedGilMaxAgeHours);
                Cfg.Save();
            }
            ImGuiComponents.HelpMarker(
                "A stale figure isn't trusted — those characters get logged into and\n" +
                "checked properly instead. 0 = trust any figure, however old.");
            ImGui.Unindent();
        }

        if (ImGui.Checkbox("Stop the whole run on any error", ref Cfg.StopOnError)) Cfg.Save();
        if (ImGui.Checkbox("Dry run (do everything except confirm the trade)", ref Cfg.NoOp)) Cfg.Save();
        ImGuiComponents.HelpMarker("Strongly recommended for your first run.");

        if (ImGui.Checkbox("Crash protection", ref Cfg.CrashProtection)) Cfg.Save();
        ImGuiComponents.HelpMarker(
            "Saves progress after each character, so if the game crashes you can pick\n" +
            "up where you left off instead of starting over.\n\n" +
            "The character that was in progress gets redone from the start — after a\n" +
            "crash mid-trade there's no way to know whether it went through. That's\n" +
            "safe, because gil is always re-read from the game: if they already hit\n" +
            "the floor, they're just skipped.");

        if (Cfg.Run.Active)
        {
            ImGui.Indent();
            ImGui.TextColored(ImGuiColors.DalamudGrey,
                $"Saved run: {Cfg.Run.Index}/{Cfg.Run.Queue.Count} done, {Cfg.Run.TotalSent:N0} gil.");
            ImGui.SameLine();
            if (ImGui.SmallButton("Clear saved run"))
            {
                Sweep.DiscardInterrupted();
            }
            ImGui.Unindent();
        }

        ImGui.Spacing();
        if (ImGui.TreeNode("Timeouts (seconds)"))
        {
            ImGui.SetNextItemWidth(120);
            if (ImGui.InputInt("Login", ref Cfg.LoginTimeout)) Cfg.Save();
            ImGui.SetNextItemWidth(120);
            if (ImGui.InputInt("Travel", ref Cfg.TravelTimeout)) Cfg.Save();
            ImGui.SetNextItemWidth(120);
            if (ImGui.InputInt("Find receiver", ref Cfg.LocateMainTimeout)) Cfg.Save();
            ImGui.SetNextItemWidth(120);
            if (ImGui.InputInt("Walk to receiver", ref Cfg.ApproachTimeout)) Cfg.Save();
            ImGui.SetNextItemWidth(120);
            if (ImGui.InputInt("Trade", ref Cfg.TradeTimeout)) Cfg.Save();
            ImGui.SetNextItemWidth(120);
            if (ImGui.InputInt("FC return", ref Cfg.FcReturnTimeout)) Cfg.Save();
            ImGui.SetNextItemWidth(120);
            if (ImGui.InputInt("Enter house", ref Cfg.EnterHouseTimeout)) Cfg.Save();
            ImGui.TreePop();
        }

        ImGui.Spacing();
        if (ImGui.TreeNode("Settle delays (ms)"))
        {
            ImGui.SetNextItemWidth(120);
            if (ImGui.InputInt("After arriving", ref Cfg.ArrivalSettleMs, 250, 1000)) Cfg.Save();
            ImGuiComponents.HelpMarker(
                "Wait this long after a teleport before looking for the receiver.\n" +
                "Objects stream in over several frames after a zone change.");

            ImGui.SetNextItemWidth(120);
            if (ImGui.InputInt("Before trading", ref Cfg.TradeSettleMs, 250, 1000)) Cfg.Save();
            ImGuiComponents.HelpMarker(
                "The receiver must be continuously loaded, rendered and in range for\n" +
                "this long before /trade fires. Raise this if the first trade of a\n" +
                "character still fails.");

            ImGui.TreePop();
        }
    }

    // ---------------- items ----------------

    private string itemSearch = "";
    private List<(uint Id, string Name)> itemHits = new();

    private void DrawItemList()
    {
        if (!TradeEngine.ItemsAvailable)
        {
            ImGui.TextColored(ImGuiColors.DalamudRed, "Item trading is unavailable.");
            ImGui.PushTextWrapPos(ImGui.GetContentRegionAvail().X);
            ImGui.TextColored(ImGuiColors.DalamudGrey,
                (Plugin.P.ItemHook.Error ?? "The OfferItemTrade signature didn't resolve.")
                + "\nThis usually means a game patch moved it. Gil transfer is unaffected.");
            ImGui.PopTextWrapPos();
            return;
        }

        ImGui.TextColored(ImGuiColors.DalamudViolet, "Items");
        ImGui.Spacing();

        // Search by name; the sheet lookup is the only sane way to pick an item ID.
        ImGui.SetNextItemWidth(240);
        if (ImGui.InputTextWithHint("##itemsearch", "Search for an item to add...",
                                    ref itemSearch, 64))
        {
            itemHits = SearchItems(itemSearch);
        }

        if (itemHits.Count > 0)
        {
            using var box = ImRaii.Child("##itemhits", new Vector2(-1, 110), true);
            if (box)
            {
                foreach (var (id, name) in itemHits)
                {
                    if (ImGui.Selectable($"{name}##hit{id}"))
                    {
                        // id 0 is gil, which already has its own row above.
                        if (id != 0 && !Cfg.Items.Any(x => x.ItemId == id))
                        {
                            Cfg.Items.Add(new DepositItem
                            {
                                ItemId = id,
                                Label = name,
                                KeepAmount = 0,
                                Enabled = true,
                            });
                            Cfg.Save();
                        }
                        itemSearch = "";
                        itemHits.Clear();
                        break;
                    }
                }
            }
        }

        ImGui.Spacing();

        var items = Cfg.Items.Where(i => !i.IsGil).ToList();
        DepositItem? remove = null;

        if (items.Count == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey,
                "No items. Search above to add some.");
        }
        else
        {
            // Bound the table to whatever room is left and let it scroll, rather than
            // letting it run off the bottom of the window. Leaves space for the
            // armoury toggle and help text below.
            var reserved = ImGui.GetTextLineHeightWithSpacing() * 7;
            var avail = Math.Max(120f, ImGui.GetContentRegionAvail().Y - reserved);

            using (var table = ImRaii.Table("##items", 6,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY,
                new Vector2(-1, avail)))
            {
                if (table)
                {
                    ImGui.TableSetupColumn("On", ImGuiTableColumnFlags.WidthFixed, 28);
                    ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
                    ImGui.TableSetupColumn("HQ", ImGuiTableColumnFlags.WidthFixed, 32);
                    ImGui.TableSetupColumn("Keep", ImGuiTableColumnFlags.WidthFixed, 110);
                    ImGui.TableSetupColumn("Would send", ImGuiTableColumnFlags.WidthFixed, 80);
                    ImGui.TableSetupColumn("##rm", ImGuiTableColumnFlags.WidthFixed, 28);
                    ImGui.TableSetupScrollFreeze(0, 1);
                    ImGui.TableHeadersRow();

                    DrawItemRows(items, ref remove);
                }
            }
        }

        if (remove != null)
        {
            Cfg.Items.Remove(remove);
            Cfg.Save();
        }

        ImGui.Spacing();

        if (ImGui.Checkbox("Also take from the armoury", ref Cfg.IncludeArmoury)) Cfg.Save();
        ImGuiComponents.HelpMarker(
            "By default only the four main bags are searched.\n\n" +
            "With this on, the armoury chest is searched too. Useful for sweeping up\n" +
            "spare gear, but be careful what you put on the list — this runs unattended\n" +
            "across every character, and gear sitting in the armoury is exactly the sort\n" +
            "of thing you'd rather not discover was traded away.\n\n" +
            "Equipped gear is never touched, and untradeable items are always skipped.");

        if (Cfg.IncludeArmoury)
        {
            ImGui.Indent();
            ImGui.TextColored(ImGuiColors.DalamudOrange,
                "Armoury included — double-check the item list above.");
            ImGui.Unindent();
        }

        ImGui.Spacing();
        ImGui.TextColored(ImGuiColors.DalamudGrey,
            "Everything above the Keep amount is traded. Keep 0 sends the lot.\n" +
            "A trade window fits 5 item stacks, so large hauls take several passes.");
    }

    private static List<(uint, string)> SearchItems(string q)
    {
        var hits = new List<(uint, string)>();
        if (string.IsNullOrWhiteSpace(q) || q.Length < 3) return hits;

        try
        {
            var sheet = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>();
            if (sheet == null) return hits;

            foreach (var row in sheet)
            {
                var name = row.Name.ToString();
                if (string.IsNullOrEmpty(name)) continue;
                if (row.RowId == 0) continue;   // gil has its own row
                if (!row.IsUntradable && name.Contains(q, StringComparison.OrdinalIgnoreCase))
                {
                    hits.Add((row.RowId, name));
                    if (hits.Count >= 30) break;
                }
            }
        }
        catch { /* sheet miss */ }

        return hits;
    }

    // ---------------- send ----------------

    private void DrawSendTab()
    {
        ImGui.Spacing();
        ImGui.PushTextWrapPos(ImGui.GetContentRegionAvail().X);
        ImGui.TextColored(ImGuiColors.DalamudGrey,
            "What each character hands over. Anything above the Keep amount is traded; "
            + "the rest stays with them.");
        ImGui.PopTextWrapPos();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // --- gil ---
        ImGui.TextColored(ImGuiColors.DalamudViolet, "Gil");
        ImGui.Spacing();

        var gil = Cfg.Items.FirstOrDefault(i => i.IsGil);
        if (gil != null)
        {
            var on = gil.Enabled;
            if (ImGui.Checkbox("Send gil##sendgil", ref on)) { gil.Enabled = on; Cfg.Save(); }

            ImGui.SameLine(0, 20);
            ImGui.SetNextItemWidth(160);
            var keep = (int)gil.KeepAmount;
            if (ImGui.InputInt("Keep on each character", ref keep, 1000, 10000))
            {
                gil.KeepAmount = Math.Max(0, keep);
                Cfg.Save();
            }
            ImGuiComponents.HelpMarker(
                "Everything above this is sent. Set 10000 to leave 10,000 gil behind.\n" +
                "A trade window caps at 1,000,000 gil, so richer alts do several passes\n" +
                "automatically.");
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // --- items ---
        DrawItemList();
    }


    private void DrawItemRows(List<DepositItem> items, ref DepositItem? remove)
    {
        foreach (var it in items)
        {
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            var on = it.Enabled;
            if (ImGui.Checkbox($"##ion{it.ItemId}", ref on)) { it.Enabled = on; Cfg.Save(); }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(it.Label);

            ImGui.TableNextColumn();
            var hq = it.Hq;
            if (ImGui.Checkbox($"##ihq{it.ItemId}", ref hq)) { it.Hq = hq; Cfg.Save(); }

            ImGui.TableNextColumn();
            ImGui.SetNextItemWidth(-1);
            var keep = (int)it.KeepAmount;
            if (ImGui.InputInt($"##ik{it.ItemId}", ref keep, 1, 10))
            {
                it.KeepAmount = Math.Max(0, keep);
                Cfg.Save();
            }

            // Live count on whoever's logged in, so you can sanity-check a keep
            // amount against a real inventory rather than guessing.
            ImGui.TableNextColumn();
            if (Player.Available)
            {
                var have = Inventory.CountOf(it.ItemId, it.Hq, Cfg.IncludeArmoury);
                var spare = have - it.KeepAmount;
                if (have == 0)
                    ImGui.TextColored(ImGuiColors.DalamudGrey, "—");
                else if (spare > 0)
                    ImGui.TextColored(ImGuiColors.HealerGreen, $"{spare:N0}");
                else
                    ImGui.TextColored(ImGuiColors.DalamudGrey, "0");

                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip($"This character has {have:N0}.\n" +
                                     $"Would send {Math.Max(0, spare):N0}.");
            }
            else
            {
                ImGui.TextColored(ImGuiColors.DalamudGrey, "—");
            }

            ImGui.TableNextColumn();
            using (ImRaii.PushId($"irm{it.ItemId}"))
            {
                if (ImGuiComponents.IconButton(FontAwesomeIcon.Trash))
                    remove = it;
            }
        }
    }

    // ---------------- log ----------------

    private void DrawLogTab()
    {
        ImGui.Spacing();
        if (ImGui.SmallButton("Clear")) Sweep.Log.Clear();
        ImGui.Spacing();

        using var child = ImRaii.Child("##log", new Vector2(-1, -1), true);
        if (!child) return;

        foreach (var line in Sweep.Log)
            DrawLogLine(line);

        if (Sweep.Running && ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 40)
            ImGui.SetScrollHereY(1.0f);
    }

    private static void DrawLogLine(SweepLogLine l)
    {
        ImGui.TextColored(ImGuiColors.DalamudGrey, l.At.ToString("HH:mm:ss"));
        ImGui.SameLine();
        if (!string.IsNullOrEmpty(l.Character))
        {
            ImGui.TextColored(ImGuiColors.DalamudViolet, l.Character);
            ImGui.SameLine();
        }
        if (l.IsError) ImGui.TextColored(ImGuiColors.DalamudRed, l.Message);
        else ImGui.TextUnformatted(l.Message);
    }
}
