﻿// COPYRIGHT 2009, 2010, 2011, 2012, 2013 by the Open Rails project.
// 
// This file is part of Open Rails.
// 
// Open Rails is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// 
// Open Rails is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
// 
// You should have received a copy of the GNU General Public License
// along with Open Rails.  If not, see <http://www.gnu.org/licenses/>.

// This file is the responsibility of the 3D & Environment Team. 

// Define this to check every material is resetting the RenderState correctly.
//#define DEBUG_RENDER_STATE

// Define this to enable sorting of blended render primitives. This is a
// complex feature and performance is not guaranteed.
#define RENDER_BLEND_SORTING

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ORTS.Processes;
using Game = ORTS.Processes.Game;

namespace ORTS.Viewer3D
{
    public enum RenderPrimitiveSequence
    {
        CabOpaque,
        Sky,
        WorldOpaque,
        WorldBlended,
        Lights, // TODO: May not be needed once alpha sorting works.
        Precipitation, // TODO: May not be needed once alpha sorting works.
        Particles,
        CabBlended,
        TextOverlayOpaque,
        TextOverlayBlended,
        // This value must be last.
        Sentinel
    }

    public enum RenderPrimitiveGroup
    {
        Cab,
        Sky,
        World,
        Lights, // TODO: May not be needed once alpha sorting works.
        Precipitation, // TODO: May not be needed once alpha sorting works.
        Particles,
        Overlay
    }

    public abstract class RenderPrimitive
    {
        public static readonly RenderPrimitiveSequence[] SequenceForBlended = new[] {
			RenderPrimitiveSequence.CabBlended,
            RenderPrimitiveSequence.Sky,
			RenderPrimitiveSequence.WorldBlended,
			RenderPrimitiveSequence.Lights,
			RenderPrimitiveSequence.Precipitation,
            RenderPrimitiveSequence.Particles,
			RenderPrimitiveSequence.TextOverlayBlended,
		};
        public static readonly RenderPrimitiveSequence[] SequenceForOpaque = new[] {
			RenderPrimitiveSequence.CabOpaque,
            RenderPrimitiveSequence.Sky,
			RenderPrimitiveSequence.WorldOpaque,
			RenderPrimitiveSequence.Lights,
			RenderPrimitiveSequence.Precipitation,
            RenderPrimitiveSequence.Particles,
			RenderPrimitiveSequence.TextOverlayOpaque,
		};

        /// <summary>
        /// This is an adjustment for the depth buffer calculation which may be used to reduce the chance of co-planar primitives from fighting each other.
        /// </summary>
        // TODO: Does this actually make any real difference?
        public float ZBias;

        /// <summary>
        /// This is a sorting adjustment for primitives with similar/the same world location. Primitives with higher SortIndex values are rendered after others. Has no effect on non-blended primitives.
        /// </summary>
        public float SortIndex;

        /// <summary>
        /// This is when the object actually renders itself onto the screen.
        /// Do not reference any volatile data.
        /// Executes in the RenderProcess thread
        /// </summary>
        /// <param name="graphicsDevice"></param>
        public abstract void Draw(GraphicsDevice graphicsDevice);
    }

    [DebuggerDisplay("{Material} {RenderPrimitive} {Flags}")]
    public class RenderItem
    {
        public readonly Material Material;
        public readonly RenderPrimitive RenderPrimitive;
        public Matrix XNAMatrix;
        public readonly ShapeFlags Flags;

        public RenderItem(Material material, RenderPrimitive renderPrimitive, ref Matrix xnaMatrix, ShapeFlags flags)
        {
            Material = material;
            RenderPrimitive = renderPrimitive;
            XNAMatrix = xnaMatrix;
            Flags = flags;
        }

        public class Comparer : IComparer<RenderItem>
        {
            readonly Vector3 XNAViewerPos;

            public Comparer(Vector3 viewerPos)
            {
                XNAViewerPos = viewerPos;
                XNAViewerPos.Z *= -1;
            }

            #region IComparer<RenderItem> Members

            public int Compare(RenderItem x, RenderItem y)
            {
                var xd = (x.XNAMatrix.Translation - XNAViewerPos).Length() - x.RenderPrimitive.SortIndex;
                var yd = (y.XNAMatrix.Translation - XNAViewerPos).Length() - y.RenderPrimitive.SortIndex;
                return Math.Sign(yd - xd);
            }

            #endregion
        }
    }

    public class RenderFrame
    {
        readonly Game Game;

        // Shared shadow map data.
        static Texture2D[] ShadowMap;
        static RenderTarget2D[] ShadowMapRenderTarget;
        static DepthStencilBuffer ShadowMapStencilBuffer;
        static DepthStencilBuffer NormalStencilBuffer;
        static Vector3 SteppedSolarDirection = Vector3.UnitX;

        // Local shadow map data.
        Matrix[] ShadowMapLightView;
        Matrix[] ShadowMapLightProj;
        Matrix[] ShadowMapLightViewProjShadowProj;
        Vector3 ShadowMapX;
        Vector3 ShadowMapY;
        Vector3[] ShadowMapCenter;

        readonly Material DummyBlendedMaterial;
        readonly Dictionary<Material, List<RenderItem>>[] RenderItems = new Dictionary<Material, List<RenderItem>>[(int)RenderPrimitiveSequence.Sentinel];
        readonly List<RenderItem>[] RenderShadowItems;

