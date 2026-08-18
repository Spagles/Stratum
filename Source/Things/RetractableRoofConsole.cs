using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

using SolarWeb.Stratum.DefModExtensions;
using SolarWeb.Stratum.MapComponents;
using SolarWeb.Stratum.Stats;

namespace SolarWeb.Stratum.Things;

public enum RetractableRoofOpeningStyle
{
  Iris,
  SplitX,
  SplitY,
  Quadrant,
  LinearX,
  LinearZ,
  ConsoleRadial
}

public class RetractableRoofConsole : Building
{
  public bool isOpeningRequested;
  public bool isTransitioning;
  public bool jobPending;
  public int currentRingIndex;
  public float transitionProgress;
  public RetractableRoofOpeningStyle openingStyle = RetractableRoofOpeningStyle.Iris;

  private List<List<IntVec3>> transitionRings = [];
  private HashSet<IntVec3> canopyCells = [];
  private Vector3 roomCentroid;
  private int irisRingCount = 1;
  private CompPowerTrader? powerComp;
  private RetractableRoofTracker? cachedTracker;
  private RoofIntegrityGrid? cachedIntegrityGrid;
  private RetractableRoofAnimator? cachedAnimator;
  private readonly HashSet<IntVec3> simulatedRetracted = [];
  private readonly HashSet<IntVec3> cellsToCheck = [];
  private readonly HashSet<IntVec3> blockedCells = [];
  private bool canopyMapped;
  private readonly Queue<IntVec3> supportCheckQueue = new();
  private readonly HashSet<IntVec3> supportCheckVisited = [];

  public override void SpawnSetup(Map map, bool respawningAfterLoad)
  {
    base.SpawnSetup(map, respawningAfterLoad);
    powerComp = GetComp<CompPowerTrader>();
    cachedTracker = map.GetComponent<RetractableRoofTracker>();
    cachedIntegrityGrid = map.GetComponent<RoofIntegrityGrid>();
    cachedAnimator = map.GetComponent<RetractableRoofAnimator>();
    canopyMapped = false;
  }

  /// <summary>
  /// Builds the ring/cell set once, on first use. The gizmos now read the canopy's real state, so
  /// canopyCells must be populated before the console is first drawn — but not during SpawnSetup,
  /// which runs before regions and rooms have been rebuilt. Driven from the UI paths as well as
  /// Tick so it still resolves while the game is paused.
  /// </summary>
  private void EnsureCanopyMapped()
  {
    if (canopyMapped) return;
    if (Map?.roofGrid == null) return;

    canopyMapped = true;
    RecalculateRings();
    RecoverOrphanedCells();
  }

  public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
  {
    base.DeSpawn(mode);
    cachedTracker = null;
    cachedIntegrityGrid = null;
    cachedAnimator = null;
  }

  public override void ExposeData()
  {
    base.ExposeData();
    Scribe_Values.Look(ref isOpeningRequested, "isOpeningRequested", false);
    Scribe_Values.Look(ref isTransitioning, "isTransitioning", false);
    Scribe_Values.Look(ref jobPending, "jobPending", false);
    Scribe_Values.Look(ref currentRingIndex, "currentRingIndex", 0);
    Scribe_Values.Look(ref transitionProgress, "transitionProgress", 0f);
    Scribe_Values.Look(ref openingStyle, "openingStyle", RetractableRoofOpeningStyle.Iris);
  }

  public static string GetStyleLabel(RetractableRoofOpeningStyle style) => style switch
  {
    RetractableRoofOpeningStyle.Iris => "Stratum_Style_Iris".Translate(),
    RetractableRoofOpeningStyle.SplitX => "Stratum_Style_SplitX".Translate(),
    RetractableRoofOpeningStyle.SplitY => "Stratum_Style_SplitY".Translate(),
    RetractableRoofOpeningStyle.Quadrant => "Stratum_Style_Quadrant".Translate(),
    RetractableRoofOpeningStyle.LinearX => "Stratum_Style_LinearX".Translate(),
    RetractableRoofOpeningStyle.LinearZ => "Stratum_Style_LinearZ".Translate(),
    RetractableRoofOpeningStyle.ConsoleRadial => "Stratum_Style_ConsoleRadial".Translate(),
    _ => style.ToString()
  };

