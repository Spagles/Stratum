using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

using SolarWeb.Stratum.MapComponents;

namespace SolarWeb.Stratum.AI.JobDrivers;

public class CleanSkylight : JobDriver
{
  private const float BaseCleanWork = 240f;
  private const float MinCleanWork = 20f;

  private float workDone;
  private float workRequired = BaseCleanWork;

  protected IntVec3 Cell => TargetA.Cell;

  public override bool TryMakePreToilReservations(bool errorOnFailed)
  {
    return pawn.Reserve(Cell, job, 1, -1, null, errorOnFailed);
  }

  public override void ExposeData()
  {
    base.ExposeData();
    Scribe_Values.Look(ref workDone, "workDone", 0f);
    Scribe_Values.Look(ref workRequired, "workRequired", BaseCleanWork);
  }

  protected override IEnumerable<Toil> MakeNewToils()
  {
    this.FailOn(() => !pawn.CanReach(Cell, PathEndMode.Touch, Danger.Deadly));
    this.FailOn(() => pawn.Faction == Faction.OfPlayer && !pawn.Map.areaManager.Home[Cell] && !job.playerForced);

    yield return Toils_Goto.GotoCell(TargetIndex.A, PathEndMode.Touch);

    Toil clean = ToilMaker.MakeToil("MakeNewToils");
    clean.defaultCompleteMode = ToilCompleteMode.Never;
    clean.handlingFacing = true;
    clean.initAction = delegate
    {
      pawn.rotationTracker.FaceCell(Cell);
      workDone = 0f;
      float coating = pawn.Map?.GetComponent<SkylightCoating>()?.GetCoatingOpacity(Cell) ?? 1f;
      workRequired = Mathf.Max(MinCleanWork, BaseCleanWork * coating);
    };
    clean.tickIntervalAction = delegate (int delta)
    {
      pawn.rotationTracker.FaceCell(Cell);
      if (pawn.IsHashIntervalTick(15, delta))
      {
        FleckMaker.ThrowSmoke(Cell.ToVector3Shifted(), pawn.Map, 0.4f);
      }

      workDone += pawn.GetStatValue(StatDefOf.CleaningSpeed) * delta;
      if (workDone >= workRequired)
      {
        var dirt = pawn.Map?.GetComponent<SkylightCoating>();
        if (dirt != null)
        {
          dirt.SetDirtLevel(Cell, 0f);
          dirt.SetPollenLevel(Cell, 0f);
          dirt.SetSnowLevel(Cell, 0f);
        }
        ReadyForNextToil();
      }
    };
    clean.WithProgressBar(TargetIndex.A, () => workDone / workRequired);
    clean.FailOnCannotTouch(TargetIndex.A, PathEndMode.Touch);
    clean.WithEffect(EffecterDefOf.Clean, TargetIndex.A);

    yield return clean;
  }
}