        public bool IsScreenChanged { get; internal set; }
        ShadowMapMaterial ShadowMapMaterial;
        SceneryShader SceneryShader;
        Vector3 SolarDirection;
        Camera Camera;
        Vector3 CameraLocation;
        Vector3 XNACameraLocation;
        Matrix XNACameraView;
        Matrix XNACameraProjection;

        public RenderFrame(Game game)
        {
            Game = game;
            DummyBlendedMaterial = new EmptyMaterial(null);

            for (int i = 0; i < RenderItems.Length; i++)
                RenderItems[i] = new Dictionary<Material, List<RenderItem>>();

            if (Game.Settings.DynamicShadows)
            {
                if (ShadowMap == null)
                {
                    var shadowMapSize = Game.Settings.ShadowMapResolution;
                    ShadowMap = new Texture2D[RenderProcess.ShadowMapCount];
                    ShadowMapRenderTarget = new RenderTarget2D[RenderProcess.ShadowMapCount];
                    for (var shadowMapIndex = 0; shadowMapIndex < RenderProcess.ShadowMapCount; shadowMapIndex++)
                        ShadowMapRenderTarget[shadowMapIndex] = new RenderTarget2D(Game.RenderProcess.GraphicsDevice, shadowMapSize, shadowMapSize, RenderProcess.ShadowMapMipCount, SurfaceFormat.Rg32, RenderTargetUsage.PreserveContents);
                    ShadowMapStencilBuffer = new DepthStencilBuffer(Game.RenderProcess.GraphicsDevice, shadowMapSize, shadowMapSize, DepthFormat.Depth16);
                    NormalStencilBuffer = Game.RenderProcess.GraphicsDevice.DepthStencilBuffer;
                }

                ShadowMapLightView = new Matrix[RenderProcess.ShadowMapCount];
                ShadowMapLightProj = new Matrix[RenderProcess.ShadowMapCount];
                ShadowMapLightViewProjShadowProj = new Matrix[RenderProcess.ShadowMapCount];
                ShadowMapCenter = new Vector3[RenderProcess.ShadowMapCount];

                RenderShadowItems = new List<RenderItem>[RenderProcess.ShadowMapCount];
                for (var shadowMapIndex = 0; shadowMapIndex < RenderProcess.ShadowMapCount; shadowMapIndex++)
                    RenderShadowItems[shadowMapIndex] = new List<RenderItem>();
            }

            XNACameraView = Matrix.Identity;
            XNACameraProjection = Matrix.CreateOrthographic(game.RenderProcess.DisplaySize.X, game.RenderProcess.DisplaySize.Y, 1, 100);
        }

        public void Clear()
        {
            for (int i = 0; i < RenderItems.Length; i++)
                foreach (Material mat in RenderItems[i].Keys)
                    RenderItems[i][mat].Clear();
            for (int i = 0; i < RenderItems.Length; i++)
                RenderItems[i].Clear();
            if (Game.Settings.DynamicShadows)
                for (var shadowMapIndex = 0; shadowMapIndex < RenderProcess.ShadowMapCount; shadowMapIndex++)
                    RenderShadowItems[shadowMapIndex].Clear();
        }

        public void PrepareFrame(Viewer viewer)
        {
            if (viewer.Settings.UseMSTSEnv == false)
                SolarDirection = viewer.World.Sky.solarDirection;
            else
                SolarDirection = viewer.World.MSTSSky.mstsskysolarDirection;

            if (ShadowMapMaterial == null)
                ShadowMapMaterial = (ShadowMapMaterial)viewer.MaterialManager.Load("ShadowMap");
            if (SceneryShader == null)
                SceneryShader = viewer.MaterialManager.SceneryShader;
        }

        public void SetCamera(Camera camera)
        {
            Camera = camera;
            XNACameraLocation = CameraLocation = Camera.Location;
            XNACameraLocation.Z *= -1;
            XNACameraView = Camera.XnaView;
            XNACameraProjection = Camera.XnaProjection;
        }