  public static string GetStyleDesc(RetractableRoofOpeningStyle style) => style switch
  {
    RetractableRoofOpeningStyle.Iris => "Stratum_Style_Iris_Desc".Translate(),
    RetractableRoofOpeningStyle.SplitX => "Stratum_Style_SplitX_Desc".Translate(),
    RetractableRoofOpeningStyle.SplitY => "Stratum_Style_SplitY_Desc".Translate(),
    RetractableRoofOpeningStyle.Quadrant => "Stratum_Style_Quadrant_Desc".Translate(),
    RetractableRoofOpeningStyle.LinearX => "Stratum_Style_LinearX_Desc".Translate(),
    RetractableRoofOpeningStyle.LinearZ => "Stratum_Style_LinearZ_Desc".Translate(),
    RetractableRoofOpeningStyle.ConsoleRadial => "Stratum_Style_ConsoleRadial_Desc".Translate(),
    _ => string.Empty
  };

  public enum CanopyState
  {
    Extended,
    Partial,
    Retracted
  }

  /// <summary>
  /// The canopy's actual state, read from the roof grid. The gizmo is driven from this rather
  /// than from <see cref="isOpeningRequested"/> so that a partially retracted canopy — the
  /// result of blocked cells or an interrupted transition — can always be moved either way.
  /// </summary>
  public CanopyState GetCanopyState()
  {
    if (Map?.roofGrid == null || canopyCells.Count == 0) return CanopyState.Extended;

    int openCount = 0;
    foreach (var c in canopyCells)
    {
      if (Map.roofGrid.RoofAt(c) == null) openCount++;
    }

    if (openCount == 0) return CanopyState.Extended;
    if (openCount == canopyCells.Count) return CanopyState.Retracted;
    return CanopyState.Partial;
  }

  private string? BlockedReason()
  {
    if (powerComp != null && !powerComp.PowerOn)
    {
      var flick = GetComp<CompFlickable>();
      if (flick != null && !flick.SwitchIsOn) return "Stratum_CanopySwitchedOff".Translate();
      return "Stratum_CanopyNoPower".Translate();
    }

    if (canopyCells.Count == 0) return "Stratum_CanopyNoCells".Translate();

    return null;
  }

  public override IEnumerable<Gizmo> GetGizmos()
  {
    foreach (var g in base.GetGizmos()) yield return g;

    EnsureCanopyMapped();

    if (isTransitioning)
    {
      yield return new Command_Action
      {
        defaultLabel = "Stratum_CancelTransition".Translate(),
        defaultDesc = "Stratum_CancelTransitionDesc".Translate(),
        icon = TexCommand.ClearPrioritizedWork,
        action = () =>
        {
          isTransitioning = false;
          jobPending = false;
        }
      };
      yield break;
    }

    yield return new Command_Action
    {
      defaultLabel = "Stratum_SelectCanopyStyle".Translate(),
      defaultDesc = "Stratum_SelectCanopyStyleDesc".Translate(),
      icon = ContentFinder<Texture2D>.Get("UI/Commands/ChangeStyle", false) ?? ContentFinder<Texture2D>.Get("UI/Commands/TryReconnect", true),
      action = () =>
      {
        var options = new List<FloatMenuOption>();
        foreach (RetractableRoofOpeningStyle s in System.Enum.GetValues(typeof(RetractableRoofOpeningStyle)))
        {
          string label = GetStyleLabel(s);
          if (s == openingStyle)
          {
            label = "✓ " + label;
          }
          var opt = new FloatMenuOption(label, () =>
          {
            if (openingStyle != s)
            {
              openingStyle = s;
              RecalculateRings();
            }
          });
          opt.tooltip = GetStyleDesc(s);
          options.Add(opt);
        }
        Find.WindowStack.Add(new FloatMenu(options));
      }
    };

    string? blocked = BlockedReason();
    var state = GetCanopyState();

    // Offer both directions when partially retracted so a blocked open or an interrupted
    // close is always recoverable.
    if (state != CanopyState.Retracted)
    {
      yield return MakeTransitionCommand(opening: true, blocked);
    }

    if (state != CanopyState.Extended)
    {
      yield return MakeTransitionCommand(opening: false, blocked);
    }
  }

  private Command_Action MakeTransitionCommand(bool opening, string? blocked)
  {
    var cmd = new Command_Action
    {
      defaultLabel = opening ? "Stratum_OpenCanopy".Translate() : "Stratum_CloseCanopy".Translate(),
      defaultDesc = opening ? "Stratum_OpenCanopyDesc".Translate() : "Stratum_CloseCanopyDesc".Translate(),
      icon = opening ? TexCommand.Install : TexCommand.ForbidOff,
      action = () =>
      {
        isOpeningRequested = opening;
        jobPending = true;
      }
    };

    if (blocked != null) cmd.Disable(blocked);
    return cmd;
  }

  public float GetActivePowerDraw()
  {
    return Mathf.Max(500f, 50f * canopyCells.Count);
  }

