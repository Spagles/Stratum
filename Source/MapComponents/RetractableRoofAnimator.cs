using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

using SolarWeb.Stratum.WorldComponents;

namespace SolarWeb.Stratum.MapComponents;

public class RetractableRoofAnimator : MapComponent
{
  public class PendingRoof : IExposable
  {
    public IntVec3 cell;
    public RoofDef def = null!;
    public ThingDef? stuff;
    public Color tint;
    public short hp;
    public int placementTick;

    public void ExposeData()
    {
      Scribe_Values.Look(ref cell, "cell");
      Scribe_Defs.Look(ref def, "def");
      Scribe_Defs.Look(ref stuff, "stuff");
      Scribe_Values.Look(ref tint, "tint", Color.white);
      Scribe_Values.Look(ref hp, "hp", (short)0);
      Scribe_Values.Look(ref placementTick, "placementTick", 0);
    }
  }

  private List<TransitioningRoof> transitions = [];
  private List<PendingRoof> pendingRoofs = [];

  public RetractableRoofAnimator(Map map) : base(map)
  {
  }

  public override void ExposeData()
  {
    base.ExposeData();
    Scribe_Collections.Look(ref pendingRoofs, "pendingRoofs", LookMode.Deep);

    if (Scribe.mode == LoadSaveMode.PostLoadInit)
    {
      pendingRoofs ??= [];

      int currentTick = Find.TickManager.TicksGame;
      for (int i = pendingRoofs.Count - 1; i >= 0; i--)
      {
        var p = pendingRoofs[i];
        if (p == null || p.def == null)
        {
          pendingRoofs.RemoveAt(i);
          continue;
        }

        if (p.placementTick > currentTick) p.placementTick = currentTick;
      }
    }
  }

  public void AddTransition(Vector3 start, Vector3 end, int duration, Material mat, Color col, Vector2[] uvs)
  {
    var pool = Find.World?.GetComponent<RoofAnimationPool>();
    if (pool == null)
    {
      StratumLog.Error("RetractableRoofAnimator: RoofAnimationPool unavailable; skipping panel animation.", once: true);
      return;
    }

    var t = pool.GetTransitioningRoof(start, end, Find.TickManager.TicksGame, duration, mat, col, uvs);
    transitions.Add(t);
  }

  public void AddPendingRoof(IntVec3 cell, RoofDef def, ThingDef? stuff, Color tint, short hp, int delayTicks)
  {
    pendingRoofs.Add(new PendingRoof
    {
      cell = cell,
      def = def,
      stuff = stuff,
      tint = tint,
      hp = hp,
      placementTick = Find.TickManager.TicksGame + delayTicks
    });
  }

  public override void MapComponentTick()
  {
    base.MapComponentTick();
    if (pendingRoofs.Count == 0) return;
    if (map?.roofGrid == null) return;

    int currentTick = Find.TickManager.TicksGame;
    var integrityGrid = map.GetComponent<RoofIntegrityGrid>();
    var tracker = map.GetComponent<RetractableRoofTracker>();

    for (int i = pendingRoofs.Count - 1; i >= 0; i--)
    {
      var p = pendingRoofs[i];
      if (currentTick < p.placementTick) continue;

      if (p.def == null)
      {
        pendingRoofs.RemoveAt(i);
        continue;
      }

      if (map.roofGrid.RoofAt(p.cell) == null)
      {
        map.roofGrid.SetRoof(p.cell, p.def);
        integrityGrid?.InitializeRoof(p.cell, p.def, p.stuff, p.tint, p.hp);
        FleckMaker.ThrowAirPuffUp(p.cell.ToVector3Shifted(), map);
      }

      if (map.roofGrid.RoofAt(p.cell) != null)
      {
        tracker?.ClearOpenRoof(map.cellIndices.CellToIndex(p.cell));
      }

      pendingRoofs.RemoveAt(i);
    }
  }

  public override void MapComponentUpdate()
  {
    base.MapComponentUpdate();
    if (transitions.Count == 0) return;

    int currentTick = Find.TickManager.TicksGame;
    float altitude = AltitudeLayer.MoteOverhead.AltitudeFor() - 0.05f;

    for (int i = transitions.Count - 1; i >= 0; i--)
    {
      var t = transitions[i];
      if (t.GetProgress(currentTick) >= 1f)
      {
        Find.World?.GetComponent<RoofAnimationPool>()?.Return(t);
        transitions.RemoveAt(i);
        continue;
      }

      Vector3 pos = t.GetCurrentPosition(currentTick);
      pos.y = altitude;

      Matrix4x4 matrix = Matrix4x4.TRS(pos, Quaternion.identity, Vector3.one);

      UnityEngine.Graphics.DrawMesh(t.mesh, matrix, t.material, 0);
    }
  }

  public override void MapRemoved()
  {
    base.MapRemoved();
    var pool = Find.World?.GetComponent<RoofAnimationPool>();
    foreach (var t in transitions)
    {
      pool?.Return(t);
    }
    transitions.Clear();

    // Deliberately not clearing pendingRoofs: the tracker still holds each cell's record, so
    // the roofs remain restorable if this map ever comes back.
  }
}