        static bool LockShadows;
        [CallOnThread("Updater")]
        public void PrepareFrame(ElapsedTime elapsedTime)
        {
            if (UserInput.IsPressed(UserCommands.DebugLockShadows))
                LockShadows = !LockShadows;

            if (Game.Settings.DynamicShadows && (RenderProcess.ShadowMapCount > 0) && !LockShadows)
            {
                var solarDirection = SolarDirection;
                solarDirection.Normalize();
                if (Vector3.Dot(SteppedSolarDirection, solarDirection) < 0.99999)
                    SteppedSolarDirection = solarDirection;

                var cameraDirection = new Vector3(-XNACameraView.M13, -XNACameraView.M23, -XNACameraView.M33);
                cameraDirection.Normalize();

                var shadowMapAlignAxisX = Vector3.Cross(SteppedSolarDirection, Vector3.UnitY);
                var shadowMapAlignAxisY = Vector3.Cross(shadowMapAlignAxisX, SteppedSolarDirection);
                shadowMapAlignAxisX.Normalize();
                shadowMapAlignAxisY.Normalize();
                ShadowMapX = shadowMapAlignAxisX;
                ShadowMapY = shadowMapAlignAxisY;

                for (var shadowMapIndex = 0; shadowMapIndex < RenderProcess.ShadowMapCount; shadowMapIndex++)
                {
                    var viewingDistance = Game.Settings.ViewingDistance;
                    var shadowMapDiameter = RenderProcess.ShadowMapDiameter[shadowMapIndex];
                    var shadowMapLocation = XNACameraLocation + RenderProcess.ShadowMapDistance[shadowMapIndex] * cameraDirection;

                    // Align shadow map location to grid so it doesn't "flutter" so much. This basically means aligning it along a
                    // grid based on the size of a shadow texel (shadowMapSize / shadowMapSize) along the axes of the sun direction
                    // and up/left.
                    var shadowMapAlignmentGrid = (float)shadowMapDiameter / Game.Settings.ShadowMapResolution;
                    var adjustX = (float)Math.IEEERemainder(Vector3.Dot(shadowMapAlignAxisX, shadowMapLocation), shadowMapAlignmentGrid);
                    var adjustY = (float)Math.IEEERemainder(Vector3.Dot(shadowMapAlignAxisY, shadowMapLocation), shadowMapAlignmentGrid);
                    shadowMapLocation.X -= shadowMapAlignAxisX.X * adjustX;
                    shadowMapLocation.Y -= shadowMapAlignAxisX.Y * adjustX;
                    shadowMapLocation.Z -= shadowMapAlignAxisX.Z * adjustX;
                    shadowMapLocation.X -= shadowMapAlignAxisY.X * adjustY;
                    shadowMapLocation.Y -= shadowMapAlignAxisY.Y * adjustY;
                    shadowMapLocation.Z -= shadowMapAlignAxisY.Z * adjustY;

                    ShadowMapLightView[shadowMapIndex] = Matrix.CreateLookAt(shadowMapLocation + viewingDistance * SteppedSolarDirection, shadowMapLocation, Vector3.Up);
                    ShadowMapLightProj[shadowMapIndex] = Matrix.CreateOrthographic(shadowMapDiameter, shadowMapDiameter, 0, viewingDistance + shadowMapDiameter / 2);
                    ShadowMapLightViewProjShadowProj[shadowMapIndex] = ShadowMapLightView[shadowMapIndex] * ShadowMapLightProj[shadowMapIndex] * new Matrix(0.5f, 0, 0, 0, 0, -0.5f, 0, 0, 0, 0, 1, 0, 0.5f + 0.5f / ShadowMapStencilBuffer.Width, 0.5f + 0.5f / ShadowMapStencilBuffer.Height, 0, 1);
                    ShadowMapCenter[shadowMapIndex] = shadowMapLocation;
                }
            }
        }

        /// <summary>
        /// Automatically adds or culls a <see cref="RenderPrimitive"/> based on a location, radius and max viewing distance.
        /// </summary>
        /// <param name="mstsLocation">Center location of the <see cref="RenderPrimitive"/> in MSTS coordinates.</param>
        /// <param name="objectRadius">Radius of a sphere containing the whole <see cref="RenderPrimitive"/>, centered on <paramref name="mstsLocation"/>.</param>
        /// <param name="objectViewingDistance">Maximum distance from which the <see cref="RenderPrimitive"/> should be viewable.</param>
        /// <param name="material"></param>
        /// <param name="primitive"></param>
        /// <param name="group"></param>
        /// <param name="xnaMatrix"></param>
        /// <param name="flags"></param>
        [CallOnThread("Updater")]
        public void AddAutoPrimitive(Vector3 mstsLocation, float objectRadius, float objectViewingDistance, Material material, RenderPrimitive primitive, RenderPrimitiveGroup group, ref Matrix xnaMatrix, ShapeFlags flags)
        {
            if (float.IsPositiveInfinity(objectViewingDistance) || (Camera != null && Camera.InRange(mstsLocation, objectRadius, objectViewingDistance)))
            {
                if (Camera != null && Camera.InFov(mstsLocation, objectRadius))
                    AddPrimitive(material, primitive, group, ref xnaMatrix, flags);

                if (Game.Settings.DynamicShadows && (RenderProcess.ShadowMapCount > 0) && ((flags & ShapeFlags.ShadowCaster) != 0))
                    for (var shadowMapIndex = 0; shadowMapIndex < RenderProcess.ShadowMapCount; shadowMapIndex++)
                        if (IsInShadowMap(shadowMapIndex, mstsLocation, objectRadius, objectViewingDistance))
                            AddShadowPrimitive(shadowMapIndex, material, primitive, ref xnaMatrix, flags);
            }
        }

        [CallOnThread("Updater")]
        public void AddPrimitive(Material material, RenderPrimitive primitive, RenderPrimitiveGroup group, ref Matrix xnaMatrix)
        {
            AddPrimitive(material, primitive, group, ref xnaMatrix, ShapeFlags.None);
        }

        [CallOnThread("Updater")]
        public void AddPrimitive(Material material, RenderPrimitive primitive, RenderPrimitiveGroup group, ref Matrix xnaMatrix, ShapeFlags flags)
        {
            List<RenderItem> items;
            bool[] blending;

            bool getBlending = material.GetBlending();
            if (getBlending && material is SceneryMaterial)
                blending = new bool[] { true, false }; // Search for opaque pixels in alpha blended primitives, thus maintaining correct DepthBuffer
            else
                blending = new bool[] {getBlending};

            foreach (bool blended in blending)
            {
                var sortingMaterial = blended ? DummyBlendedMaterial : material;
                var sequence = RenderItems[(int)GetRenderSequence(group, blended)];

                if (!sequence.TryGetValue(sortingMaterial, out items))
                {
                    items = new List<RenderItem>();
                    sequence.Add(sortingMaterial, items);
                }
                items.Add(new RenderItem(material, primitive, ref xnaMatrix, flags));
            }
            if (((flags & ShapeFlags.AutoZBias) != 0) && (primitive.ZBias == 0))
                primitive.ZBias = 1;
        }