  public float GetAvailablePowerRatio(float desiredPower)
  {
    if (powerComp == null) return 1f;

    var flick = GetComp<CompFlickable>();
    if (flick != null && !flick.SwitchIsOn) return 0f;

    var breakdown = GetComp<CompBreakdownable>();
    if (breakdown != null && breakdown.BrokenDown) return 0f;

    var net = powerComp.PowerNet;
    if (net == null) return 0f;
    if (desiredPower <= 0f) return 1f;

    float totalGen = 0f;
    float otherDemand = 0f;

    if (net.powerComps != null)
    {
      foreach (var comp in net.powerComps)
      {
        if (comp == null || comp.parent == null || !comp.parent.Spawned) continue;

        float output = comp.PowerOutput;
        if (output > 0f)
        {
          totalGen += output;
        }
        else if (comp != powerComp && output < 0f)
        {
          otherDemand += -output;
        }
      }
    }

    float surplus = Mathf.Max(0f, totalGen - otherDemand);
    if (surplus >= desiredPower) return 1f;

    float deficit = desiredPower - surplus;
    float storedEnergy = net.CurrentStoredEnergy();

    // Energy buffer required to sustain the deficit over a 600-tick (10s) transition window (600 / 60000 = 0.01 Wd per Watt)
    float requiredEnergy = deficit / 100f;
    float batterySupportedWattage = 0f;

    if (requiredEnergy > 0f && storedEnergy > 0f)
    {
      float batteryFactor = Mathf.Clamp01(storedEnergy / requiredEnergy);
      batterySupportedWattage = deficit * batteryFactor;
    }

    float availablePower = surplus + batterySupportedWattage;
    float ratio = availablePower / desiredPower;
    return ratio < 0.05f ? 0f : Mathf.Clamp01(ratio);
  }

  public override string GetInspectString()
  {
    if (Map == null || Map.roofGrid == null) return base.GetInspectString();
    string str = base.GetInspectString();

    EnsureCanopyMapped();

    if (canopyCells.Count == 0 && this.IsHashIntervalTick(60))
    {
      RecalculateRings();
    }

    float desiredPower = GetActivePowerDraw();
    float powerRatio = isTransitioning ? GetAvailablePowerRatio(desiredPower) : 1f;

    string statusText;
    if (isTransitioning)
    {
      if (powerRatio <= 0f)
      {
        statusText = "Stratum_CanopyPausedNoPower".Translate();
      }
      else if (powerRatio < 0.99f)
      {
        int pct = Mathf.RoundToInt(powerRatio * 100f);
        statusText = isOpeningRequested
          ? "Stratum_CanopyOpeningSlowed".Translate(pct)
          : "Stratum_CanopyClosingSlowed".Translate(pct);
      }
      else
      {
        statusText = (isOpeningRequested ? "Stratum_CanopyOpening" : "Stratum_CanopyClosing").Translate();
      }
    }
    else if (canopyCells.Count > 0)
    {
      statusText = (GetCanopyState() switch
      {
        CanopyState.Retracted => "Stratum_CanopyRetracted",
        CanopyState.Extended => "Stratum_CanopyExtended",
        _ => "Stratum_CanopyPartiallyRetracted"
      }).Translate();
    }
    else
    {
      statusText = "Stratum_CanopyIdle".Translate();
    }

    if (!str.NullOrEmpty()) str += "\n";
    str += "Stratum_CanopyStatus".Translate(statusText) + "\n";
    str += "Stratum_CanopyStyle".Translate(GetStyleLabel(openingStyle)) + "\n";
    str += "Stratum_ConnectedTiles".Translate(canopyCells.Count) + "\n";

    if (isTransitioning && powerRatio < 0.99f && powerRatio > 0f)
    {
      float actualDraw = Mathf.Round(desiredPower * powerRatio);
      str += "Stratum_TransitionPowerThrottled".Translate(actualDraw, desiredPower);
    }
    else
    {
      str += "Stratum_TransitionPower".Translate(desiredPower);
    }

    // Without these, a canopy that has silently stalled looks identical to one that is simply idle.
    if (jobPending && !isTransitioning)
    {
      str += "\n" + "Stratum_CanopyAwaitingColonist".Translate();
    }

    if (blockedCells.Count > 0 && !isTransitioning)
    {
      str += "\n" + "Stratum_CanopyBlockedCells".Translate(blockedCells.Count);
    }

    return str;
  }

  public override void DrawExtraSelectionOverlays()
  {
    base.DrawExtraSelectionOverlays();

    if (canopyCells.Count > 0)
    {
      GenDraw.DrawFieldEdges(canopyCells.ToList(), Color.cyan, null);
    }
  }

