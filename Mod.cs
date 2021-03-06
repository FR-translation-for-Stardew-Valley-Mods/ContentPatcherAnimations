﻿using System;
using System.Collections;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using StardewModdingAPI;
using ContentPatcher;
using System.Reflection;
using System.Collections.Generic;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.BellsAndWhistles;
using StardewValley.Projectiles;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;
using StardewModdingAPI.Events;
using Microsoft.Xna.Framework;
using SpaceShared;

namespace ContentPatcherAnimations
{
    public class PatchData
    {
        public Func<bool> IsActive;
        public Func<Texture2D> TargetFunc;
        public Texture2D Target;
        public Func<Texture2D> SourceFunc;
        public Texture2D Source;
        public Func<Rectangle> FromAreaFunc;
        public Func<Rectangle> ToAreaFunc;
        public int CurrentFrame = 0;
    }

    public class Mod : StardewModdingAPI.Mod
    {
        public static Mod instance;

        public const BindingFlags PublicI = BindingFlags.Public | BindingFlags.Instance;
        public const BindingFlags PublicS = BindingFlags.Public | BindingFlags.Static;
        public const BindingFlags PrivateI = BindingFlags.NonPublic | BindingFlags.Instance;
        public const BindingFlags PrivateS = BindingFlags.NonPublic | BindingFlags.Static;

        private StardewModdingAPI.Mod contentPatcher;
        private IEnumerable cpPatches;

        private Dictionary<Patch, PatchData> animatedPatches = new Dictionary<Patch, PatchData>();

        public static uint frameCounter = 0;

        public override void Entry(IModHelper helper)
        {
            instance = this;
            Log.Monitor = Monitor;

            Helper.Events.GameLoop.UpdateTicked += UpdateAnimations;
            Helper.Events.GameLoop.SaveCreated += UpdateTargetTextures;
            Helper.Events.GameLoop.SaveLoaded += UpdateTargetTextures;
            Helper.Events.GameLoop.DayStarted += UpdateTargetTextures;
        }

        private void UpdateAnimations(object sender, UpdateTickedEventArgs e)
        {
            if ( contentPatcher == null )
            {
                var modData = Helper.ModRegistry.Get("Pathoschild.ContentPatcher");
                contentPatcher = (StardewModdingAPI.Mod)modData.GetType().GetProperty("Mod", PrivateI | PublicI).GetValue(modData);
                var patchManager = contentPatcher.GetType().GetField("PatchManager", PrivateI).GetValue(contentPatcher);
                cpPatches = (IEnumerable)patchManager.GetType().GetField("Patches", PrivateI).GetValue(patchManager);

                CollectPatches();
            }

            ++frameCounter;
            Game1.graphics.GraphicsDevice.Textures[0] = null;
            foreach ( var patch in animatedPatches )
            {
                if (!patch.Value.IsActive.Invoke() || patch.Value.Source == null || patch.Value.Target == null)
                    continue;
                
                if ( frameCounter % patch.Key.AnimationFrameTime == 0 )
                {
                    if (++patch.Value.CurrentFrame >= patch.Key.AnimationFrameCount)
                        patch.Value.CurrentFrame = 0;
                    
                    var sourceRect = patch.Value.FromAreaFunc.Invoke();
                    sourceRect.X += patch.Value.CurrentFrame * sourceRect.Width;
                    var targetRect = patch.Value.ToAreaFunc.Invoke();
                    if ( targetRect == Rectangle.Empty )
                        targetRect = new Rectangle(0, 0, sourceRect.Width, sourceRect.Height);
                    var cols = new Color[sourceRect.Width * sourceRect.Height];
                    patch.Value.Source.GetData(0, sourceRect, cols, 0, cols.Length);
                    patch.Value.Target.SetData(0, targetRect, cols, 0, cols.Length);
                }
            }
        }

        private void UpdateTargetTextures(object sender, EventArgs args)
        {
            foreach ( var patch in animatedPatches )
            {
                try
                {
                    patch.Value.Source = patch.Value.SourceFunc();
                    patch.Value.Target = patch.Value.TargetFunc();
                }
                catch ( Exception e )
                {
                    Log.error("Exception loading " + patch.Key.LogName + " textures: " + e);
                }
            }
        }

        private void CollectPatches()
        {
            foreach (var pack in contentPatcher.Helper.ContentPacks.GetOwned())
            {
                var patches = pack.ReadJsonFile<PatchList>("content.json");
                foreach (var patch in patches.Changes)
                {
                    if (patch.AnimationFrameTime > 0 && patch.AnimationFrameCount > 0)
                    {
                        Log.trace("Loading animated patch from content pack " + pack.Manifest.UniqueID);
                        if (patch.LogName == null || patch.LogName == "")
                        {
                            Log.error("Animated patches must specify a LogName!");
                            continue;
                        }

                        PatchData data = new PatchData();

                        object targetPatch = null;
                        foreach (var cpPatch in cpPatches)
                        {
                            var name = (string)cpPatch.GetType().GetProperty("LogName", PublicI).GetValue(cpPatch);
                            if (name == pack.Manifest.Name + " > " + patch.LogName)
                            {
                                targetPatch = cpPatch;
                                break;
                            }
                        }
                        if (targetPatch == null)
                        {
                            Log.error("Failed to find patch with name \"" + patch.LogName + "\"!?!?");
                            continue;
                        }
                        var appliedProp = targetPatch.GetType().GetProperty("IsApplied", PublicI);
                        var sourceProp = targetPatch.GetType().GetProperty("FromAsset", PublicI);
                        var targetProp = targetPatch.GetType().GetProperty("TargetAsset", PublicI);
                        
                        data.IsActive = () => (bool)appliedProp.GetValue(targetPatch);
                        data.SourceFunc = () => pack.LoadAsset<Texture2D>((string)sourceProp.GetValue(targetPatch));
                        data.TargetFunc = () => FindTargetTexture((string)targetProp.GetValue(targetPatch));
                        data.FromAreaFunc = () => GetRectangleFromPatch(targetPatch, "FromArea");
                        data.ToAreaFunc = () => GetRectangleFromPatch(targetPatch, "ToArea");

                        animatedPatches.Add(patch, data);
                    }
                }
            }
        }

        private Texture2D FindTargetTexture(string target)
        {
            var tex = Game1.content.Load<Texture2D>(target);
            if ( tex.GetType().Name == "ScaledTexture2D" )
            {
                Log.trace("Found ScaledTexture2D from PyTK: " + target);
                tex = Helper.Reflection.GetProperty<Texture2D>(tex, "STexture").GetValue();
            }
            return tex;
        }

        private Rectangle GetRectangleFromPatch(object targetPatch, string rectName)
        {
            var rect = targetPatch.GetType().GetField(rectName, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(targetPatch);
            var tryGetRectValue = rect.GetType().GetMethod("TryGetRectangle");

            object[] args = new object[] { null, null };
            if ( !((bool) tryGetRectValue.Invoke(rect, args)) )
            {
                return Rectangle.Empty;
            }

            return (Rectangle)args[0];
        }
    }
}
