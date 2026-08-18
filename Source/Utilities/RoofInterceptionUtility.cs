using RimWorld;
using Verse;

using SolarWeb.Stratum.DefModExtensions;
using SolarWeb.Stratum.Hooks;
using SolarWeb.Stratum.MapComponents;

namespace SolarWeb.Stratum.Utilities;

public static class RoofInterceptionUtility
{
  private const int NearbySearchRadius = 15;
  private const int WidenedSearchRadius = 40;
  private const int MapWideAttempts = 1000;

  public static bool WouldRoofStopSkyfaller(Map map, IntVec3 cell, int skyfallerHealth, ref RoofIntegrityGrid? grid)
  {
    if (map == null || !cell.IsValid || !cell.InBounds(map)) return false;

    // Compat hooks report a roof on another map level, whose mitigation stats we cannot reach from
    // here, so this stays the raw comparison it has always been. A mispredicted cross-map roof is
    // no longer fatal: Skyfaller_Patch relocates the contents at impact rather than deleting them.
    // damageAmount 0 keeps this a pure query -- only the impact path is allowed to apply damage.
    int hookHp = 0;
    if (MapHookRegistry.InterceptDropPodByRoof(map, cell, 0, ref hookHp) && hookHp > skyfallerHealth)
      return true;

    var roof = map.roofGrid.RoofAt(cell);
    if (roof == null || !roof.HasModExtension<BuildableRoofExtension>()) return false;

    grid ??= map.GetComponent<RoofIntegrityGrid>();
    return grid != null && grid.WouldSurviveDamage(cell, skyfallerHealth);
  }

  /// <summary>
  /// Finds somewhere a skyfaller can actually land, searching outward from
  /// <paramref name="origin"/> and widening until the whole map has been tried.
  /// </summary>
  /// <returns>
  /// False only when the map has no landable cell at all; callers must then let the skyfaller punch
  /// through the roof rather than destroy it.
  /// </returns>
  public static bool TryFindSafeCell(Map map, IntVec3 origin, int skyfallerHealth, out IntVec3 result)
  {
    result = IntVec3.Invalid;
    if (map == null) return false;

    RoofIntegrityGrid? grid = null;

    bool IsLandable(IntVec3 c)
    {
      if (!DropCellFinder.IsGoodDropSpot(c, map, allowFogged: true, canRoofPunch: true)) return false;
      return !WouldRoofStopSkyfaller(map, c, skyfallerHealth, ref grid);
    }

    // Nearby: keep the pod as close to where it was aimed as we can. Skips index 0, which is the
    // origin cell the caller already knows is blocked.
    int maxCells = GenRadial.NumCellsInRadius(NearbySearchRadius);
    for (int i = 1; i < maxCells; i++)
    {
      IntVec3 candidate = origin + GenRadial.RadialPattern[i];
      if (!candidate.InBounds(map)) continue;
      if (!IsLandable(candidate)) continue;

      result = candidate;
      return true;
    }

    if (DropCellFinder.TryFindDropSpotNear(origin, map, out var widened, allowFogged: true,
          canRoofPunch: true, maxRadius: WidenedSearchRadius, mustBeReachableFromCenter: false)
        && IsLandable(widened))
    {
      result = widened;
      return true;
    }

    return CellFinderLoose.TryGetRandomCellWith(IsLandable, map, MapWideAttempts, out result);
  }
}