  public void InitiateTransition()
  {
    if (Map == null) return;
    RecalculateRings();
    blockedCells.Clear();
    isTransitioning = true;
    jobPending = false;
    transitionProgress = 9999f; // Trigger first ring immediately on next tick

    if (isOpeningRequested)
    {
      currentRingIndex = 0;
      while (currentRingIndex < transitionRings.Count && RingIsAlreadyOpen(transitionRings[currentRingIndex]))
        currentRingIndex++;
    }
    else
    {
      currentRingIndex = transitionRings.Count - 1;
      while (currentRingIndex >= 0 && RingIsAlreadyClosed(transitionRings[currentRingIndex]))
        currentRingIndex--;
    }
  }

  private bool IsRetractableRoof(RoofDef roof)
  {
    return roof != null && roof.GetModExtension<BuildableRoofExtension>()?.isRetractable == true;
  }

  private bool RingIsAlreadyOpen(List<IntVec3> ring)
  {
    if (Map == null || Map.roofGrid == null) return true;
    foreach (var cell in ring)
    {
      var roof = Map.roofGrid.RoofAt(cell);
      if (IsRetractableRoof(roof))
        return false;
    }
    return true;
  }

  private bool RingIsAlreadyClosed(List<IntVec3> ring)
  {
    if (Map == null || Map.roofGrid == null) return true;
    foreach (var cell in ring)
    {
      var roof = Map.roofGrid.RoofAt(cell);
      if (!IsRetractableRoof(roof))
        return false;
    }
    return true;
  }

  private void RecalculateRings()
  {
    var tracker = cachedTracker;
    HashSet<IntVec3> validCells;
    Vector3 consolePos = Position.ToVector3Shifted();

    var room = this.GetRoom();
    if (room == null)
    {
      transitionRings.Clear();
      return;
    }

    var roomValidCells = new HashSet<IntVec3>();
    foreach (var cell in room.Cells)
    {
      int idx = Map.cellIndices.CellToIndex(cell);
      var r = Map.roofGrid.RoofAt(cell);

      bool isValid = false;
      if (IsRetractableRoof(r)) isValid = true;
      else if (tracker != null && tracker.IsRetracted(idx)) isValid = true;

      if (isValid)
      {
        roomValidCells.Add(cell);
      }
    }

    if (roomValidCells.Count == 0)
    {
      transitionRings.Clear();
      return;
    }

    if (!room.TouchesMapEdge)
    {
      validCells = roomValidCells;
    }
    else
    {
      var unvisited = new HashSet<IntVec3>(roomValidCells);
      var components = new List<HashSet<IntVec3>>();

      while (unvisited.Count > 0)
      {
        var comp = new HashSet<IntVec3>();
        var queue = new Queue<IntVec3>();

        var start = unvisited.First();
        queue.Enqueue(start);
        unvisited.Remove(start);
        comp.Add(start);

        while (queue.Count > 0)
        {
          var curr = queue.Dequeue();
          for (int i = 0; i < 8; i++)
          {
            IntVec3 n = curr + GenAdj.AdjacentCells[i];
            if (unvisited.Contains(n))
            {
              unvisited.Remove(n);
              comp.Add(n);
              queue.Enqueue(n);
            }
          }
        }
        components.Add(comp);
      }

      HashSet<IntVec3> closestComp = components[0];
      float minDist = float.MaxValue;

      foreach (var comp in components)
      {
        float dist = float.MaxValue;
        foreach (var c in comp)
        {
          float d = Vector3.Distance(c.ToVector3Shifted(), consolePos);
          if (d < dist) dist = d;
        }
        if (dist < minDist)
        {
          minDist = dist;
          closestComp = comp;
        }
      }

      validCells = closestComp;
    }

    float sumX = 0;
    float sumZ = 0;
    foreach (var c in validCells)
    {
      sumX += c.x;
      sumZ += c.z;
    }
    roomCentroid = new Vector3(sumX / validCells.Count, 0f, sumZ / validCells.Count);
    Vector3 centroid = roomCentroid;

    var irisDict = new SortedDictionary<float, List<IntVec3>>();
    foreach (var c in validCells)
    {
      float dist = Vector3.Distance(c.ToVector3Shifted(), centroid);
      float roundedDist = Mathf.Round(dist * 2f) / 2f;
      if (!irisDict.TryGetValue(roundedDist, out var list))
      {
        list = [];
        irisDict[roundedDist] = list;
      }
      list.Add(c);
    }
    irisRingCount = Mathf.Max(1, irisDict.Count);

    if (openingStyle == RetractableRoofOpeningStyle.Iris)
    {
      transitionRings = irisDict.Values.ToList();
    }
    else
    {
      var styleDict = new SortedDictionary<float, List<IntVec3>>();
      foreach (var c in validCells)
      {
        float key = openingStyle switch
        {
          RetractableRoofOpeningStyle.SplitX => Mathf.Abs(c.x + 0.5f - centroid.x),
          RetractableRoofOpeningStyle.SplitY => Mathf.Abs(c.z + 0.5f - centroid.z),
          RetractableRoofOpeningStyle.Quadrant => Mathf.Max(Mathf.Abs(c.x + 0.5f - centroid.x), Mathf.Abs(c.z + 0.5f - centroid.z)),
          RetractableRoofOpeningStyle.LinearX => (float)c.x,
          RetractableRoofOpeningStyle.LinearZ => (float)c.z,
          RetractableRoofOpeningStyle.ConsoleRadial => Mathf.Round(Vector3.Distance(c.ToVector3Shifted(), consolePos) * 2f) / 2f,
          _ => Mathf.Round(Vector3.Distance(c.ToVector3Shifted(), centroid) * 2f) / 2f
        };

        if (!styleDict.TryGetValue(key, out var list))
        {
          list = [];
          styleDict[key] = list;
        }
        list.Add(c);
      }
      transitionRings = styleDict.Values.ToList();
    }

    canopyCells = validCells;
  }

