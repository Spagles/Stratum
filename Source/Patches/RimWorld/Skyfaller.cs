using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

using SolarWeb.Stratum.DefModExtensions;
using SolarWeb.Stratum.MapComponents;
using SolarWeb.Stratum.Utilities;

namespace SolarWeb.Stratum.Patches;

[HarmonyPatch]
public static class Skyfaller_Patch
{
  /// <summary>
  /// Skyfallers this patch has already deflected once, by thingIDNumber.
  /// </summary>
  /// <remarks>
  /// A cell is picked because the roof there is predicted not to stop the skyfaller, but for roofs
  /// on other map levels that prediction goes through a compat hook that can only report raw hit
  /// points -- it cannot see the destination's damage threshold or armour. A pod deflected onto
  /// such a roof can therefore be deflected again. One deflection per skyfaller; after that it
  /// punches through, so a wrong prediction can never turn into a pod bouncing around the map.
  ///
  /// HitRoof runs exactly once per skyfaller, and this prefix runs ahead of every one of its early
  /// returns, so entries are always consumed and the set cannot grow without bound.
  /// </remarks>
  private static readonly HashSet<int> alreadyDeflected = [];

  [HarmonyPatch(typeof(Skyfaller), "HitRoof")]
  [HarmonyPrefix]
  public static bool HitRoof_Prefix(Skyfaller __instance)
  {
    bool wasDeflectedBefore = alreadyDeflected.Remove(__instance.thingIDNumber);

    if (!Stratum.Settings.enableDropPodInterception) return true;
    var map = __instance.Map;
    if (map == null) return true;

    var integrityGrid = map.GetComponent<RoofIntegrityGrid>();
    if (integrityGrid == null) return true;

    var pos = __instance.Position;
    var skyfallerHealth = __instance.def.BaseMaxHitPoints;

    int effectiveHp = 0;
    bool intercepted = Hooks.MapHookRegistry.InterceptDropPodByRoof(map, pos, skyfallerHealth, ref effectiveHp);

    if (intercepted && effectiveHp > 0)
    {
      return BounceOff(__instance, map, pos, skyfallerHealth, wasDeflectedBefore);
    }

    CellRect cr = __instance.OccupiedRect();
    CellRect cellRect = cr.ExpandedBy((!__instance.def.skyfaller.minimalRoofDestruction) ? 1 : 0).ClipInsideMap(map);

    bool anyBuildableRoof = false;

    foreach (IntVec3 c in cellRect.Cells)
    {
      var roofAtC = map.roofGrid.RoofAt(c);
      if (roofAtC != null && roofAtC.HasModExtension<BuildableRoofExtension>())
      {
        anyBuildableRoof = true;
        integrityGrid.TakeDamage(c, skyfallerHealth);
      }
    }

    if (anyBuildableRoof && integrityGrid.GetHitPoints(pos) > 0)
    {
      return BounceOff(__instance, map, pos, skyfallerHealth, wasDeflectedBefore);
    }

    return true;
  }

  /// <summary>
  /// The roof held. Slam into it, then put whatever the skyfaller was carrying down somewhere it
  /// can survive.
  /// </summary>
  /// <returns>
  /// False once the skyfaller has been dealt with, true to fall through to vanilla HitRoof and
  /// punch through the roof instead.
  /// </returns>
  /// <remarks>
  /// This used to be a bare Destroy(), and Skyfaller.Destroy calls
  /// innerContainer.ClearAndDestroyContents() -- so an intercepted pod deleted its colonists and
  /// their gear with no corpse, no letter and no log entry. Nothing a roof does may destroy cargo.
  /// </remarks>
  private static bool BounceOff(Skyfaller skyfaller, Map map, IntVec3 pos, int skyfallerHealth, bool wasDeflectedBefore)
  {
    // An empty decoy is the one skyfaller that should simply vanish here -- it exists only so the
    // player sees the impact that its real, already-relocated pod avoided.
    bool carriesNothing = skyfaller is IActiveTransporter transporter
      ? (transporter.Contents?.innerContainer?.Count ?? 0) == 0
      : !skyfaller.innerContainer.Any;

    IntVec3 safeCell = IntVec3.Invalid;
    bool canRelocate = !carriesNothing
      && !wasDeflectedBefore
      && RoofInterceptionUtility.TryFindSafeCell(map, pos, skyfallerHealth, out safeCell);

    if (!carriesNothing && !canRelocate)
    {
      // Nowhere left to put it. Better to breach the roof than to delete the cargo.
      StratumLog.Warning($"Cannot relocate {skyfaller.def.defName} carrying cargo at {pos} on {map} "
                       + (wasDeflectedBefore ? "(already deflected once); " : "(no landable cell on the map); ")
                       + "letting it punch through the roof rather than destroying its contents.");
      return true;
    }

    for (int i = 0; i < 6; i++)
    {
      FleckMaker.ThrowDustPuff(pos.ToVector3Shifted() + Gen.RandomHorizontalVector(1f), map, 1.2f);
    }
    FleckMaker.ThrowLightningGlow(pos.ToVector3Shifted(), map, 2f);
    GenClamor.DoClamor(skyfaller, 15f, ClamorDefOf.Impact);

    if (canRelocate)
    {
      // Relaunch the same skyfaller type at the safe cell and hand the cargo over before
      // destroying the original, so ClearAndDestroyContents finds nothing left to destroy.
      Skyfaller relocated = SkyfallerMaker.MakeSkyfaller(skyfaller.def);
      skyfaller.innerContainer.TryTransferAllToContainer(relocated.innerContainer, canMergeWithExistingStacks: false);
      GenSpawn.Spawn(relocated, safeCell, map);
      alreadyDeflected.Add(relocated.thingIDNumber);
    }

    skyfaller.Destroy();
    return false;
  }
}
