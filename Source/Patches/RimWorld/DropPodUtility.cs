using HarmonyLib;
using RimWorld;
using Verse;

using SolarWeb.Stratum.MapComponents;
using SolarWeb.Stratum.Utilities;

namespace SolarWeb.Stratum.Patches;

[HarmonyPatch]
public static class DropPodUtility_Patch
{
  /// <summary>
  /// Steers a pod away from any roof that would stop it, before the skyfaller is ever created.
  /// An empty decoy is dropped on the original cell so the player still sees -- and hears -- the
  /// pod slam into their hull.
  /// </summary>
  /// <remarks>
  /// When nothing landable exists anywhere on the map the pod is left where it was aimed;
  /// Skyfaller_Patch then lets it punch through the roof rather than destroying its contents.
  /// </remarks>
  [HarmonyPatch(typeof(DropPodUtility), nameof(DropPodUtility.MakeDropPodAt))]
  [HarmonyPrefix]
  public static void MakeDropPodAt_Prefix(ref IntVec3 c, Map map, ActiveTransporterInfo info, Faction faction)
  {
    if (!Stratum.Settings.enableDropPodInterception) return;
    if (map == null || !c.IsValid || !c.InBounds(map)) return;

    var fallerDef = info?.sentTransporterDef?.dropPodFaller ?? faction?.def.dropPodIncoming ?? ThingDefOf.DropPodIncoming;

    // Read the same hit points the impact path reads. A local floor here would silently disagree
    // with Skyfallers_MaxHitPoints.xml the moment either number moved.
    int podHitPoints = fallerDef.BaseMaxHitPoints;

    RoofIntegrityGrid? grid = null;
    if (!RoofInterceptionUtility.WouldRoofStopSkyfaller(map, c, podHitPoints, ref grid)) return;

    if (!RoofInterceptionUtility.TryFindSafeCell(map, c, podHitPoints, out IntVec3 newCell)) return;

    IntVec3 originalCell = c;
    c = newCell;

    var podDef = info?.sentTransporterDef?.dropPodActive ?? faction?.def.dropPodActive ?? ThingDefOf.ActiveDropPod;
    ActiveTransporter dummyTransporter = (ActiveTransporter)ThingMaker.MakeThing(podDef);
    dummyTransporter.Contents = new ActiveTransporterInfo();
    SkyfallerMaker.SpawnSkyfaller(fallerDef, dummyTransporter, originalCell, map);
  }
}
