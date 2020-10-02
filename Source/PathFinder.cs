﻿using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.Sound;
using System.Diagnostics;
using UnityEngine;
using System.Threading;

namespace RimThreaded
{

    public class PathFinder_Patch
    {
        public static AccessTools.FieldRef<PathFinder, Map> mapField =
            AccessTools.FieldRefAccess<PathFinder, Map>("map");
        public static AccessTools.FieldRef<PathFinder, RegionCostCalculatorWrapper> regionCostCalculator =
            AccessTools.FieldRefAccess<PathFinder, RegionCostCalculatorWrapper>("regionCostCalculator");

        public static Dictionary<int, PathFinderNodeFast[]> calcGridDict2 =
            new Dictionary<int, PathFinderNodeFast[]>();
        public static Dictionary<int, FastPriorityQueue<CostNode>> openListDict2 =
            new Dictionary<int, FastPriorityQueue<CostNode>>();

        public static readonly SimpleCurve NonRegionBasedHeuristicStrengthHuman_DistanceCurve =
            AccessTools.StaticFieldRefAccess<SimpleCurve>(typeof(PathFinder), "NonRegionBasedHeuristicStrengthHuman_DistanceCurve");
        public static readonly int[] Directions =
            AccessTools.StaticFieldRefAccess<int[]>(typeof(PathFinder), "Directions");

        public static object pLock = new object();

        public struct CostNode
        {
            public int index;

            public int cost;

            public CostNode(int index, int cost)
            {
                this.index = index;
                this.cost = cost;
            }
        }
        public struct PathFinderNodeFast
        {
            public int knownCost;

            public int heuristicCost;

            public int parentIndex;

            public int costNodeCost;

            public ushort status;
        }
        [Conditional("PFPROFILE")]
        private static void PfProfilerBeginSample(string s)
        {
        }
        private static CellRect CalculateDestinationRect(LocalTargetInfo dest, PathEndMode peMode)
        {
            CellRect result = (dest.HasThing && peMode != PathEndMode.OnCell) ? dest.Thing.OccupiedRect() : CellRect.SingleCell(dest.Cell);
            if (peMode == PathEndMode.Touch)
            {
                result = result.ExpandedBy(1);
            }

            return result;
        }

