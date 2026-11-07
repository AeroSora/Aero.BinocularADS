using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Aero.BinocularADS
{
    internal static partial class ProductionPipeline
    {
        private static void RestoreAmmunition()
        {
            for (int i = 0; i < Entries.Count; i++)
            {
                Entry entry = Entries[i];
                if (entry.Kind == EntryKind.Ammunition && entry.Source != null)
                {
                    entry.Source.forceRenderingOff = entry.SessionOriginalForceOff;
                    entry.Source.shadowCastingMode = entry.SessionOriginalShadow;
                }
            }
        }

        private static void DestroyRegistry()
        {
            OpticGlass.Clear();
            _loggedGlass = false;
            // Clear cached and pending anchors before reassigning renderers and part IDs. Old IDs can disappear or point to another part.
            _registryGeneration++;
            NativeFrontMarker.Invalidate();
            RestoreAmmunition();
            for (int i = 0; i < Entries.Count; i++)
                DestroyEntry(Entries[i]);
            Entries.Clear();

            for (int i = 0; i < CoverageDraws.Count; i++)
                if (CoverageDraws[i].CoverageMaterial != null)
                    Object.Destroy(CoverageDraws[i].CoverageMaterial);
            CoverageDraws.Clear();
            PartNames.Clear();
            SightPicture.Clear();
            _registryReady = false;
        }

        private static int Id(Object o) => o == null ? 0 : o.GetInstanceID();

        // Log resource IDs at the first capture of each aim so consecutive aims show which objects were recreated.
        private static void LogChurn()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"[BinoADS] churn frame {Time.frameCount} | registryGen={_registryGeneration} entries={Entries.Count} draws={CoverageDraws.Count}");
            sb.Append($" | eye={Id(_eye)}");
            if (_eye != null)
                foreach (Behaviour b in _eye.GetComponents<Behaviour>())
                    sb.Append($" {b.GetType().Name}={Id(b)}{(b.enabled ? "" : "(off)")}");
            sb.Append($" | targets eyeColorA={Id(_eyeColorA)} coverageA={Id(_coverageA)} coverageB={Id(_coverageB)} weaponA={Id(_weaponA)} weaponB={Id(_weaponB)} blurA={Id(_blurA)} blurB={Id(_blurB)} reallocs={_targetReallocs}");
            sb.Append($" | coverageMat0={(CoverageDraws.Count > 0 ? Id(CoverageDraws[0].CoverageMaterial) : 0)}");
            sb.Append($" clone0={(Entries.Count > 0 ? Id(Entries[0].OpaqueClone) : 0)}");
            sb.Append($" | managedMB={System.GC.GetTotalMemory(false) / 1048576.0:0.0}");
            Plugin.Log.LogInfo(sb.ToString());
        }

        private static void DestroyEntry(Entry entry)
        {
            if (entry == null)
                return;
            if (entry.Source != null)
            {
                entry.Source.forceRenderingOff = entry.SessionOriginalForceOff;
                entry.Source.shadowCastingMode = entry.SessionOriginalShadow;
            }
            if (entry.OpaqueObject != null)
                Object.Destroy(entry.OpaqueObject);
            if (entry.GlassObject != null)
                Object.Destroy(entry.GlassObject);
            for (int i = 0; i < entry.OwnedNoDraw.Count; i++)
                if (entry.OwnedNoDraw[i] != null)
                    Object.Destroy(entry.OwnedNoDraw[i]);
        }

        private static void ReleaseTargets()
        {
            Release(ref _eyeColorA);
            Release(ref _coverageA);
            Release(ref _coverageB);
            Release(ref _weaponA);
            Release(ref _weaponB);
            Release(ref _blurA);
            Release(ref _blurB);
            Release(ref _preLens);
            _lensCopySerial = 0;
            DeveloperReleaseTargets();
            _targetWidth = 0;
            _targetHeight = 0;
            _blurWidth = 0;
            _blurHeight = 0;
        }

        private static void Release(ref RenderTexture texture)
        {
            if (texture == null)
                return;
            texture.Release();
            Object.Destroy(texture);
            texture = null;
        }

        public static void Dispose()
        {
            End("production disposed");
            EyeAmbientOcclusion.Dispose();
            EyeReflectionIntensity.Dispose();
            DeveloperDispose();
            ReleaseTargets();

            if (_eye != null)
            {
                if (_coverageCommand != null)
                    _eye.RemoveCommandBuffer(CameraEvent.AfterEverything, _coverageCommand);
                if (_highlightCommand != null)
                    _eye.RemoveCommandBuffer(CameraEvent.AfterLighting, _highlightCommand);
                Object.Destroy(_eye.gameObject);
                _eye = null;
            }
            _coverageCommand?.Release();
            _coverageCommand = null;
            _highlightCommand?.Release();
            _highlightCommand = null;
            _highlightOwner = null;
            _highlightOwnerCache = null;
            if (_lensBlendMaterial != null)
                Object.Destroy(_lensBlendMaterial);
            _lensBlendMaterial = null;
            _deriveCommand?.Release();
            _deriveCommand = null;
            if (_premultiply != null)
                Object.Destroy(_premultiply);
            if (_reproject != null)
                Object.Destroy(_reproject);
            _reproject = null;
            if (_composite != null)
                Object.Destroy(_composite);
            if (_blurPasses != null)
            {
                for (int i = 0; i < _blurPasses.Length; i++)
                    if (_blurPasses[i] != null)
                        Object.Destroy(_blurPasses[i]);
                _blurPasses = null;
            }
            if (_quad != null)
                Object.Destroy(_quad);
            _premultiply = null;
            _composite = null;
            _quad = null;
            if (_hook != null)
                Object.Destroy(_hook);
            _hook = null;
            _ambientOwner = null;
        }
    }
}