  protected override void Tick()
  {
    base.Tick();
    if (Map == null || !Spawned || Map.roofGrid == null) return;

    try
    {
      // Before the isTransitioning check, so a save loaded mid-transition rebuilds its rings
      // and resumes rather than aborting on an empty ring list.
      EnsureCanopyMapped();

      if (!isTransitioning)
      {
        if (Find.Selector.IsSelected(this) && this.IsHashIntervalTick(60))
        {
          RecalculateRings();
        }

        if (powerComp != null)
        {
          powerComp.PowerOutput = -200f;
        }
        return;
      }

      float desiredPower = GetActivePowerDraw();
      float powerRatio = GetAvailablePowerRatio(desiredPower);

      if (powerComp != null)
      {
        if (powerRatio > 0f)
        {
          powerComp.PowerOutput = -Mathf.Max(50f, desiredPower * powerRatio);
        }
        else
        {
          powerComp.PowerOutput = -desiredPower;
        }
      }

      if (powerRatio <= 0f)
      {
        return;
      }

      if (transitionRings.Count == 0 ||
         (isOpeningRequested && currentRingIndex >= transitionRings.Count) ||
         (!isOpeningRequested && currentRingIndex < 0))
      {
        isTransitioning = false;
        return;
      }

      int animationDuration = 1; // Default minimum to prevent div by 0
      var tracker = cachedTracker;
      var currentRing = transitionRings[currentRingIndex];
      foreach (var cell in currentRing)
      {
        var rDef = Map.roofGrid.RoofAt(cell);

        if (rDef == null && tracker != null)
        {
          int index = Map.cellIndices.CellToIndex(cell);
          tracker.PeekOpenRoof(index, out rDef, out _, out _, out _);
        }

        if (rDef != null)
        {
          var ext = rDef.GetModExtension<BuildableRoofExtension>();
          if (ext != null && ext.isRetractable)
          {
            int ticks = 30;
            if (ext.buildableDef != null)
            {
              ticks = Mathf.RoundToInt(ext.buildableDef.GetStatValueAbstract(DefOf.StatDefOf.TransitionSpeed));
            }

            if (ticks > animationDuration)
            {
              animationDuration = ticks;
            }
          }
        }
      }

      int baseDelay = Mathf.Max(1, animationDuration / 3);
      int baseRingTicks = animationDuration + baseDelay;
      int targetTotalTicks = Mathf.Max(1, irisRingCount) * baseRingTicks;
      int ticksToNextRing = Mathf.Max(1, Mathf.RoundToInt((float)targetTotalTicks / transitionRings.Count));

      float speedMultiplier = powerRatio;
      transitionProgress += speedMultiplier;

      if (transitionProgress >= ticksToNextRing)
      {
        transitionProgress = 0f;

        var ring = transitionRings[currentRingIndex];
        int scaledAnimationDuration = Mathf.RoundToInt(animationDuration / Mathf.Max(0.1f, powerRatio));

        foreach (var cell in ring)
        {
          // Per-cell isolation: one bad cell must not abort the transition. That matters most
          // when closing, where bailing out mid-ring used to leave cells with neither a roof
          // nor a tracker record, orphaning them permanently.
          try
          {
            if (isOpeningRequested)
              RetractCell(cell, scaledAnimationDuration);
            else
              ExtendCell(cell, scaledAnimationDuration);
          }
          catch (System.Exception ex)
          {
            StratumLog.Error(
              $"Error {(isOpeningRequested ? "retracting" : "extending")} canopy cell {cell}: {ex}", once: true);
          }
        }

        DefOf.SoundDefOf.DropPod_Open?.PlayOneShot(new TargetInfo(Position, Map));

        if (isOpeningRequested)
          currentRingIndex++;
        else
          currentRingIndex--;
      }
    }
    catch (System.Exception ex)
    {
      StratumLog.Error($"Error in RetractableRoofConsole.Tick: {ex}");
      isTransitioning = false;
    }
  }