        private static Area GetAllowedArea(Pawn pawn)
        {
            if (pawn != null && pawn.playerSettings != null && !pawn.Drafted && ForbidUtility.CaresAboutForbidden(pawn, cellTarget: true))
            {
                Area area = pawn.playerSettings.EffectiveAreaRestrictionInPawnCurrentMap;
                if (area != null && area.TrueCount <= 0)
                {
                    area = null;
                }

                return area;
            }

            return null;
        }
        private static float DetermineHeuristicStrength(Pawn pawn, IntVec3 start, LocalTargetInfo dest)
        {
            if (pawn != null && pawn.RaceProps.Animal)
            {
                return 1.75f;
            }

            float lengthHorizontal = (start - dest.Cell).LengthHorizontal;
            return Mathf.RoundToInt(NonRegionBasedHeuristicStrengthHuman_DistanceCurve.Evaluate(lengthHorizontal));
        }
        private static List<int> CalculateAndAddDisallowedCorners2(Map map2, PathEndMode peMode, CellRect destinationRect)
        {
            List<int> disallowedCornerIndices2 = new List<int>(4);
            if (peMode == PathEndMode.Touch)
            {
                int minX = destinationRect.minX;
                int minZ = destinationRect.minZ;
                int maxX = destinationRect.maxX;
                int maxZ = destinationRect.maxZ;
                if (!IsCornerTouchAllowed2(map2, minX + 1, minZ + 1, minX + 1, minZ, minX, minZ + 1))
                {
                    disallowedCornerIndices2.Add(map2.cellIndices.CellToIndex(minX, minZ));
                }

                if (!IsCornerTouchAllowed2(map2, minX + 1, maxZ - 1, minX + 1, maxZ, minX, maxZ - 1))
                {
                    disallowedCornerIndices2.Add(map2.cellIndices.CellToIndex(minX, maxZ));
                }

                if (!IsCornerTouchAllowed2(map2, maxX - 1, maxZ - 1, maxX - 1, maxZ, maxX, maxZ - 1))
                {
                    disallowedCornerIndices2.Add(map2.cellIndices.CellToIndex(maxX, maxZ));
                }

                if (!IsCornerTouchAllowed2(map2, maxX - 1, minZ + 1, maxX - 1, minZ, maxX, minZ + 1))
                {
                    disallowedCornerIndices2.Add(map2.cellIndices.CellToIndex(maxX, minZ));
                }
            }
            return disallowedCornerIndices2;
        }
        private static bool IsCornerTouchAllowed2(Map map2, int cornerX, int cornerZ, int adjCardinal1X, int adjCardinal1Z, int adjCardinal2X, int adjCardinal2Z)
        {
            return TouchPathEndModeUtility.IsCornerTouchAllowed(cornerX, cornerZ, adjCardinal1X, adjCardinal1Z, adjCardinal2X, adjCardinal2Z, map2);
        }
        private static void InitStatusesAndPushStartNode2(CellIndices cellIndices, ref int curIndex, IntVec3 start, PathFinderNodeFast[] pathFinderNodeFast, FastPriorityQueue<CostNode> fastPriorityQueue, ref ushort statusOpenValue2, ref ushort statusClosedValue2)
        {
            statusOpenValue2 += 2;
            statusClosedValue2 += 2;
            if (statusClosedValue2 >= 65435)
            {
                int num = pathFinderNodeFast.Length;
                for (int i = 0; i < num; i++)
                {
                    pathFinderNodeFast[i].status = 0;
                }

                statusOpenValue2 = 1;
                statusClosedValue2 = 2;
            }
            curIndex = cellIndices.CellToIndex(start);
            pathFinderNodeFast[curIndex].knownCost = 0;
            pathFinderNodeFast[curIndex].heuristicCost = 0;
            pathFinderNodeFast[curIndex].costNodeCost = 0;
            pathFinderNodeFast[curIndex].parentIndex = curIndex;
            pathFinderNodeFast[curIndex].status = statusOpenValue2;
            fastPriorityQueue.Clear();
            fastPriorityQueue.Push(new CostNode(curIndex, 0));
        }

        private static void DebugDrawRichData()
        {
        }
        [Conditional("PFPROFILE")]
        private static void PfProfilerEndSample()
        {
        }
        private static PawnPath FinalizedPath2(CellIndices cellIndices, int finalIndex, bool usedRegionHeuristics, PathFinderNodeFast[] pathFinderNodeFast)
        {
            //HACK - fix pool
            //PawnPath emptyPawnPath = map(__instance).pawnPathPool.GetEmptyPawnPath();
            PawnPath emptyPawnPath = new PawnPath();
            int num = finalIndex;
            while (true)
            {
                int parentIndex = pathFinderNodeFast[num].parentIndex;
                emptyPawnPath.AddNode(cellIndices.IndexToCell(num));
                if (num == parentIndex)
                {
                    break;
                }

                num = parentIndex;
            }
            emptyPawnPath.SetupFound(pathFinderNodeFast[finalIndex].knownCost, usedRegionHeuristics);
            return emptyPawnPath;
        }

        private static readonly SimpleCurve RegionHeuristicWeightByNodesOpened = new SimpleCurve
        {
            new CurvePoint(0f, 1f),
            new CurvePoint(3500f, 1f),
            new CurvePoint(4500f, 5f),
            new CurvePoint(30000f, 50f),
            new CurvePoint(100000f, 500f)
        };
        public class CostNodeComparer : IComparer<CostNode>
        {
            public int Compare(CostNode a, CostNode b)
            {
                return a.cost.CompareTo(b.cost);
            }
        }



