﻿using System;
using RimWorld;
using Verse;

namespace RimThreaded.RW_Patches
{

    public class Building_Door_Patch
    {
        internal static void RunDestructivePatches()
        {
            Type original = typeof(Building_Door);
            Type patched = typeof(Building_Door_Patch);
            RimThreadedHarmony.Prefix(original, patched, "get_DoorPowerOn");
        }

        public static bool get_DoorPowerOn(Building_Door __instance, ref bool __result)
        {
            CompPowerTrader pc = __instance.powerComp;
            bool poweron = false;
            if (pc != null)
            {
                try
                {
                    poweron = pc.PowerOn;
                }
                catch (NullReferenceException) { Log.Error("Error getting PowerOn status for Building_Door: " + __instance.ToString()); }
            }
            __result = poweron;
            return false;
        }
    }
}