  /// <summary>
  /// Retracts one canopy cell. The roof is banked in the tracker before it leaves the grid, so
  /// the cell stays restorable from the moment it opens.
  /// </summary>
  private void RetractCell(IntVec3 cell, int animationDuration)
  {
    var roof = Map.roofGrid.RoofAt(cell);
    if (!IsRetractableRoof(roof)) return;

    if (BordersEmptyAir(cell))
    {
      NoteBlockedCell(cell, "it borders open air");
      return;
    }

    if (WouldCauseCollapse(cell))
    {
      NoteBlockedCell(cell, "retracting it would leave roof unsupported");
      return;
    }

    var integrityGrid = cachedIntegrityGrid;
    var stuff = integrityGrid?.GetStuff(cell);
    var tint = integrityGrid?.GetGlassTint(cell);
    var hp = integrityGrid?.GetHitPoints(cell) ?? (short)180;

    cachedTracker?.SaveOpenRoof(Map.cellIndices.CellToIndex(cell), roof, stuff, tint, hp);

    AddPanelAnimation(cell, roof, stuff, animationDuration, retracting: true);

    Map.roofGrid.SetRoof(cell, null);
    FleckMaker.ThrowAirPuffUp(cell.ToVector3Shifted(), Map);
  }

  /// <summary>
  /// Extends one canopy cell back. The tracker record is deliberately left in place here:
  /// RetractableRoofAnimator clears it only once the roof is actually back in the grid, so an
  /// interrupted close stays recoverable and re-issuing one is idempotent.
  /// </summary>
  private void ExtendCell(IntVec3 cell, int animationDuration)
  {
    var tracker = cachedTracker;
    if (tracker == null) return;

    int index = Map.cellIndices.CellToIndex(cell);
    if (!tracker.PeekOpenRoof(index, out var rDef, out var stuff, out var tint, out var hp)) return;

    if (Map.roofGrid.RoofAt(cell) != null)
    {
      // Something else roofed this cell while it was retracted. State has already converged,
      // so retire the record rather than leaving the cell looking retracted forever.
      tracker.ClearOpenRoof(index);
      return;
    }

    AddPanelAnimation(cell, rDef, stuff, animationDuration, retracting: false);

    var animator = cachedAnimator ?? Map.GetComponent<RetractableRoofAnimator>();
    if (animator != null)
    {
      animator.AddPendingRoof(cell, rDef, stuff, tint ?? Color.white, hp, animationDuration);
    }
    else
    {
      Map.roofGrid.SetRoof(cell, rDef);
      cachedIntegrityGrid?.InitializeRoof(cell, rDef, stuff, tint, hp);
      tracker.ClearOpenRoof(index);
      FleckMaker.ThrowAirPuffUp(cell.ToVector3Shifted(), Map);
    }
  }

  private bool BordersEmptyAir(IntVec3 cell)
  {
    for (int i = 0; i < 8; i++)
    {
      IntVec3 n = cell + GenAdj.AdjacentCells[i];
      if (!n.InBounds(Map)) continue;
      if (canopyCells.Contains(n)) continue;

      if (Map.roofGrid.RoofAt(n) == null && n.GetEdifice(Map) == null) return true;
    }
    return false;
  }

  /// <summary>True if retracting this cell would leave some other roof tile unsupported.</summary>
  private bool WouldCauseCollapse(IntVec3 cell)
  {
    simulatedRetracted.Clear();
    simulatedRetracted.Add(cell);

    cellsToCheck.Clear();
    foreach (var c in canopyCells)
    {
      if (Map.roofGrid.RoofAt(c) != null && c != cell)
      {
        cellsToCheck.Add(c);
      }
      for (int i = 0; i < 8; i++)
      {
        IntVec3 adj = c + GenAdj.AdjacentCells[i];
        if (adj.InBounds(Map) && !canopyCells.Contains(adj) && Map.roofGrid.RoofAt(adj) != null)
        {
          cellsToCheck.Add(adj);
        }
      }
    }

    foreach (var c in cellsToCheck)
    {
      if (IsCellSupported(c) && !IsCellSupported(c, simulatedRetracted)) return true;
    }
    return false;
  }