        /*
        public static void Postfix_Constructor(PathFinder __instance, Map map)
        {
            int num = mapSizeX(__instance) * mapSizeZ(__instance);
            calcGridDict[__instance] = new PathFinderNodeFast[num];
            openListDict[__instance] = new FastPriorityQueue<CostNode>(new CostNodeComparer());
        }
        */
        public static bool FindPath(PathFinder __instance, ref PawnPath __result, IntVec3 start, LocalTargetInfo dest, TraverseParms traverseParms, PathEndMode peMode = PathEndMode.OnCell)
        {
            Map map = mapField(__instance);
            int mapSizeX = map.Size.x;
            int mapSizeZ = map.Size.z;
            PathFinderNodeFast[] calcGrid2 = new PathFinderNodeFast[mapSizeX * mapSizeZ];
            ushort statusOpenValue2 = 1;
            ushort statusClosedValue2 = 2;

            FastPriorityQueue<CostNode> openList = new FastPriorityQueue<CostNode>(new CostNodeComparer()); //CHANGE
            RegionCostCalculatorWrapper regionCostCalculator = new RegionCostCalculatorWrapper(map); //CHANGE
            List<int> disallowedCornerIndices = new List<int>(4); //CHANGE

            if (DebugSettings.pathThroughWalls)
            {
                traverseParms.mode = TraverseMode.PassAllDestroyableThings;
            }

            Pawn pawn = traverseParms.pawn;
            if (pawn != null && pawn.Map != map)
            {
                Log.Error("Tried to FindPath for pawn which is spawned in another map. His map PathFinder should have been used, not this one. pawn=" + pawn + " pawn.Map=" + pawn.Map + " map=" + map);
                __result = PawnPath.NotFound;
                return false;
            }

            if (!start.IsValid)
            {
                Log.Error("Tried to FindPath with invalid start " + start + ", pawn= " + pawn);
                __result = PawnPath.NotFound;
                return false;
            }

            if (!dest.IsValid)
            {
                Log.Error("Tried to FindPath with invalid dest " + dest + ", pawn= " + pawn);
                __result = PawnPath.NotFound;
                return false;
            }

            if (traverseParms.mode == TraverseMode.ByPawn)
            {
                if (!pawn.CanReach(dest, peMode, Danger.Deadly, traverseParms.canBash, traverseParms.mode))
                {
                    __result = PawnPath.NotFound;
                    return false;
                }
            }
            else if (!map.reachability.CanReach(start, dest, peMode, traverseParms))
            {
                __result = PawnPath.NotFound;
                return false;
            }

            PfProfilerBeginSample("FindPath for " + pawn + " from " + start + " to " + dest + (dest.HasThing ? (" at " + dest.Cell) : ""));
            CellIndices cellIndices = map.cellIndices; //CHANGE
            PathGrid pathGrid = map.pathGrid;//CHANGE
            Building[] this_edificeGrid = map.edificeGrid.InnerArray;//CHANGE
            List<Blueprint>[] blueprintGrid = map.blueprintGrid.InnerArray;//CHANGE
            int x = dest.Cell.x;
            int z = dest.Cell.z;
            int curIndex = cellIndices.CellToIndex(start);
            int num = cellIndices.CellToIndex(dest.Cell);
            ByteGrid byteGrid = pawn?.GetAvoidGrid();
            bool flag = traverseParms.mode == TraverseMode.PassAllDestroyableThings || traverseParms.mode == TraverseMode.PassAllDestroyableThingsNotWater;
            bool flag2 = traverseParms.mode != TraverseMode.NoPassClosedDoorsOrWater && traverseParms.mode != TraverseMode.PassAllDestroyableThingsNotWater;
            bool flag3 = !flag;
            CellRect cellRect = CalculateDestinationRect(dest, peMode);
            bool flag4 = cellRect.Width == 1 && cellRect.Height == 1;
            int[] array = map.pathGrid.pathGrid;
            TerrainDef[] topGrid = map.terrainGrid.topGrid;
            EdificeGrid edificeGrid = map.edificeGrid;
            int num2 = 0;
            int num3 = 0;
            Area allowedArea = GetAllowedArea(pawn);
            bool flag5 = pawn != null && PawnUtility.ShouldCollideWithPawns(pawn);
            bool flag6 = (!flag && start.GetRegion(map) != null) & flag2;
            bool flag7 = !flag || !flag3;
            bool flag8 = false;
            bool flag9 = pawn?.Drafted ?? false;
            int num4 = (pawn?.IsColonist ?? false) ? 100000 : 2000;
            int num5 = 0;
            int num6 = 0;
            float num7 = DetermineHeuristicStrength(pawn, start, dest);
            int num8;
            int num9;
            if (pawn != null)
            {
                num8 = pawn.TicksPerMoveCardinal;
                num9 = pawn.TicksPerMoveDiagonal;
            }
            else
            {
                num8 = 13;
                num9 = 18;
            }

            //CalculateAndAddDisallowedCorners(traverseParms, peMode, cellRect);
            //InitStatusesAndPushStartNode(ref curIndex, start);
            ///---START INSERT---
            disallowedCornerIndices.Clear();
            if (peMode == PathEndMode.Touch)
            {
                int minX = cellRect.minX;
                int minZ = cellRect.minZ;
                int maxX = cellRect.maxX;
                int maxZ = cellRect.maxZ;
                if (!TouchPathEndModeUtility.IsCornerTouchAllowed(minX + 1, minZ + 1, minX + 1, minZ, minX, minZ + 1, map))
                {
                    disallowedCornerIndices.Add(map.cellIndices.CellToIndex(minX, minZ));
                }

                if (!TouchPathEndModeUtility.IsCornerTouchAllowed(minX + 1, maxZ - 1, minX + 1, maxZ, minX, maxZ - 1, map))
                {
                    disallowedCornerIndices.Add(map.cellIndices.CellToIndex(minX, maxZ));
                }

                if (!TouchPathEndModeUtility.IsCornerTouchAllowed(maxX - 1, maxZ - 1, maxX - 1, maxZ, maxX, maxZ - 1, map))
                {
                    disallowedCornerIndices.Add(map.cellIndices.CellToIndex(maxX, maxZ));
                }

                if (!TouchPathEndModeUtility.IsCornerTouchAllowed(maxX - 1, minZ + 1, maxX - 1, minZ, maxX, minZ + 1, map))
                {
                    disallowedCornerIndices.Add(map.cellIndices.CellToIndex(maxX, minZ));
                }
            }
            statusOpenValue2 += 2;
            statusClosedValue2 += 2;
            if (statusClosedValue2 >= 65435)
            {
                for (int i = 0; i < calcGrid2.Length; i++)
                {
                    calcGrid2[i].status = 0;
                }

                statusOpenValue2 = 1;
                statusClosedValue2 = 2;
            }

            curIndex = cellIndices.CellToIndex(start);
            calcGrid2[curIndex].knownCost = 0;
            calcGrid2[curIndex].heuristicCost = 0;
            calcGrid2[curIndex].costNodeCost = 0;
            calcGrid2[curIndex].parentIndex = curIndex;
            calcGrid2[curIndex].status = statusOpenValue2;
            openList.Clear();
            openList.Push(new CostNode(curIndex, 0));
            ///---END INSERT---
            while (true)
            {
                PfProfilerBeginSample("Open cell");
                if (openList.Count <= 0)
                {
                    string text = (pawn != null && pawn.CurJob != null) ? pawn.CurJob.ToString() : "null";
                    string text2 = (pawn != null && pawn.Faction != null) ? pawn.Faction.ToString() : "null";
                    Log.Warning(pawn + " pathing from " + start + " to " + dest + " ran out of cells to process.\nJob:" + text + "\nFaction: " + text2);
                    DebugDrawRichData();
                    PfProfilerEndSample();
                    PfProfilerEndSample();
                    __result = PawnPath.NotFound;
                    return false;
                }

                num5 += openList.Count;
                num6++;
                CostNode costNode = openList.Pop();
                curIndex = costNode.index;
                if (costNode.cost != calcGrid2[curIndex].costNodeCost) //CHANGE
                {
                    PfProfilerEndSample();
                    continue;
                }

                if (calcGrid2[curIndex].status == statusClosedValue2) //CHANGE
                {
                    PfProfilerEndSample();
                    continue;
                }

                IntVec3 c = cellIndices.IndexToCell(curIndex);
                int x2 = c.x;
                int z2 = c.z;
                if (flag4)
                {
                    if (curIndex == num)
                    {
                        PfProfilerEndSample();
                        //PawnPath result = FinalizedPath(curIndex, flag8);
                        //---START INSERT---
                        PawnPath emptyPawnPath = new PawnPath();
                        int num1 = curIndex;
                        while (true)
                        {
                            int parentIndex = calcGrid2[num1].parentIndex;
                            emptyPawnPath.AddNode(map.cellIndices.IndexToCell(num1));
                            if (num1 == parentIndex)
                            {
                                break;
                            }
                            num1 = parentIndex;
                        }
                        emptyPawnPath.SetupFound(calcGrid2[curIndex].knownCost, flag8);
                        //---END INSERT---
                        PfProfilerEndSample();
                        __result = emptyPawnPath;
                        return false;
                    }
                }
                else if (cellRect.Contains(c) && !disallowedCornerIndices.Contains(curIndex))
                {
                    PfProfilerEndSample();
                    //PawnPath result2 = FinalizedPath(curIndex, flag8);
                    //---START INSERT---
                    PawnPath emptyPawnPath = new PawnPath();
                    int num1 = curIndex;
                    while (true)
                    {
                        int parentIndex = calcGrid2[num1].parentIndex;
                        emptyPawnPath.AddNode(map.cellIndices.IndexToCell(num1));
                        if (num1 == parentIndex)
                        {
                            break;
                        }
                        num1 = parentIndex;
                    }
                    emptyPawnPath.SetupFound(calcGrid2[curIndex].knownCost, flag8);
                    //---END INSERT---
                    PfProfilerEndSample();
                    __result = emptyPawnPath;
                    return false;
                }

                if (num2 > 160000)
                {
                    break;
                }

                PfProfilerEndSample();
                PfProfilerBeginSample("Neighbor consideration");
                for (int i = 0; i < 8; i++)
                {
                    uint num10 = (uint)(x2 + Directions[i]);
                    uint num11 = (uint)(z2 + Directions[i + 8]);
                    if (num10 >= mapSizeX || num11 >= mapSizeZ)
                    {
                        continue;
                    }

                    int num12 = (int)num10;
                    int num13 = (int)num11;
                    int num14 = cellIndices.CellToIndex(num12, num13);
                    if (calcGrid2[num14].status == statusClosedValue2 && !flag8) //CHANGE
                    {
                        continue;
                    }

                    int num15 = 0;
                    bool flag10 = false;
                    if (!flag2 && new IntVec3(num12, 0, num13).GetTerrain(map).HasTag("Water"))
                    {
                        continue;
                    }

                    if (!pathGrid.WalkableFast(num14))
                    {
                        if (!flag)
                        {
                            continue;
                        }

                        flag10 = true;
                        num15 += 70;
                        Building building = edificeGrid[num14];
                        if (building == null || !PathFinder.IsDestroyable(building))
                        {
                            continue;
                        }

                        num15 += (int)((float)building.HitPoints * 0.2f);
                    }

                    switch (i)
                    {
                        case 4:
                            if (PathFinder.BlocksDiagonalMovement(curIndex - mapSizeX, map))
                            {
                                if (flag7)
                                {
                                    continue;
                                }

                                num15 += 70;
                            }

                            if (PathFinder.BlocksDiagonalMovement(curIndex + 1, map))
                            {
                                if (flag7)
                                {
                                    continue;
                                }

                                num15 += 70;
                            }

                            break;
                        case 5:
                            if (PathFinder.BlocksDiagonalMovement(curIndex + mapSizeX, map))
                            {
                                if (flag7)
                                {
                                    continue;
                                }

                                num15 += 70;
                            }

                            if (PathFinder.BlocksDiagonalMovement(curIndex + 1, map))
                            {
                                if (flag7)
                                {
                                    continue;
                                }

                                num15 += 70;
                            }

                            break;
                        case 6:
                            if (PathFinder.BlocksDiagonalMovement(curIndex + mapSizeX, map))
                            {
                                if (flag7)
                                {
                                    continue;
                                }

                                num15 += 70;
                            }

                            if (PathFinder.BlocksDiagonalMovement(curIndex - 1, map))
                            {
                                if (flag7)
                                {
                                    continue;
                                }

                                num15 += 70;
                            }

                            break;
                        case 7:
                            if (PathFinder.BlocksDiagonalMovement(curIndex - mapSizeX, map))
                            {
                                if (flag7)
                                {
                                    continue;
                                }

                                num15 += 70;
                            }

                            if (PathFinder.BlocksDiagonalMovement(curIndex - 1, map))
                            {
                                if (flag7)
                                {
                                    continue;
                                }

                                num15 += 70;
                            }

                            break;
                    }

                    int num16 = (i > 3) ? num9 : num8;
                    num16 += num15;
                    if (!flag10)
                    {
                        num16 += array[num14];
                        num16 = ((!flag9) ? (num16 + topGrid[num14].extraNonDraftedPerceivedPathCost) : (num16 + topGrid[num14].extraDraftedPerceivedPathCost));
                    }

                    if (byteGrid != null)
                    {
                        num16 += byteGrid[num14] * 8;
                    }

                    if (allowedArea != null && !allowedArea[num14])
                    {
                        num16 += 600;
                    }

                    if (flag5 && PawnUtility.AnyPawnBlockingPathAt(new IntVec3(num12, 0, num13), pawn, actAsIfHadCollideWithPawnsJob: false, collideOnlyWithStandingPawns: false, forPathFinder: true))
                    {
                        num16 += 175;
                    }

                    Building building2 = this_edificeGrid[num14]; //CHANGE
                    if (building2 != null)
                    {
                        PfProfilerBeginSample("Edifices");
                        int buildingCost = PathFinder.GetBuildingCost(building2, traverseParms, pawn);
                        if (buildingCost == int.MaxValue)
                        {
                            PfProfilerEndSample();
                            continue;
                        }

                        num16 += buildingCost;
                        PfProfilerEndSample();
                    }

                    List<Blueprint> list = blueprintGrid[num14];
                    if (list != null)
                    {
                        PfProfilerBeginSample("Blueprints");
                        int num17 = 0;
                        for (int j = 0; j < list.Count; j++)
                        {
                            num17 = Mathf.Max(num17, PathFinder.GetBlueprintCost(list[j], pawn));
                        }

                        if (num17 == int.MaxValue)
                        {
                            PfProfilerEndSample();
                            continue;
                        }

                        num16 += num17;
                        PfProfilerEndSample();
                    }

                    int num18 = num16 + calcGrid2[curIndex].knownCost; //CHANGE
                    ushort status = calcGrid2[num14].status; //CHANGE
                    if (status == statusClosedValue2 || status == statusOpenValue2) //CHANGE
                    {
                        int num19 = 0;
                        if (status == statusClosedValue2) //CHANGE
                        {
                            num19 = num8;
                        }

                        if (calcGrid2[num14].knownCost <= num18 + num19) //CHANGE
                        {
                            continue;
                        }
                    }

                    if (flag8)
                    {
                        //CHANGE
                        calcGrid2[num14].heuristicCost = Mathf.RoundToInt((float)regionCostCalculator.GetPathCostFromDestToRegion(num14) * RegionHeuristicWeightByNodesOpened.Evaluate(num3));
                        if (calcGrid2[num14].heuristicCost < 0) //CHANGE
                        {
                            Log.ErrorOnce("Heuristic cost overflow for " + pawn.ToStringSafe() + " pathing from " + start + " to " + dest + ".", pawn.GetHashCode() ^ 0xB8DC389);
                            calcGrid2[num14].heuristicCost = 0; //CHANGE
                        }
                    }
                    else if (status != statusClosedValue2 && status != statusOpenValue2) //CHANGE
                    {
                        int dx = Math.Abs(num12 - x);
                        int dz = Math.Abs(num13 - z);
                        int num20 = GenMath.OctileDistance(dx, dz, num8, num9);
                        calcGrid2[num14].heuristicCost = Mathf.RoundToInt((float)num20 * num7); //CHANGE
                    }

                    int num21 = num18 + calcGrid2[num14].heuristicCost; //CHANGE
                    if (num21 < 0)
                    {
                        Log.ErrorOnce("Node cost overflow for " + pawn.ToStringSafe() + " pathing from " + start + " to " + dest + ".", pawn.GetHashCode() ^ 0x53CB9DE);
                        num21 = 0;
                    }

                    calcGrid2[num14].parentIndex = curIndex; //CHANGE
                    calcGrid2[num14].knownCost = num18; //CHANGE
                    calcGrid2[num14].status = statusOpenValue2; //CHANGE
                    calcGrid2[num14].costNodeCost = num21; //CHANGE
                    num3++;
                    openList.Push(new CostNode(num14, num21));
                }

                PfProfilerEndSample();
                num2++;
                calcGrid2[curIndex].status = statusClosedValue2; //CHANGE
                if (num3 >= num4 && flag6 && !flag8)
                {
                    flag8 = true;
                    regionCostCalculator.Init(cellRect, traverseParms, num8, num9, byteGrid, allowedArea, flag9, disallowedCornerIndices);
                    //InitStatusesAndPushStartNode(ref curIndex, start);
                    //---START INSERT
                    statusOpenValue2 += 2; //CHANGE
                    statusClosedValue2 += 2; //CHANGE
                    if (statusClosedValue2 >= 65435) //CHANGE
                    {
                        for (int i = 0; i < calcGrid2.Length; i++) //CHANGE
                        {
                            calcGrid2[i].status = 0; //CHANGE
                        }

                        statusOpenValue2 = 1; //CHANGE
                        statusClosedValue2 = 2; //CHANGE
                    }

                    curIndex = cellIndices.CellToIndex(start);
                    calcGrid2[curIndex].knownCost = 0; //CHANGE
                    calcGrid2[curIndex].heuristicCost = 0; //CHANGE
                    calcGrid2[curIndex].costNodeCost = 0; //CHANGE
                    calcGrid2[curIndex].parentIndex = curIndex; //CHANGE
                    calcGrid2[curIndex].status = statusOpenValue2; //CHANGE
                    openList.Clear();
                    openList.Push(new CostNode(curIndex, 0));
                    //---END INSERT
                    num3 = 0;
                    num2 = 0;
                }
            }

            Log.Warning(pawn + " pathing from " + start + " to " + dest + " hit search limit of " + 160000 + " cells.");
            DebugDrawRichData();
            PfProfilerEndSample();
            PfProfilerEndSample();
            __result = PawnPath.NotFound;
            return false;
        }

    }
}