        [CallOnThread("Updater")]
        void AddShadowPrimitive(int shadowMapIndex, Material material, RenderPrimitive primitive, ref Matrix xnaMatrix, ShapeFlags flags)
        {
            RenderShadowItems[shadowMapIndex].Add(new RenderItem(material, primitive, ref xnaMatrix, flags));
        }

        [CallOnThread("Updater")]
        public void Sort()
        {
            var renderItemComparer = new RenderItem.Comparer(CameraLocation);
            foreach (var sequence in RenderItems)
            {
                foreach (var sequenceMaterial in sequence.Where(kvp => kvp.Value.Count > 0))
                {
                    if (sequenceMaterial.Key != DummyBlendedMaterial)
                        continue;
                    sequenceMaterial.Value.Sort(renderItemComparer);
                }
            }
        }

        bool IsInShadowMap(int shadowMapIndex, Vector3 mstsLocation, float objectRadius, float objectViewingDistance)
        {
            if (ShadowMapRenderTarget == null)
                return false;

            mstsLocation.Z *= -1;
            mstsLocation.X -= ShadowMapCenter[shadowMapIndex].X;
            mstsLocation.Y -= ShadowMapCenter[shadowMapIndex].Y;
            mstsLocation.Z -= ShadowMapCenter[shadowMapIndex].Z;
            objectRadius += RenderProcess.ShadowMapDiameter[shadowMapIndex] / 2;

            // Check if object is inside the sphere.
            var length = mstsLocation.LengthSquared();
            if (length <= objectRadius * objectRadius)
                return true;

            // Check if object is inside cylinder.
            var dotX = Math.Abs(Vector3.Dot(mstsLocation, ShadowMapX));
            if (dotX > objectRadius)
                return false;

            var dotY = Math.Abs(Vector3.Dot(mstsLocation, ShadowMapY));
            if (dotY > objectRadius)
                return false;

            // Check if object is on correct side of center.
            var dotZ = Vector3.Dot(mstsLocation, SteppedSolarDirection);
            if (dotZ < 0)
                return false;

            return true;
        }

        static RenderPrimitiveSequence GetRenderSequence(RenderPrimitiveGroup group, bool blended)
        {
            if (blended)
                return RenderPrimitive.SequenceForBlended[(int)group];
            return RenderPrimitive.SequenceForOpaque[(int)group];
        }

        [CallOnThread("Render")]
        public void Draw(GraphicsDevice graphicsDevice)
        {
#if DEBUG_RENDER_STATE
			DebugRenderState(graphicsDevice.RenderState, "RenderFrame.Draw");
#endif
            var logging = UserInput.IsPressed(UserCommands.DebugLogRenderFrame);
            if (logging)
            {
                Console.WriteLine();
                Console.WriteLine();
                Console.WriteLine("Draw {");
            }

            if (Game.Settings.DynamicShadows && (RenderProcess.ShadowMapCount > 0) && ShadowMapMaterial != null)
                DrawShadows(graphicsDevice, logging);

            DrawSimple(graphicsDevice, logging);

            for (var i = 0; i < (int)RenderPrimitiveSequence.Sentinel; i++)
                Game.RenderProcess.PrimitiveCount[i] = RenderItems[i].Values.Sum(l => l.Count);

            if (logging)
            {
                Console.WriteLine("}");
                Console.WriteLine();
            }
        }

        void DrawShadows( GraphicsDevice graphicsDevice, bool logging )
        {
            if (logging) Console.WriteLine("  DrawShadows {");
            for (var shadowMapIndex = 0; shadowMapIndex < RenderProcess.ShadowMapCount; shadowMapIndex++)
                DrawShadows(graphicsDevice, logging, shadowMapIndex);
            for (var shadowMapIndex = 0; shadowMapIndex < RenderProcess.ShadowMapCount; shadowMapIndex++)
                Game.RenderProcess.ShadowPrimitiveCount[shadowMapIndex] = RenderShadowItems[shadowMapIndex].Count;
            if (logging) Console.WriteLine("  }");
        }