  private Vector3 GetPanelSlideDirection(IntVec3 cell)
  {
    Vector3 anchor = cell.ToVector3Shifted();
    switch (openingStyle)
    {
      case RetractableRoofOpeningStyle.SplitX:
        {
          float dx = anchor.x - roomCentroid.x;
          return dx < 0 ? new Vector3(-1f, 0, 0) : new Vector3(1f, 0, 0);
        }
      case RetractableRoofOpeningStyle.SplitY:
        {
          float dz = anchor.z - roomCentroid.z;
          return dz < 0 ? new Vector3(0, 0, -1f) : new Vector3(0, 0, 1f);
        }
      case RetractableRoofOpeningStyle.LinearX:
        {
          return new Vector3(1f, 0, 0);
        }
      case RetractableRoofOpeningStyle.LinearZ:
        {
          return new Vector3(0, 0, 1f);
        }
      case RetractableRoofOpeningStyle.ConsoleRadial:
        {
          Vector3 consolePos = Position.ToVector3Shifted();
          Vector3 rawDir = (anchor - consolePos).normalized;
          if (rawDir.sqrMagnitude < 0.001f) return new Vector3(0, 0, 1f);
          return Mathf.Abs(rawDir.x) > Mathf.Abs(rawDir.z)
            ? new Vector3(Mathf.Sign(rawDir.x), 0, 0)
            : new Vector3(0, 0, Mathf.Sign(rawDir.z));
        }
      case RetractableRoofOpeningStyle.Quadrant:
      case RetractableRoofOpeningStyle.Iris:
      default:
        {
          Vector3 rawDir = (anchor - roomCentroid).normalized;
          if (rawDir.sqrMagnitude < 0.001f) return new Vector3(0, 0, 1f);
          return Mathf.Abs(rawDir.x) > Mathf.Abs(rawDir.z)
            ? new Vector3(Mathf.Sign(rawDir.x), 0, 0)
            : new Vector3(0, 0, Mathf.Sign(rawDir.z));
        }
    }
  }

  /// <summary>
  /// Queues the sliding-panel visual for a cell. Cosmetic only — nothing in here may stop the
  /// cell itself from moving.
  /// </summary>
  private void AddPanelAnimation(IntVec3 cell, RoofDef roof, ThingDef? stuff, int animationDuration, bool retracting)
  {
    var animator = cachedAnimator ?? Map.GetComponent<RetractableRoofAnimator>();
    if (animator == null) return;

    var gd = RoofStatCache.GetGraphicData(roof);
    if (gd == null) return;

    var entry = Graphics.RoofAtlasManager.GetEntry(gd.texPath);
    Vector2[]? uvs = null;

    if (entry.IsSeamless && entry.SeamlessGrid != null)
    {
      int col = cell.x % entry.GridWidth;
      if (col < 0) col += entry.GridWidth;
      int row = cell.z % entry.GridHeight;
      if (row < 0) row += entry.GridHeight;

      if (entry.SeamlessGrid.TryGetValue((col, row), out var uvEntry))
      {
        uvs = uvEntry;
      }
    }
    else if (entry.FlatVariants.Count > 0)
    {
      uvs = entry.FlatVariants[Mathf.Abs(cell.GetHashCode()) % entry.FlatVariants.Count];
    }

    if (uvs == null) return;

    Vector3 anchor = cell.ToVector3Shifted();
    Vector3 dir = GetPanelSlideDirection(cell);
    Vector3 offset = anchor + dir * 1f;

    // Retracting slides the panel outward; extending slides it back in.
    Vector3 start = retracting ? anchor : offset;
    Vector3 end = retracting ? offset : anchor;

    Color color = RoofStatCache.GetColor(roof, stuff);
    if (RoofStatCache.IsSkylight(roof))
    {
      color.a = 1f - RoofStatCache.GetTransparency(roof);
    }

    var mats = Graphics.RoofAtlasManager.GetTransitionMaterials(gd.texPath, color);
    Material mat = roof.isNatural
      ? Graphics.RoofAtlasManager.GetMetaOverlay(gd.texPath)
      : (RoofStatCache.IsSkylight(roof) ? mats.transparent : mats.cutout);

    Color vertexColor = roof.isNatural ? color : Color.white;
    animator.AddTransition(start, end, animationDuration, mat, vertexColor, uvs);
  }

