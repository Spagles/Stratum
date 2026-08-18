using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace SolarWeb.Stratum.MapComponents;

public class RetractableRoofTracker(Map map) : MapComponent(map)
{
  private List<int> openCellIndices = [];
  private List<RoofDef> originalRoofDefs = [];
  private List<ThingDef?> stuffDefs = [];
  private List<Color> glassTints = [];
  private List<short> hitPoints = [];

  public override void ExposeData()
  {
    base.ExposeData();
    Scribe_Collections.Look(ref openCellIndices, "openCellIndices", LookMode.Value);
    Scribe_Collections.Look(ref originalRoofDefs, "originalRoofDefs", LookMode.Def);
    Scribe_Collections.Look(ref stuffDefs, "stuffDefs", LookMode.Def);
    Scribe_Collections.Look(ref glassTints, "glassTints", LookMode.Value);
    Scribe_Collections.Look(ref hitPoints, "hitPoints", LookMode.Value);

    if (Scribe.mode == LoadSaveMode.PostLoadInit)
    {
      openCellIndices ??= [];
      originalRoofDefs ??= [];
      stuffDefs ??= [];
      glassTints ??= [];
      hitPoints ??= [];
      SanitizeAfterLoad();
    }
  }

  private void SanitizeAfterLoad()
  {
    int usable = Mathf.Min(
      openCellIndices.Count,
      Mathf.Min(originalRoofDefs.Count, Mathf.Min(stuffDefs.Count, Mathf.Min(glassTints.Count, hitPoints.Count))));

    if (usable < openCellIndices.Count)
    {
      StratumLog.Warning(
        $"RetractableRoofTracker: parallel lists desynced on load ({openCellIndices.Count} cells, " +
        $"{usable} usable); dropping {openCellIndices.Count - usable} unrestorable record(s).");
    }

    openCellIndices.RemoveRange(usable, openCellIndices.Count - usable);
    originalRoofDefs.RemoveRange(usable, originalRoofDefs.Count - usable);
    stuffDefs.RemoveRange(usable, stuffDefs.Count - usable);
    glassTints.RemoveRange(usable, glassTints.Count - usable);
    hitPoints.RemoveRange(usable, hitPoints.Count - usable);

    for (int i = openCellIndices.Count - 1; i >= 0; i--)
    {
      if (originalRoofDefs[i] == null)
      {
        StratumLog.Warning(
          $"RetractableRoofTracker: cell index {openCellIndices[i]} has an unresolved RoofDef; " +
          "dropping the record. That cell will stay unroofed until rebuilt.");
        ClearOpenRoof(openCellIndices[i]);
      }
    }
  }

  public void SaveOpenRoof(int cellIndex, RoofDef roofDef, ThingDef? stuff, Color? tint, short hp)
  {
    int i = openCellIndices.IndexOf(cellIndex);
    if (i >= 0)
    {
      originalRoofDefs[i] = roofDef;
      stuffDefs[i] = stuff;
      glassTints[i] = tint ?? Color.white;
      hitPoints[i] = hp;
      return;
    }

    openCellIndices.Add(cellIndex);
    originalRoofDefs.Add(roofDef);
    stuffDefs.Add(stuff);
    glassTints.Add(tint ?? Color.white);
    hitPoints.Add(hp);
  }

  /// <summary>
  /// Forgets the retracted-roof record for a cell. Only call this once the roof has actually
  /// been written back into the roof grid: while a cell has no roof, this record is the sole
  /// means of restoring it, so clearing it early orphans the cell permanently.
  /// </summary>
  public bool ClearOpenRoof(int cellIndex)
  {
    int i = openCellIndices.IndexOf(cellIndex);
    if (i < 0) return false;

    openCellIndices.RemoveAt(i);
    originalRoofDefs.RemoveAt(i);
    stuffDefs.RemoveAt(i);
    glassTints.RemoveAt(i);
    hitPoints.RemoveAt(i);
    return true;
  }

  public bool PeekOpenRoof(int cellIndex, out RoofDef roofDef, out ThingDef? stuff, out Color? tint, out short hp)
  {
    int i = openCellIndices.IndexOf(cellIndex);
    if (i >= 0)
    {
      roofDef = originalRoofDefs[i];
      stuff = stuffDefs[i];
      tint = glassTints[i];
      hp = hitPoints[i];
      return true;
    }

    roofDef = null!;
    stuff = null;
    tint = null;
    hp = 0;
    return false;
  }

  public bool IsRetracted(int cellIndex)
  {
    return openCellIndices.Contains(cellIndex);
  }

  public IEnumerable<int> GetRetractedCells()
  {
    return openCellIndices;
  }
}
