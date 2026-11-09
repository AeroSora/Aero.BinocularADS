using System;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Aero.BinocularADS
{
    internal static partial class ProductionPipeline
    {
        private static bool BuildRegistry()
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try { return BuildRegistryCore(); }
            finally
            {
                AimBuildMs += watch.Elapsed.TotalMilliseconds;
                AimBuilds++;
                Plugin.Diagnostic($"[BinoADS] registry build {watch.Elapsed.TotalMilliseconds:0.0} ms CPU at frame {Time.frameCount}");
                // Log the eye's parts by coverage ID. When something shows through a rail or cover, this tells us whether the occluder was drawn at all.
                var names = new System.Text.StringBuilder();
                for (int i = 0; i < PartNames.Count; i++)
                    names.Append(i == 0 ? "" : ", ").Append(i + 1).Append(" '").Append(PartNames[i]).Append('\'');
                Plugin.Diagnostic($"[BinoADS] registry parts ({PartNames.Count}): {names}");
            }
        }

        internal static int TargetReallocs => _targetReallocs;

        private static bool BuildRegistryCore()
        {
            if (_weaponRoot == null)
            {
                FailSession("weapon root disappeared during registry build");
                return false;
            }

            SightPicture.Clear();
            if (_ssaaLens != null)
                SightPicture.Add(_ssaaLens);
            if (_ssaaCollimator != null)
                SightPicture.Add(_ssaaCollimator);

            Scan.Clear();
            ArmRenderers.Clear();
            _weaponRoot.GetComponentsInChildren(false, Scan);
            if (Plugin.TransparentArms.Value)
            {
                var body = BinocularController.LocalPlayer?.PlayerBody;
                if (body != null)
                {
                    foreach (SkinnedMeshRenderer arm in body.GetComponentsInChildren<SkinnedMeshRenderer>(false))
                    {
                        if (arm != null && arm.name.IndexOf("hand", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            Scan.Add(arm);
                            ArmRenderers.Add(arm);
                        }
                    }
                    // Custom outfits name their arm meshes freely ("000_BodySkin", "010_BodySkin", non-Latin names), so also take
                    // every renderer of the body's Hands part, which is what the game itself draws as the first-person arms.
                    int byPart = AddHandsPartRenderers(body);
                    if (byPart > 0)
                        Plugin.Diagnostic($"[BinoADS] arms: {byPart} renderer(s) of the Hands body part added to the scan");
                }
            }

            int ordinary = 0;
            int mixed = 0;
            int transparent = 0;
            int cutout = 0;
            int ammo = 0;
            int reducedLod = 0;
            int beams = 0;
            int meshless = 0;
            var transparentNames = new System.Text.StringBuilder();
            var meshlessNames = new System.Text.StringBuilder();

            for (int i = 0; i < Scan.Count; i++)
            {
                Renderer renderer = Scan[i];
                WeaponRendererPolicy.Decision decision = WeaponRendererPolicy.Evaluate(
                    renderer,
                    renderer != null && SightPicture.Contains(renderer),
                    renderer != null && IsOwnedClone(renderer),
                    requireEligible: false);

                if (decision == WeaponRendererPolicy.Decision.Missing ||
                    decision == WeaponRendererPolicy.Decision.OwnedClone ||
                    decision == WeaponRendererPolicy.Decision.UnsupportedType ||
                    decision == WeaponRendererPolicy.Decision.SightPicture)
                    continue;

                // Skip reduced LODs because the private clones have no LODGroup and would all draw. A low-poly box can extend beyond the real mesh and write coverage there, leaving a flat dark rectangle in the composite.
                if (decision == WeaponRendererPolicy.Decision.ReducedLod)
                {
                    reducedLod++;
                    continue;
                }

                // Leave beams in the world pass so they appear once and stay clear of the weapon blur.
                if (decision == WeaponRendererPolicy.Decision.Beam)
                {
                    beams++;
                    continue;
                }

                // Skip renderers with no mesh, whether the MeshFilter is missing, empty, or the skinned mesh has no shared mesh. They draw nothing in EFT. Rejecting them would disable binocular ADS on weapons like the PP-19, which has an empty weapon_izhmash_saiga_9_9x19_barrel_LOD0. For real meshes, keep the material count and shader checks below.
                Mesh mesh = GetMesh(renderer);
                if (mesh == null)
                {
                    meshless++;
                    if (meshlessNames.Length > 0)
                        meshlessNames.Append(", ");
                    meshlessNames.Append(renderer.name);
                    continue;
                }

                Material[] materials = renderer.sharedMaterials;
                if (materials == null || materials.Length < mesh.subMeshCount)
                {
                    FailUnsupported(
                        renderer, -1, null,
                        $"material cardinality {materials?.Length ?? 0} < submeshes {mesh.subMeshCount}");
                    return false;
                }

                bool isAmmo = WeaponRendererPolicy.IsAmmunition(renderer);
                var slots = new SlotClass[mesh.subMeshCount];
                bool hasOpaque = false;
                bool hasTransparent = false;
                bool skipArm = false;

                for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
                {
                    Material material = materials[submesh];
                    if (material == null)
                    {
                        FailUnsupported(renderer, submesh, null, "used source slot is null");
                        return false;
                    }

                    string failure;
                    if (!Classify(material, out slots[submesh], out failure))
                    {
                        // Some custom outfit gloves are tagged Transparent but use alpha testing, so the classifier can't place them. Leave those arms native and solid. A development build can treat known materials as opaque instead.
                        //
                        // Weapon parts keep the strict rule, since a wrong guess there breaks the sight.
                        bool takeOver = false;
                        DeveloperTakeOverConflictingArms(ref takeOver);
                        bool armOpaque = false;
                        if (ArmRenderers.Contains(renderer))
                            DeveloperConflictingArmIsOpaque(material, ref armOpaque);
                        if (takeOver && ArmRenderers.Contains(renderer) && material.GetTag("RenderType", false, "") == "Transparent")
                        {
                            slots[submesh] = SlotClass.Transparent;
                            Plugin.Diagnostic($"[BinoADS] arms (developer takeover): '{renderer.name}' submesh {submesh} material '{material.name}' queue {material.renderQueue} treated as transparent");
                        }
                        else if (armOpaque)
                        {
                            slots[submesh] = SlotClass.Opaque;
                            Plugin.Diagnostic($"[BinoADS] arms (development build): '{renderer.name}' submesh {submesh} material '{material.name}' shader '{material.shader?.name}' " +
                                              $"treated as opaque (queue now {material.renderQueue}: {failure})");
                        }
                        else if (ArmRenderers.Contains(renderer))
                        {
                            skipArm = true;
                            Plugin.Diagnostic($"[BinoADS] arms: '{renderer.name}' left native and solid: submesh {submesh} material '{material.name}' " +
                                              $"shader '{material.shader?.name}' queue {material.renderQueue}: {failure}");
                            break;
                        }
                        else
                        {
                            FailUnsupported(renderer, submesh, material, failure);
                            return false;
                        }
                    }

                    if (slots[submesh] == SlotClass.Transparent)
                        hasTransparent = true;
                    else
                    {
                        hasOpaque = true;
                        if (slots[submesh] == SlotClass.Cutout)
                            cutout++;
                    }
                }

                if (skipArm)
                {
                    ArmRenderers.Remove(renderer);
                    continue;
                }

                if (isAmmo)
                {
                    var entry = NewEntry(renderer, materials, slots, EntryKind.Ammunition);
                    Entries.Add(entry);
                    ammo++;
                    continue;
                }

                if (!hasOpaque)
                {
                    // Keep wholly transparent renderers in EFT's main pass. They never enter the eye images, so parts like a slide back, magnifier joint or laser housing can stay sharp.
                    //
                    // Giving all of them coverage would make the sight picture opaque and blurred because the composite reads coverage as solid weapon. Check the logged materials before changing admission. IsOpticMaterial can identify glass that must stay native to keep the world seen through it sharp.
                    transparent++;
                    if (transparentNames.Length > 0)
                        transparentNames.Append(", ");
                    Material first = materials.Length > 0 ? materials[0] : null;
                    // Collect CW FX/OpticLens glass for ApplyMainSuppression. At reduced sight-picture opacity, the glass would otherwise show up in the pre-lens copy where the opaque native picture normally covers it.
                    if (WeaponRendererPolicy.IsOpticSurface(renderer) && first != null && first.shader != null &&
                        first.shader.name.IndexOf("OpticLens", StringComparison.Ordinal) >= 0)
                        OpticGlass.Add(renderer);
                    transparentNames.Append(renderer.name).Append('/')
                        .Append(first != null && first.shader != null ? first.shader.name : "<none>")
                        .Append('/').Append(first != null ? first.renderQueue : -1)
                        .Append(WeaponRendererPolicy.IsOpticSurface(renderer) ? "[optical]" : "[body]");
                    continue;
                }

                EntryKind kind = hasTransparent ? EntryKind.Mixed : EntryKind.Ordinary;
                Entry item = NewEntry(renderer, materials, slots, kind);
                item.SecondaryExcluded =
                    renderer is SkinnedMeshRenderer &&
                    !Plugin.DoubleArms.Value &&
                    (ArmRenderers.Contains(renderer) || renderer.name.IndexOf("hand", StringComparison.OrdinalIgnoreCase) >= 0);

                if (kind == EntryKind.Mixed && !CreateMixedClones(item, mesh))
                {
                    FailUnsupported(renderer, -1, null, "could not reproduce mixed renderer clones");
                    DestroyEntry(item);
                    return false;
                }

                Entries.Add(item);
                if (!CreateCoverageDraws(item))
                    return false;

                if (kind == EntryKind.Mixed)
                    mixed++;
                else
                    ordinary++;
            }

            Scan.Clear();
            _registryReady = true;

            Plugin.Diagnostic(
                $"[BinoADS] renderer ownership | policy=shared-v1 ordinary={ordinary} mixed={mixed} transparentNative={transparent} " +
                $"cutoutSlots={cutout} ammo={ammo} reducedLod={reducedLod} beams={beams} " +
                $"meshlessSkipped={meshless} meshless=[{meshlessNames}] " +
                $"sightPicture=[{Describe(_ssaaLens)}, {Describe(_ssaaCollimator)}] " +
                $"transparentNative=[{transparentNames}] " +
                $"sourceArraysImmutable=true cloneNullSlots=0");
            return true;
        }

        private static Entry NewEntry(
            Renderer renderer, Material[] materials, SlotClass[] slots, EntryKind kind)
        {
            return new Entry
            {
                Source = renderer,
                SourceMaterials = materials.ToArray(),
                Slots = slots,
                Kind = kind,
                SessionOriginalForceOff = renderer.forceRenderingOff,
                SessionOriginalShadow = renderer.shadowCastingMode,
            };
        }

        private static Mesh GetMesh(Renderer renderer)
        {
            if (renderer is SkinnedMeshRenderer skinned)
                return skinned.sharedMesh;
            MeshFilter filter = renderer.GetComponent<MeshFilter>();
            return filter == null ? null : filter.sharedMesh;
        }

        private static bool IsOwnedClone(Renderer renderer)
        {
            if (renderer == null ||
                (renderer.gameObject.hideFlags & HideFlags.HideAndDontSave) == 0)
                return false;
            string name = renderer.name;
            return name != null &&
                   (name.EndsWith("[Aero Opaque Eye]", StringComparison.Ordinal) ||
                    name.EndsWith("[Aero Main Glass]", StringComparison.Ordinal));
        }

        private const string DitheredTransparencyShader = "Transparent/DepthZwriteDithered";

        private static bool Classify(Material material, out SlotClass result, out string failure)
        {
            result = SlotClass.Opaque;
            failure = null;

            string renderType = material.GetTag("RenderType", false, "");

            // Treat EFT's Transparent/DepthZwriteDithered as transparent. It uses screen-space dithering for parts like the Magpul PMAG Gen M3 round-counter window. The material says TransparentCutout at queue 2000, but the shader has _MainTex, _Color and _OpacityScale and no _Cutoff for a matching coverage draw. Classifying it as cutout rejected every aim with that magazine. Leave it in EFT's main pass with the other glass.
            if (material.shader != null &&
                string.Equals(material.shader.name, DitheredTransparencyShader, StringComparison.Ordinal))
            {
                result = SlotClass.Transparent;
                return true;
            }

            // Unity Standard declares _Cutoff even in opaque mode. Trust an explicit opaque tag over that property, or a stock material such as xc_longslide_trit / xc_tritium can disable the whole aim. It logged shader=Standard queue=2000 RenderType=Opaque src=1 dst=0 zwrite=1 mode=0. Keep the _Cutoff fallback for shaders without a useful tag or keyword.
            bool declaredOpaque =
                renderType == "Opaque" ||
                (material.HasProperty(ModeId) && Mathf.RoundToInt(material.GetFloat(ModeId)) == 0);

            bool alphaTest =
                renderType == "TransparentCutout" ||
                material.renderQueue == 2450 ||
                material.IsKeywordEnabled("_ALPHATEST_ON") ||
                material.IsKeywordEnabled("ALPHATEST_ON") ||
                (HasCutoff(material) && renderType != "Transparent" && !declaredOpaque);

            bool blendTupleExposed =
                material.HasProperty(SrcBlendId) ||
                material.HasProperty(DstBlendId) ||
                material.HasProperty(ZWriteId);
            bool nonOpaqueTuple = false;
            if (blendTupleExposed)
            {
                int src = material.HasProperty(SrcBlendId) ? Mathf.RoundToInt(material.GetFloat(SrcBlendId)) : 1;
                int dst = material.HasProperty(DstBlendId) ? Mathf.RoundToInt(material.GetFloat(DstBlendId)) : 0;
                int z = material.HasProperty(ZWriteId) ? Mathf.RoundToInt(material.GetFloat(ZWriteId)) : 1;
                nonOpaqueTuple = src != 1 || dst != 0 || z != 1;
            }

            bool transparent =
                renderType == "Transparent" ||
                material.renderQueue > 2500 ||
                (material.HasProperty(SurfaceId) && Mathf.RoundToInt(material.GetFloat(SurfaceId)) == 1) ||
                (material.HasProperty(ModeId) &&
                 (Mathf.RoundToInt(material.GetFloat(ModeId)) == 2 ||
                  Mathf.RoundToInt(material.GetFloat(ModeId)) == 3)) ||
                nonOpaqueTuple;

            if (alphaTest && transparent)
            {
                failure = "conflicting alpha-test and transparent signals";
                return false;
            }

            if (transparent)
                result = SlotClass.Transparent;
            else if (alphaTest)
                result = SlotClass.Cutout;
            else if (renderType == "Opaque" || material.renderQueue <= 2500)
                result = SlotClass.Opaque;
            else
            {
                failure = "no supported opaque/cutout/transparent contract";
                return false;
            }

            return true;
        }

        /// <summary>The first-person arm renderers in this registry build (by name or by the Hands body part).</summary>
        private static readonly System.Collections.Generic.HashSet<Renderer> ArmRenderers = new System.Collections.Generic.HashSet<Renderer>();

        private static readonly HarmonyLib.AccessTools.FieldRef<EFT.Visual.LoddedSkin, Diz.Skinning.AbstractSkin[]> LodsField =
            HarmonyLib.AccessTools.FieldRefAccess<EFT.Visual.LoddedSkin, Diz.Skinning.AbstractSkin[]>("_lods");

        /// <summary>Adds the active renderers of the body's Hands part that the name rule missed. Returns how many.</summary>
        private static int AddHandsPartRenderers(EFT.PlayerBody body)
        {
            if (body.BodySkins == null || !body.BodySkins.TryGetValue(EFT.EBodyModelPart.Hands, out EFT.Visual.LoddedSkin hands) || hands == null)
                return 0;
            int added = 0;
            foreach (Diz.Skinning.AbstractSkin lod in LodsField(hands) ?? Array.Empty<Diz.Skinning.AbstractSkin>())
            {
                SkinnedMeshRenderer renderer = lod != null ? lod.SkinnedMeshRenderer : null;
                if (renderer == null || !renderer.gameObject.activeInHierarchy || !renderer.enabled || Scan.Contains(renderer))
                    continue;
                Scan.Add(renderer);
                ArmRenderers.Add(renderer);
                added++;
            }
            return added;
        }

        private static bool HasCutoff(Material material)
        {
            return material.HasProperty(CutoffId) ||
                   material.HasProperty(AlphaCutoffId) ||
                   material.HasProperty(CutoffValueId);
        }

        private static void FailUnsupported(
            Renderer renderer, int submesh, Material material, string reason)
        {
            string tags = material == null ? "<none>" :
                $"RenderType='{material.GetTag("RenderType", false, "")}'";
            string blend = material == null ? "<none>" :
                $"src={Property(material, SrcBlendId)} dst={Property(material, DstBlendId)} " +
                $"zwrite={Property(material, ZWriteId)} surface={Property(material, SurfaceId)} " +
                $"mode={Property(material, ModeId)}";
            FailSession(
                $"unsupported renderer | renderer='{renderer?.name ?? "<null>"}' submesh={submesh} " +
                $"material='{material?.name ?? "<null>"}' shader='{material?.shader?.name ?? "<null>"}' " +
                $"queue={(material == null ? -1 : material.renderQueue)} {tags} {blend} | {reason}");
        }

        private static string Property(Material material, int id)
        {
            return material.HasProperty(id) ? material.GetFloat(id).ToString("0.###") : "<absent>";
        }

        private static bool CreateMixedClones(Entry entry, Mesh mesh)
        {
            Material[] opaque = new Material[entry.SourceMaterials.Length];
            Material[] glass = new Material[entry.SourceMaterials.Length];

            for (int i = 0; i < entry.SourceMaterials.Length; i++)
            {
                Material source = entry.SourceMaterials[i];
                Material noDrawA = CreateNoDraw(source.renderQueue);
                Material noDrawB = CreateNoDraw(source.renderQueue);
                if (noDrawA == null || noDrawB == null)
                    return false;
                entry.OwnedNoDraw.Add(noDrawA);
                entry.OwnedNoDraw.Add(noDrawB);

                bool isTransparent =
                    i < entry.Slots.Length && entry.Slots[i] == SlotClass.Transparent;
                bool isUsed = i < entry.Slots.Length;
                opaque[i] = isUsed && !isTransparent ? source : noDrawA;
                glass[i] = isUsed && isTransparent ? source : noDrawB;
            }

            entry.OpaqueClone = CloneRenderer(
                entry.Source, mesh, opaque, "Aero Opaque Eye", out entry.OpaqueObject);
            entry.GlassClone = CloneRenderer(
                entry.Source, mesh, glass, "Aero Main Glass", out entry.GlassObject);
            if (entry.OpaqueClone == null || entry.GlassClone == null)
                return false;

            entry.OpaqueClone.enabled = false;
            entry.GlassClone.enabled = false;
            entry.GlassClone.shadowCastingMode = ShadowCastingMode.Off;
            return entry.OpaqueClone.sharedMaterials.All(m => m != null) &&
                   entry.GlassClone.sharedMaterials.All(m => m != null);
        }

        private static Material CreateNoDraw(int queue)
        {
            if (!Supported(ShaderAssets.NoDraw))
                return null;
            return new Material(ShaderAssets.NoDraw)
            {
                name = "Aero NoDraw",
                renderQueue = queue,
                hideFlags = HideFlags.HideAndDontSave,
            };
        }

        private static Renderer CloneRenderer(
            Renderer source, Mesh mesh, Material[] materials, string suffix, out GameObject host)
        {
            host = new GameObject(source.name + " [" + suffix + "]")
            {
                hideFlags = HideFlags.HideAndDontSave,
                layer = source.gameObject.layer,
            };
            host.transform.SetParent(source.transform, false);

            Renderer clone;
            if (source is MeshRenderer sourceMesh)
            {
                host.AddComponent<MeshFilter>().sharedMesh = mesh;
                var target = host.AddComponent<MeshRenderer>();
                target.additionalVertexStreams = sourceMesh.additionalVertexStreams;
                clone = target;
            }
            else if (source is SkinnedMeshRenderer sourceSkin)
            {
                var target = host.AddComponent<SkinnedMeshRenderer>();
                target.sharedMesh = sourceSkin.sharedMesh;
                target.bones = sourceSkin.bones;
                target.rootBone = sourceSkin.rootBone;
                target.localBounds = sourceSkin.localBounds;
                target.quality = sourceSkin.quality;
                target.updateWhenOffscreen = sourceSkin.updateWhenOffscreen;
                target.skinnedMotionVectors = sourceSkin.skinnedMotionVectors;
                clone = target;
            }
            else
            {
                Object.Destroy(host);
                host = null;
                return null;
            }

            clone.sharedMaterials = materials;
            CopyRendererState(source, clone);
            CopyPropertyBlocks(source, clone, materials.Length);
            return clone;
        }

        private static void CopyRendererState(Renderer source, Renderer clone)
        {
            clone.enabled = source.enabled;
            clone.shadowCastingMode = source.shadowCastingMode;
            clone.receiveShadows = source.receiveShadows;
            clone.lightProbeUsage = source.lightProbeUsage;
            clone.reflectionProbeUsage = source.reflectionProbeUsage;
            clone.probeAnchor = source.probeAnchor;
            clone.motionVectorGenerationMode = source.motionVectorGenerationMode;
            clone.allowOcclusionWhenDynamic = source.allowOcclusionWhenDynamic;
            clone.sortingLayerID = source.sortingLayerID;
            clone.sortingOrder = source.sortingOrder;
            clone.lightmapIndex = source.lightmapIndex;
            clone.lightmapScaleOffset = source.lightmapScaleOffset;
            clone.realtimeLightmapIndex = source.realtimeLightmapIndex;
            clone.realtimeLightmapScaleOffset = source.realtimeLightmapScaleOffset;
        }

        private static void CopyPropertyBlocks(Renderer source, Renderer target, int slots)
        {
            CloneBlock.Clear();
            source.GetPropertyBlock(CloneBlock);
            target.SetPropertyBlock(CloneBlock);
            for (int i = 0; i < slots; i++)
            {
                CloneBlock.Clear();
                source.GetPropertyBlock(CloneBlock, i);
                target.SetPropertyBlock(CloneBlock, i);
            }
        }

        private static bool CreateCoverageDraws(Entry entry)
        {
            if (entry.Kind == EntryKind.Ammunition)
                return true;

            Renderer drawRenderer = entry.Kind == EntryKind.Mixed ? entry.OpaqueClone : entry.Source;
            // Write one ID per renderer to coverage blue for iron-sight focus. All of its submeshes share that ID.
            PartNames.Add(entry.Source != null ? entry.Source.name : "?");
            float partId = PartNames.Count;
            for (int i = 0; i < entry.Slots.Length; i++)
            {
                // Skip coverage for transparent slots. With coverage.r == 0 the defocus gate leaves them sharp, but writing coverage would make the composite treat the sight picture as solid and blur it. Blurring these parts safely would need a defocus mask separate from the coverage used for blending.
                if (entry.Slots[i] == SlotClass.Transparent)
                    continue;

                Material source = entry.SourceMaterials[i];
                bool cutout = entry.Slots[i] == SlotClass.Cutout;
                var draw = new CoverageDraw
                {
                    Owner = entry,
                    Renderer = drawRenderer,
                    PropertySource = entry.Source,
                    Submesh = i,
                    SourceMaterial = source,
                    Cutout = cutout,
                };

                Shader shader = cutout ? ShaderAssets.CoverageCutout : ShaderAssets.CoverageOpaque;
                draw.CoverageMaterial = new Material(shader)
                {
                    name = cutout ? "Aero Coverage Cutout" : "Aero Coverage Opaque",
                    hideFlags = HideFlags.HideAndDontSave,
                    renderQueue = source.renderQueue,
                };
                draw.CoverageMaterial.SetFloat(CullId, ResolveCull(source));
                draw.CoverageMaterial.SetFloat(PartIdId, partId);

                if (cutout && !ResolveAlphaContract(draw))
                {
                    FailUnsupported(entry.Source, i, source, "alpha-tested slot lacks texture/cutoff contract");
                    Object.Destroy(draw.CoverageMaterial);
                    return false;
                }

                CoverageDraws.Add(draw);
            }
            return true;
        }

        private static float ResolveCull(Material material)
        {
            if (material.HasProperty(CullId))
                return material.GetFloat(CullId);
            if (material.HasProperty(CullModeId))
                return material.GetFloat(CullModeId);
            return (float)CullMode.Back;
        }

        private static bool ResolveAlphaContract(CoverageDraw draw)
        {
            Material material = draw.SourceMaterial;
            draw.TextureId = material.HasProperty(MainTexId) ? MainTexId :
                             material.HasProperty(BaseMapId) ? BaseMapId : 0;
            draw.CutoffId = material.HasProperty(CutoffId) ? CutoffId :
                            material.HasProperty(AlphaCutoffId) ? AlphaCutoffId :
                            material.HasProperty(CutoffValueId) ? CutoffValueId : 0;
            draw.TintId = material.HasProperty(ColorId) ? ColorId :
                          material.HasProperty(BaseColorId) ? BaseColorId : 0;
            draw.StId = draw.TextureId == MainTexId
                ? Shader.PropertyToID("_MainTex_ST")
                : Shader.PropertyToID("_BaseMap_ST");
            return draw.TextureId != 0 && draw.CutoffId != 0 &&
                   material.GetTexture(draw.TextureId) != null;
        }

        private static void RefreshCoverageMaterials()
        {
            if (_coverageRefreshFrame == Time.frameCount)
                return;
            _coverageRefreshFrame = Time.frameCount;

            for (int i = 0; i < CoverageDraws.Count; i++)
            {
                CoverageDraw draw = CoverageDraws[i];
                if (!draw.Cutout)
                    continue;

                Material source = draw.SourceMaterial;
                Material target = draw.CoverageMaterial;
                Texture texture = source.GetTexture(draw.TextureId);
                float cutoff = source.GetFloat(draw.CutoffId);
                Color tint = draw.TintId == 0 ? Color.white : source.GetColor(draw.TintId);
                Vector2 scale = source.GetTextureScale(draw.TextureId);
                Vector2 offset = source.GetTextureOffset(draw.TextureId);

                CoverageBlock.Clear();
                draw.PropertySource.GetPropertyBlock(CoverageBlock, draw.Submesh);
                if (!CoverageBlock.isEmpty)
                {
                    if (CoverageBlock.HasProperty(draw.TextureId) &&
                        CoverageBlock.GetTexture(draw.TextureId) != null)
                        texture = CoverageBlock.GetTexture(draw.TextureId);
                    if (CoverageBlock.HasProperty(draw.CutoffId))
                        cutoff = CoverageBlock.GetFloat(draw.CutoffId);
                    if (draw.TintId != 0 && CoverageBlock.HasProperty(draw.TintId))
                        tint = CoverageBlock.GetColor(draw.TintId);
                    if (CoverageBlock.HasProperty(draw.StId))
                    {
                        Vector4 st = CoverageBlock.GetVector(draw.StId);
                        scale = new Vector2(st.x, st.y);
                        offset = new Vector2(st.z, st.w);
                    }
                }

                target.SetTexture(MainTexId, texture);
                target.SetTextureScale("_MainTex", scale);
                target.SetTextureOffset("_MainTex", offset);
                target.SetFloat(CutoffId, cutoff);
                target.SetColor(ColorId, tint);
                target.SetFloat(CullId, ResolveCull(source));
            }
        }

        private static bool ValidateSourceArrays()
        {
            for (int i = 0; i < Entries.Count; i++)
            {
                Entry entry = Entries[i];
                if (entry.Source == null)
                    return false;
                Material[] current = entry.Source.sharedMaterials;
                if (current == null || current.Length != entry.SourceMaterials.Length)
                    return false;
                for (int j = 0; j < current.Length; j++)
                    if (current[j] != entry.SourceMaterials[j])
                        return false;
            }
            return true;
        }

        // EFT recreates the chambered round while firing. Refresh only the patron_ renderers so ordinary parts don't trigger continuous registry rebuilds.
        private static void RefreshAmmunition()
        {
            for (int i = Entries.Count - 1; i >= 0; i--)
            {
                if (Entries[i].Kind == EntryKind.Ammunition && Entries[i].Source == null)
                    Entries.RemoveAt(i);
            }

            if (_weaponRoot == null)
                return;
            Scan.Clear();
            _weaponRoot.GetComponentsInChildren(false, Scan);
            for (int i = 0; i < Scan.Count; i++)
            {
                Renderer renderer = Scan[i];
                if (!WeaponRendererPolicy.IsAmmunition(renderer))
                    continue;

                bool exists = false;
                for (int j = 0; j < Entries.Count; j++)
                {
                    if (Entries[j].Source == renderer)
                    {
                        exists = true;
                        break;
                    }
                }
                if (exists)
                    continue;

                Mesh mesh = GetMesh(renderer);
                Material[] materials = renderer.sharedMaterials;
                if (mesh == null || materials == null || materials.Length < mesh.subMeshCount)
                    continue;
                var slots = new SlotClass[mesh.subMeshCount];
                Entries.Add(NewEntry(renderer, materials, slots, EntryKind.Ammunition));
                if (Plugin.Debug.Value)
                    Plugin.Log.LogInfo(
                        $"[BinoADS] dynamic ammunition ownership | renderer='{renderer.name}' hidden=true");
            }
            Scan.Clear();
        }
    }
}