  /// <summary>
  /// Re-adopts canopy cells that earlier versions orphaned. Closing used to discard a cell's
  /// tracker record before its roof was actually restored, so an interrupted close could leave
  /// cells with neither a roof nor a record — invisible to <see cref="RecalculateRings"/> and
  /// therefore impossible to close ever again. Anything roofless inside the console's room that
  /// touches a known canopy cell is re-registered so it can be extended normally.
  /// </summary>
  private void RecoverOrphanedCells()
  {
    var tracker = cachedTracker;
    if (tracker == null || Map?.roofGrid == null) return;
    if (canopyCells.Count == 0) return;

    var room = this.GetRoom();
    if (room == null) return;

    RoofDef? canopyRoof = null;
    foreach (var c in canopyCells)
    {
      var r = Map.roofGrid.RoofAt(c);
      if (IsRetractableRoof(r)) { canopyRoof = r; break; }

      if (tracker.PeekOpenRoof(Map.cellIndices.CellToIndex(c), out var saved, out _, out _, out _))
      {
        canopyRoof = saved;
        break;
      }
    }

    if (canopyRoof == null) return; // Nothing to infer the missing roof's identity from.

    var roomCells = new HashSet<IntVec3>(room.Cells);
    var recovered = new List<IntVec3>();
    var frontier = new Queue<IntVec3>(canopyCells);
    var seen = new HashSet<IntVec3>(canopyCells);

    while (frontier.Count > 0)
    {
      var curr = frontier.Dequeue();
      for (int i = 0; i < 8; i++)
      {
        IntVec3 n = curr + GenAdj.AdjacentCells[i];
        if (!n.InBounds(Map) || !seen.Add(n)) continue;
        if (!roomCells.Contains(n)) continue;
        if (Map.roofGrid.RoofAt(n) != null) continue;

        int idx = Map.cellIndices.CellToIndex(n);
        if (tracker.IsRetracted(idx)) continue;

        short maxHp = (short)RoofStatCache.GetMaxHitPoints(canopyRoof, null);
        tracker.SaveOpenRoof(idx, canopyRoof, null, null, maxHp);
        recovered.Add(n);
        frontier.Enqueue(n);
      }
    }

    if (recovered.Count > 0)
    {
      StratumLog.Warning(
        $"Canopy console at {Position} re-adopted {recovered.Count} orphaned cell(s) left open by an " +
        "interrupted close. They can now be extended again.");
      RecalculateRings();
    }
  }

  /// <summary>Records a cell an open could not retract, so the player can see why it stalled.</summary>
  private void NoteBlockedCell(IntVec3 cell, string reason)
  {
    if (blockedCells.Add(cell))
    {
      StratumLog.Warning($"Canopy console at {Position} could not retract cell {cell}: {reason}.");
    }
  }

  private bool IsRoofHolder(IntVec3 c)
  {
    if (Map == null) return false;
    Building edifice = c.GetEdifice(Map);
    return edifice != null && edifice.def != null && edifice.def.holdsRoof;
  }

  private bool IsCellSupported(IntVec3 startCell, HashSet<IntVec3>? simulatedRetracted = null)
  {
    if (Map?.roofGrid == null) return false;
    if (IsRoofHolder(startCell)) return true;

    supportCheckQueue.Clear();
    supportCheckVisited.Clear();

    supportCheckQueue.Enqueue(startCell);
    supportCheckVisited.Add(startCell);

    while (supportCheckQueue.Count > 0)
    {
      IntVec3 curr = supportCheckQueue.Dequeue();

      if (IsRoofHolder(curr))
      {
        if (ChebyshevDistance(startCell, curr) <= 6)
        {
          return true;
        }
      }

      for (int i = 0; i < 4; i++)
      {
        IntVec3 n = curr + GenAdj.CardinalDirections[i];
        if (!n.InBounds(Map)) continue;

        if (IsRoofHolder(n))
        {
          if (ChebyshevDistance(startCell, n) <= 6)
          {
            return true;
          }
        }

        if (!supportCheckVisited.Contains(n))
        {
          var roof = Map.roofGrid.RoofAt(n);
          if (roof != null && (simulatedRetracted == null || !simulatedRetracted.Contains(n)))
          {
            supportCheckVisited.Add(n);
            supportCheckQueue.Enqueue(n);
          }
        }
      }
    }

    return false;
  }

  private static int ChebyshevDistance(IntVec3 a, IntVec3 b)
  {
    return Mathf.Max(Mathf.Abs(a.x - b.x), Mathf.Abs(a.z - b.z));
  }
}
