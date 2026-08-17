using UnityEngine;
using Verse;

using SolarWeb.Stratum.Hooks;
using SolarWeb.Stratum.Stats;

namespace SolarWeb.Stratum.TiltThePlanet;

[StaticConstructorOnStartup]
public static class CompatInitializer
{
  static CompatInitializer()
  {
    MapHookRegistry.RegisterGlobal<MapHookRegistry.GroundGlowHandler>(
      MapHookRegistry.HookId.GroundGlow,
      GroundGlowCompatHandler
    );
  }

  private static bool GroundGlowCompatHandler(GlowGrid instance, Map map, IntVec3 cell, bool ignoreCavePlants, bool ignoreSky, ref float result)
  {
    var roof = map.roofGrid.RoofAt(cell);
    if (roof != null && RoofStatCache.IsSkylight(roof))
    {
      // Effective, not raw: this handler short-circuits Stratum's own GroundGlow prefix, so reading
      // the bare stat here would make skylight coating stop affecting light entirely under Tilt.
      float transparency = RoofStatCache.GetEffectiveTransparency(roof, Patches.GlowGrid_Patch.CoatingFor(map), cell);
      if (transparency > 0f)
      {
        float skyGlow = 0f;
        if (!ignoreSky)
        {
          skyGlow = RealTiltMod.Patch_SkyManager_CurrentSkyTarget.usableLightFraction * transparency;
        }

        int cellIdx = map.cellIndices.CellToIndex(cell);
        Color32 accumulated = RealTiltMod.Patch_GlowGrid_GetAccumulatedGlowAt_Postfix.GetAccumulatedGlowAtOriginal(instance, cellIdx, ignoreCavePlants);
        float luxValue = RealTiltMod.LightingPatch.DecodeLuxFromColorInt(accumulated);
        float artificialGlow = RealTiltMod.Patch_SkyManager_CurrentSkyTarget.mapLuxToDisplayPercent(luxValue);

        result = Mathf.Max(skyGlow, artificialGlow);
        return true;
      }
    }

    return false;
  }
}
