using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;
using Object = UnityEngine.Object;

namespace Aero.BinocularADS
{
    // Inject this callback where the three upscale branches rejoin.
    internal static class PostUpscaleComposite
    {
        // Both callbacks run inside EFT's PostProcessLayer.BuildCommandBuffers. Catch errors here so the mod can disable itself and the game can finish building its own buffers.
        public static void Append(SSAAPropagator instance, CommandBuffer externalCommandBuffer)
        {
            try
            {
                ProductionPipeline.Append(instance, externalCommandBuffer);
            }
            catch (System.Exception e)
            {
                ProductionPipeline.InjectedCallbackFailed("post-upscale composite", e);
            }
        }

        // Run immediately after EFT's optic lens draw to apply Magnified Sight Picture Opacity.
        public static void AfterOpticLens(SSAAPropagator instance, CommandBuffer externalCommandBuffer)
        {
            try
            {
                ProductionPipeline.AfterOpticLens(instance, externalCommandBuffer);
            }
            catch (System.Exception e)
            {
                ProductionPipeline.InjectedCallbackFailed("magnified sight opacity", e);
            }
        }
    }

    [HarmonyPatch(typeof(PostProcessLayer), "BuildCommandBuffers")]
    internal static class PostProcessLayerBuildCommandBuffersPatch
    {
        [HarmonyPrepare]
        private static bool Prepare()
        {
            MethodInfo method = AccessTools.DeclaredMethod(
                typeof(PostProcessLayer), "BuildCommandBuffers", Type.EmptyTypes);
            bool valid = method != null && method.ReturnType == typeof(void);
            if (!valid)
                ProductionPipeline.MarkPatchIncompatible(
                    "exact parameterless PostProcessLayer.BuildCommandBuffers was not found");
            else
                ProductionPipeline.MarkBuildHookPatched();
            return valid;
        }

        [HarmonyPrefix]
        private static void Prefix(PostProcessLayer __instance, out int __state)
        {
            __state = ProductionPipeline.BuildPrefix(__instance);
        }

        [HarmonyPostfix]
        private static void Postfix(int __state)
        {
            ProductionPipeline.BuildPostfix(__state);
        }

        [HarmonyFinalizer]
        private static Exception Finalizer(Exception __exception, int __state)
        {
            return ProductionPipeline.BuildFinalizer(__exception, __state);
        }
    }

    [HarmonyPatch]
    internal static class SSAAPropagatorFillCommandBufferPatch
    {
        private static MethodBase TargetMethod()
        {
            MethodInfo method = AccessTools.DeclaredMethod(
                typeof(SSAAPropagator),
                "FillCommandBuffer",
                new[]
                {
                    typeof(CommandBuffer),
                    typeof(RenderTargetIdentifier),
                    typeof(RenderTargetIdentifier),
                });
            if (method == null || method.ReturnType != typeof(void))
                ProductionPipeline.MarkPatchIncompatible(
                    "exact SSAAPropagator.FillCommandBuffer signature was not found");
            return method;
        }

        private static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> source)
        {
            var codes = source.ToList();
            FieldInfo thermal = AccessTools.Field(typeof(SSAAPropagator), "_thermalVisionIsOn");
            FieldInfo lens = AccessTools.Field(typeof(SSAAPropagator), "_opticLensRenderer");
            FieldInfo collimator = AccessTools.Field(typeof(SSAAPropagator), "_collimatorRenderer");
            MethodInfo inequality = AccessTools.Method(
                typeof(Object), "op_Inequality", new[] { typeof(Object), typeof(Object) });
            MethodInfo append = AccessTools.Method(
                typeof(PostUpscaleComposite), nameof(PostUpscaleComposite.Append));

            var matches = new List<int>();
            for (int i = 0; i + 9 < codes.Count; i++)
            {
                if (codes[i].opcode != OpCodes.Ldarg_0 ||
                    !LoadsField(codes[i + 1], thermal) ||
                    !IsConditionalBranch(codes[i + 2].opcode) ||
                    codes[i + 3].opcode != OpCodes.Ldarg_0 ||
                    !LoadsField(codes[i + 4], lens) ||
                    codes[i + 5].opcode != OpCodes.Ldnull ||
                    codes[i + 6].operand as MethodInfo != inequality ||
                    !IsConditionalBranch(codes[i + 7].opcode) ||
                    codes[i + 8].opcode != OpCodes.Ldarg_0 ||
                    !LoadsField(codes[i + 9], collimator))
                    continue;
                matches.Add(i);
            }

            if (matches.Count != 1 || append == null)
            {
                ProductionPipeline.MarkPatchIncompatible(
                    $"post-upscale/pre-optic IL matcher found {matches.Count} anchors");
                return codes;
            }

            int anchor = matches[0];
            var loadInstance = new CodeInstruction(OpCodes.Ldarg_0);
            loadInstance.labels.AddRange(codes[anchor].labels);
            loadInstance.blocks.AddRange(codes[anchor].blocks);
            codes[anchor].labels.Clear();
            codes[anchor].blocks.Clear();

            codes.InsertRange(
                anchor,
                new[]
                {
                    loadInstance,
                    new CodeInstruction(OpCodes.Ldarg_1),
                    new CodeInstruction(OpCodes.Call, append),
                });
            ProductionPipeline.MarkFillHookPatched();

            // Match exactly one lens draw, ldfld _opticLensMaterial followed by callvirt CommandBuffer.DrawRenderer(Renderer, Material), and insert the second call after it. If the match fails, skip lens opacity and keep the weapon composite.
            FieldInfo lensMaterial = AccessTools.Field(typeof(SSAAPropagator), "_opticLensMaterial");
            MethodInfo drawRenderer = AccessTools.Method(typeof(CommandBuffer), nameof(CommandBuffer.DrawRenderer),
                new[] { typeof(Renderer), typeof(Material) });
            MethodInfo afterLens = AccessTools.Method(typeof(PostUpscaleComposite), nameof(PostUpscaleComposite.AfterOpticLens));
            var lensDraws = new List<int>();
            for (int i = 1; i < codes.Count; i++)
            {
                if (codes[i].operand as MethodInfo == drawRenderer && LoadsField(codes[i - 1], lensMaterial))
                    lensDraws.Add(i);
            }
            if (lensDraws.Count == 1 && afterLens != null && lensMaterial != null && drawRenderer != null)
            {
                codes.InsertRange(lensDraws[0] + 1, new[]
                {
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Ldarg_1),
                    new CodeInstruction(OpCodes.Call, afterLens),
                });
                ProductionPipeline._lensHookInstalled = true;
                Plugin.Diagnostic("[BinoADS] magnified sight opacity hook installed after the optic lens draw");
            }
            else
            {
                Plugin.Log.LogWarning(
                    $"[BinoADS] magnified sight opacity unavailable: optic lens draw matched {lensDraws.Count} times (expected 1)");
            }
            return codes;
        }

        private static bool LoadsField(CodeInstruction code, FieldInfo field)
        {
            return code.opcode == OpCodes.Ldfld && Equals(code.operand, field);
        }

        private static bool IsConditionalBranch(OpCode opcode)
        {
            return opcode.FlowControl == FlowControl.Cond_Branch;
        }
    }
}