        void DrawShadows(GraphicsDevice graphicsDevice, bool logging, int shadowMapIndex)
        {
            if (logging) Console.WriteLine("    {0} {{", shadowMapIndex);

            // Prepare renderer for drawing the shadow map.
            graphicsDevice.SetRenderTarget(0, ShadowMapRenderTarget[shadowMapIndex]);
            graphicsDevice.DepthStencilBuffer = ShadowMapStencilBuffer;
            graphicsDevice.Clear(ClearOptions.DepthBuffer, Color.Black, 1, 0);

            // Prepare for normal (non-blocking) rendering of scenery.
            ShadowMapMaterial.SetState(graphicsDevice, ShadowMapMaterial.Mode.Normal);

            // Render non-terrain, non-forest shadow items first.
            if (logging) Console.WriteLine("      {0,-5} * SceneryMaterial (normal)", RenderShadowItems[shadowMapIndex].Count(ri => ri.Material is SceneryMaterial));
            ShadowMapMaterial.Render(graphicsDevice, RenderShadowItems[shadowMapIndex].Where(ri => ri.Material is SceneryMaterial), ref ShadowMapLightView[shadowMapIndex], ref ShadowMapLightProj[shadowMapIndex]);

            // Prepare for normal (non-blocking) rendering of forests.
            ShadowMapMaterial.SetState(graphicsDevice, ShadowMapMaterial.Mode.Forest);

            // Render forest shadow items next.
            if (logging) Console.WriteLine("      {0,-5} * ForestMaterial (forest)", RenderShadowItems[shadowMapIndex].Count(ri => ri.Material is ForestMaterial));
            ShadowMapMaterial.Render(graphicsDevice, RenderShadowItems[shadowMapIndex].Where(ri => ri.Material is ForestMaterial), ref ShadowMapLightView[shadowMapIndex], ref ShadowMapLightProj[shadowMapIndex]);

            // Prepare for normal (non-blocking) rendering of terrain.
            ShadowMapMaterial.SetState(graphicsDevice, ShadowMapMaterial.Mode.Normal);

            // Render terrain shadow items now, with their magic.
            if (logging) Console.WriteLine("      {0,-5} * TerrainMaterial (normal)", RenderShadowItems[shadowMapIndex].Count(ri => ri.Material is TerrainMaterial));
            graphicsDevice.VertexDeclaration = TerrainPatch.SharedPatchVertexDeclaration;
            graphicsDevice.Indices = TerrainPatch.SharedPatchIndexBuffer;
            ShadowMapMaterial.Render(graphicsDevice, RenderShadowItems[shadowMapIndex].Where(ri => ri.Material is TerrainMaterial && (ri.Material is TerrainSharedMaterial)), ref ShadowMapLightView[shadowMapIndex], ref ShadowMapLightProj[shadowMapIndex]);
            ShadowMapMaterial.Render(graphicsDevice, RenderShadowItems[shadowMapIndex].Where(ri => ri.Material is TerrainMaterial && !(ri.Material is TerrainSharedMaterial)), ref ShadowMapLightView[shadowMapIndex], ref ShadowMapLightProj[shadowMapIndex]);

            // Prepare for blocking rendering of terrain.
            ShadowMapMaterial.SetState(graphicsDevice, ShadowMapMaterial.Mode.Blocker);

            // Render terrain shadow items in blocking mode.
            if (logging) Console.WriteLine("      {0,-5} * TerrainMaterial (blocker)", RenderShadowItems[shadowMapIndex].Count(ri => ri.Material is TerrainMaterial));
            ShadowMapMaterial.Render(graphicsDevice, RenderShadowItems[shadowMapIndex].Where(ri => ri.Material is TerrainMaterial), ref ShadowMapLightView[shadowMapIndex], ref ShadowMapLightProj[shadowMapIndex]);

            // All done.
            ShadowMapMaterial.ResetState(graphicsDevice);
#if DEBUG_RENDER_STATE
            DebugRenderState(graphicsDevice.RenderState, ShadowMapMaterial.ToString());
#endif
            graphicsDevice.DepthStencilBuffer = NormalStencilBuffer;
            graphicsDevice.SetRenderTarget(0, null);
            ShadowMap[shadowMapIndex] = ShadowMapRenderTarget[shadowMapIndex].GetTexture();

            // Blur the shadow map.
            if (Game.Settings.ShadowMapBlur)
            {
                ShadowMap[shadowMapIndex] = ShadowMapMaterial.ApplyBlur(graphicsDevice, ShadowMap[shadowMapIndex], ShadowMapRenderTarget[shadowMapIndex], ShadowMapStencilBuffer, NormalStencilBuffer);
#if DEBUG_RENDER_STATE
                DebugRenderState(graphicsDevice.RenderState, ShadowMapMaterial.ToString() + " ApplyBlur()");
#endif
            }

            if (logging) Console.WriteLine("    }");
        }

        /// <summary>
        /// Executed in the RenderProcess thread - simple draw
        /// </summary>
        /// <param name="graphicsDevice"></param>
        /// <param name="logging"></param>
        void DrawSimple(GraphicsDevice graphicsDevice, bool logging)
        {
            if (Game.Settings.DistantMountains)
            {
                if (logging) Console.WriteLine("  DrawSimple (Distant Mountains) {");
                graphicsDevice.Clear(ClearOptions.Target | ClearOptions.DepthBuffer | ClearOptions.Stencil, SharedMaterialManager.FogColor, 1, 0);
                DrawSequencesDistantMountains(graphicsDevice, logging);
                if (logging) Console.WriteLine("  }");
                if (logging) Console.WriteLine("  DrawSimple {");
                graphicsDevice.Clear(ClearOptions.DepthBuffer, SharedMaterialManager.FogColor, 1, 0);
                DrawSequences(graphicsDevice, logging);
                if (logging) Console.WriteLine("  }");
            }
            else
            {
                if (logging) Console.WriteLine("  DrawSimple {");
                graphicsDevice.Clear(ClearOptions.Target | ClearOptions.DepthBuffer | ClearOptions.Stencil, SharedMaterialManager.FogColor, 1, 0);
                DrawSequences(graphicsDevice, logging);
                if (logging) Console.WriteLine("  }");
            }
        }

        void DrawSequences(GraphicsDevice graphicsDevice, bool logging)
        {
            if (Game.Settings.DynamicShadows && (RenderProcess.ShadowMapCount > 0) && SceneryShader != null)
                SceneryShader.SetShadowMap(ShadowMapLightViewProjShadowProj, ShadowMap, RenderProcess.ShadowMapLimit);

            var renderItems = new List<RenderItem>();
            for (var i = 0; i < (int)RenderPrimitiveSequence.Sentinel; i++)
            {
                if (logging) Console.WriteLine("    {0} {{", (RenderPrimitiveSequence)i);
                var sequence = RenderItems[i];
                foreach (var sequenceMaterial in sequence)
                {
                    if (sequenceMaterial.Value.Count == 0)
                        continue;
                    if (sequenceMaterial.Key == DummyBlendedMaterial)
                    {
                        // Blended: multiple materials, group by material as much as possible without destroying ordering.
                        Material lastMaterial = null;
                        foreach (var renderItem in sequenceMaterial.Value)
                        {
                            if (lastMaterial != renderItem.Material)
                            {
                                if (renderItems.Count > 0)
                                {
                                    if (logging) Console.WriteLine("      {0,-5} * {1}", renderItems.Count, lastMaterial);
                                    lastMaterial.Render(graphicsDevice, renderItems, ref XNACameraView, ref XNACameraProjection);
                                    renderItems.Clear();
                                }
                                if (lastMaterial != null)
                                    lastMaterial.ResetState(graphicsDevice);
#if DEBUG_RENDER_STATE
								if (lastMaterial != null)
									DebugRenderState(graphicsDevice.RenderState, lastMaterial.ToString());
#endif
                                renderItem.Material.SetState(graphicsDevice, lastMaterial);
                                lastMaterial = renderItem.Material;
                            }
                            renderItems.Add(renderItem);
                        }
                        if (renderItems.Count > 0)
                        {
                            if (logging) Console.WriteLine("      {0,-5} * {1}", renderItems.Count, lastMaterial);
                            lastMaterial.Render(graphicsDevice, renderItems, ref XNACameraView, ref XNACameraProjection);
                            renderItems.Clear();
                        }
                        if (lastMaterial != null)
                            lastMaterial.ResetState(graphicsDevice);
#if DEBUG_RENDER_STATE
						if (lastMaterial != null)
							DebugRenderState(graphicsDevice.RenderState, lastMaterial.ToString());
#endif
                    }
                    else
                    {
                        if (Game.Settings.DistantMountains && (sequenceMaterial.Key is TerrainSharedDistantMountain || sequenceMaterial.Key is SkyMaterial))
                            continue;
                        // Opaque: single material, render in one go.
                        sequenceMaterial.Key.SetState(graphicsDevice, null);
                        if (logging) Console.WriteLine("      {0,-5} * {1}", sequenceMaterial.Value.Count, sequenceMaterial.Key);
                        sequenceMaterial.Key.Render(graphicsDevice, sequenceMaterial.Value, ref XNACameraView, ref XNACameraProjection);
                        sequenceMaterial.Key.ResetState(graphicsDevice);
#if DEBUG_RENDER_STATE
						DebugRenderState(graphicsDevice.RenderState, sequenceMaterial.Key.ToString());
#endif
                    }
                }
                if (logging) Console.WriteLine("    }");
            }
        }

        void DrawSequencesDistantMountains(GraphicsDevice graphicsDevice, bool logging)
        {
            for (var i = 0; i < (int)RenderPrimitiveSequence.Sentinel; i++)
            {
                if (logging) Console.WriteLine("    {0} {{", (RenderPrimitiveSequence)i);
                var sequence = RenderItems[i];
                foreach (var sequenceMaterial in sequence)
                {
                    if (sequenceMaterial.Value.Count == 0)
                        continue;
                    if (sequenceMaterial.Key is TerrainSharedDistantMountain || sequenceMaterial.Key is SkyMaterial)
                    {
                        // Opaque: single material, render in one go.
                        sequenceMaterial.Key.SetState(graphicsDevice, null);
                        if (logging) Console.WriteLine("      {0,-5} * {1}", sequenceMaterial.Value.Count, sequenceMaterial.Key);
                        sequenceMaterial.Key.Render(graphicsDevice, sequenceMaterial.Value, ref XNACameraView, ref Camera.XnaDistantMountainProjection);
                        sequenceMaterial.Key.ResetState(graphicsDevice);
#if DEBUG_RENDER_STATE
						DebugRenderState(graphicsDevice.RenderState, sequenceMaterial.Key.ToString());
#endif
                    }
                }
                if (logging) Console.WriteLine("    }");
            }
        }

#if DEBUG_RENDER_STATE
        static void DebugRenderState(RenderState renderState, string location)
        {
            if (renderState.AlphaBlendEnable != false) throw new InvalidOperationException(String.Format("RenderState.AlphaBlendEnable is {0}; expected {1} in {2}.", renderState.AlphaBlendEnable, false, location));
            if (renderState.AlphaBlendOperation != BlendFunction.Add) throw new InvalidOperationException(String.Format("RenderState.AlphaBlendOperation is {0}; expected {1} in {2}.", renderState.AlphaBlendOperation, BlendFunction.Add, location));
            // DOCUMENTATION IS WRONG, it says Blend.One:
            if (renderState.AlphaDestinationBlend != Blend.Zero) throw new InvalidOperationException(String.Format("RenderState.AlphaDestinationBlend is {0}; expected {1} in {2}.", renderState.AlphaDestinationBlend, Blend.Zero, location));
            if (renderState.AlphaFunction != CompareFunction.Always) throw new InvalidOperationException(String.Format("RenderState.AlphaFunction is {0}; expected {1} in {2}.", renderState.AlphaFunction, CompareFunction.Always, location));
            if (renderState.AlphaSourceBlend != Blend.One) throw new InvalidOperationException(String.Format("RenderState.AlphaSourceBlend is {0}; expected {1} in {2}.", renderState.AlphaSourceBlend, Blend.One, location));
            if (renderState.AlphaTestEnable != false) throw new InvalidOperationException(String.Format("RenderState.AlphaTestEnable is {0}; expected {1} in {2}.", renderState.AlphaTestEnable, false, location));
            if (renderState.BlendFactor != Color.White) throw new InvalidOperationException(String.Format("RenderState.BlendFactor is {0}; expected {1} in {2}.", renderState.BlendFactor, Color.White, location));
            if (renderState.BlendFunction != BlendFunction.Add) throw new InvalidOperationException(String.Format("RenderState.BlendFunction is {0}; expected {1} in {2}.", renderState.BlendFunction, BlendFunction.Add, location));
            // DOCUMENTATION IS WRONG, it says ColorWriteChannels.None:
            if (renderState.ColorWriteChannels != ColorWriteChannels.All) throw new InvalidOperationException(String.Format("RenderState.ColorWriteChannels is {0}; expected {1} in {2}.", renderState.ColorWriteChannels, ColorWriteChannels.All, location));
            // DOCUMENTATION IS WRONG, it says ColorWriteChannels.None:
            if (renderState.ColorWriteChannels1 != ColorWriteChannels.All) throw new InvalidOperationException(String.Format("RenderState.ColorWriteChannels1 is {0}; expected {1} in {2}.", renderState.ColorWriteChannels1, ColorWriteChannels.All, location));
            // DOCUMENTATION IS WRONG, it says ColorWriteChannels.None:
            if (renderState.ColorWriteChannels2 != ColorWriteChannels.All) throw new InvalidOperationException(String.Format("RenderState.ColorWriteChannels2 is {0}; expected {1} in {2}.", renderState.ColorWriteChannels2, ColorWriteChannels.All, location));
            // DOCUMENTATION IS WRONG, it says ColorWriteChannels.None:
            if (renderState.ColorWriteChannels3 != ColorWriteChannels.All) throw new InvalidOperationException(String.Format("RenderState.ColorWriteChannels3 is {0}; expected {1} in {2}.", renderState.ColorWriteChannels3, ColorWriteChannels.All, location));
            if (renderState.CounterClockwiseStencilDepthBufferFail != StencilOperation.Keep) throw new InvalidOperationException(String.Format("RenderState.CounterClockwiseStencilDepthBufferFail is {0}; expected {1} in {2}.", renderState.CounterClockwiseStencilDepthBufferFail, StencilOperation.Keep, location));
            if (renderState.CounterClockwiseStencilFail != StencilOperation.Keep) throw new InvalidOperationException(String.Format("RenderState.CounterClockwiseStencilFail is {0}; expected {1} in {2}.", renderState.CounterClockwiseStencilFail, StencilOperation.Keep, location));
            if (renderState.CounterClockwiseStencilFunction != CompareFunction.Always) throw new InvalidOperationException(String.Format("RenderState.CounterClockwiseStencilFunction is {0}; expected {1} in {2}.", renderState.CounterClockwiseStencilFunction, CompareFunction.Always, location));
            if (renderState.CounterClockwiseStencilPass != StencilOperation.Keep) throw new InvalidOperationException(String.Format("RenderState.CounterClockwiseStencilPass is {0}; expected {1} in {2}.", renderState.CounterClockwiseStencilPass, StencilOperation.Keep, location));
            if (renderState.CullMode != CullMode.CullCounterClockwiseFace) throw new InvalidOperationException(String.Format("RenderState.CullMode is {0}; expected {1} in {2}.", renderState.CullMode, CullMode.CullCounterClockwiseFace, location));
            if (renderState.DepthBias != 0.0f) throw new InvalidOperationException(String.Format("RenderState.DepthBias is {0}; expected {1} in {2}.", renderState.DepthBias, 0.0f, location));
            if (renderState.DepthBufferEnable != true) throw new InvalidOperationException(String.Format("RenderState.DepthBufferEnable is {0}; expected {1} in {2}.", renderState.DepthBufferEnable, true, location));
            if (renderState.DepthBufferFunction != CompareFunction.LessEqual) throw new InvalidOperationException(String.Format("RenderState.DepthBufferFunction is {0}; expected {1} in {2}.", renderState.DepthBufferFunction, CompareFunction.LessEqual, location));
            if (renderState.DepthBufferWriteEnable != true) throw new InvalidOperationException(String.Format("RenderState.DepthBufferWriteEnable is {0}; expected {1} in {2}.", renderState.DepthBufferWriteEnable, true, location));
            if (renderState.DestinationBlend != Blend.Zero) throw new InvalidOperationException(String.Format("RenderState.DestinationBlend is {0}; expected {1} in {2}.", renderState.DestinationBlend, Blend.Zero, location));
            if (renderState.FillMode != FillMode.Solid) throw new InvalidOperationException(String.Format("RenderState.FillMode is {0}; expected {1} in {2}.", renderState.FillMode, FillMode.Solid, location));
            if (renderState.FogColor != Color.TransparentBlack) throw new InvalidOperationException(String.Format("RenderState.FogColor is {0}; expected {1} in {2}.", renderState.FogColor, Color.TransparentBlack, location));
            if (renderState.FogDensity != 1.0f) throw new InvalidOperationException(String.Format("RenderState.FogDensity is {0}; expected {1} in {2}.", renderState.FogDensity, 1.0f, location));
            if (renderState.FogEnable != false) throw new InvalidOperationException(String.Format("RenderState.FogEnable is {0}; expected {1} in {2}.", renderState.FogEnable, false, location));
            if (renderState.FogEnd != 1.0f) throw new InvalidOperationException(String.Format("RenderState.FogEnd is {0}; expected {1} in {2}.", renderState.FogEnd, 1.0f, location));
            if (renderState.FogStart != 0.0f) throw new InvalidOperationException(String.Format("RenderState.FogStart is {0}; expected {1} in {2}.", renderState.FogStart, 0.0f, location));
            if (renderState.FogTableMode != FogMode.None) throw new InvalidOperationException(String.Format("RenderState.FogTableMode is {0}; expected {1} in {2}.", renderState.FogTableMode, FogMode.None, location));
            if (renderState.FogVertexMode != FogMode.None) throw new InvalidOperationException(String.Format("RenderState.FogVertexMode is {0}; expected {1} in {2}.", renderState.FogVertexMode, FogMode.None, location));
            if (renderState.MultiSampleAntiAlias != true) throw new InvalidOperationException(String.Format("RenderState.MultiSampleAntiAlias is {0}; expected {1} in {2}.", renderState.MultiSampleAntiAlias, true, location));
            if (renderState.MultiSampleMask != -1) throw new InvalidOperationException(String.Format("RenderState.MultiSampleMask is {0}; expected {1} in {2}.", renderState.MultiSampleMask, -1, location));
            //if (renderState.PointSize != 64) throw new InvalidOperationException(String.Format("RenderState.e.PointSize is {0}; expected {1} in {2}.", renderState.e.PointSize, 64, location));
            //if (renderState.PointSizeMax != 64.0f) throw new InvalidOperationException(String.Format("RenderState.PointSizeMax is {0}; expected {1} in {2}.", renderState.PointSizeMax, 64.0f, location));
            //if (renderState.PointSizeMin != 1.0f) throw new InvalidOperationException(String.Format("RenderState.PointSizeMin is {0}; expected {1} in {2}.", renderState.PointSizeMin, 1.0f, location));
            if (renderState.PointSpriteEnable != false) throw new InvalidOperationException(String.Format("RenderState.PointSpriteEnable is {0}; expected {1} in {2}.", renderState.PointSpriteEnable, false, location));
            if (renderState.RangeFogEnable != false) throw new InvalidOperationException(String.Format("RenderState.RangeFogEnable is {0}; expected {1} in {2}.", renderState.RangeFogEnable, false, location));
            if (renderState.ReferenceAlpha != 0) throw new InvalidOperationException(String.Format("RenderState.ReferenceAlpha is {0}; expected {1} in {2}.", renderState.ReferenceAlpha, 0, location));
            if (renderState.ReferenceStencil != 0) throw new InvalidOperationException(String.Format("RenderState.ReferenceStencil is {0}; expected {1} in {2}.", renderState.ReferenceStencil, 0, location));
            if (renderState.ScissorTestEnable != false) throw new InvalidOperationException(String.Format("RenderState.ScissorTestEnable is {0}; expected {1} in {2}.", renderState.ScissorTestEnable, false, location));
            if (renderState.SeparateAlphaBlendEnabled != false) throw new InvalidOperationException(String.Format("RenderState.SeparateAlphaBlendEnabled is {0}; expected {1} in {2}.", renderState.SeparateAlphaBlendEnabled, false, location));
            if (renderState.SlopeScaleDepthBias != 0) throw new InvalidOperationException(String.Format("RenderState.SlopeScaleDepthBias is {0}; expected {1} in {2}.", renderState.SlopeScaleDepthBias, 0, location));
            if (renderState.SourceBlend != Blend.One) throw new InvalidOperationException(String.Format("RenderState.SourceBlend is {0}; expected {1} in {2}.", renderState.SourceBlend, Blend.One, location));
            if (renderState.StencilDepthBufferFail != StencilOperation.Keep) throw new InvalidOperationException(String.Format("RenderState.StencilDepthBufferFail is {0}; expected {1} in {2}.", renderState.StencilDepthBufferFail, StencilOperation.Keep, location));
            if (renderState.StencilEnable != false) throw new InvalidOperationException(String.Format("RenderState.StencilEnable is {0}; expected {1} in {2}.", renderState.StencilEnable, false, location));
            if (renderState.StencilFail != StencilOperation.Keep) throw new InvalidOperationException(String.Format("RenderState.StencilFail is {0}; expected {1} in {2}.", renderState.StencilFail, StencilOperation.Keep, location));
            if (renderState.StencilFunction != CompareFunction.Always) throw new InvalidOperationException(String.Format("RenderState.StencilFunction is {0}; expected {1} in {2}.", renderState.StencilFunction, CompareFunction.Always, location));
            // DOCUMENTATION IS WRONG, it says Int32.MaxValue:
            if (renderState.StencilMask != -1) throw new InvalidOperationException(String.Format("RenderState.StencilMask is {0}; expected {1} in {2}.", renderState.StencilMask, -1, location));
            if (renderState.StencilPass != StencilOperation.Keep) throw new InvalidOperationException(String.Format("RenderState.StencilPass is {0}; expected {1} in {2}.", renderState.StencilPass, StencilOperation.Keep, location));
            // DOCUMENTATION IS WRONG, it says Int32.MaxValue:
            if (renderState.StencilWriteMask != -1) throw new InvalidOperationException(String.Format("RenderState.StencilWriteMask is {0}; expected {1} in {2}.", renderState.StencilWriteMask, -1, location));
            if (renderState.TwoSidedStencilMode != false) throw new InvalidOperationException(String.Format("RenderState.TwoSidedStencilMode is {0}; expected {1} in {2}.", renderState.TwoSidedStencilMode, false, location));
        }
#endif
    }
}
