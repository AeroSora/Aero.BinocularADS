using UnityEngine;

namespace Aero.BinocularADS
{
    // Attach the watchdog to the gameplay camera because OnPostRender only reaches components on the camera that rendered. Restore the native viewmodel each frame even if the pipeline failed after hiding it.
    //
    // Keep compositing out of OnRenderImage. It takes over EFT's post-processing chain, flipping the frame and shrinking it to roughly 70% of the screen.
    [DisallowMultipleComponent]
    public class MainCameraHook : MonoBehaviour
    {
        private void OnPostRender()
        {
            ProductionPipeline.OnPostRender(GetComponent<Camera>());
        }

        private void OnDisable()
        {
            ProductionPipeline.RestoreSuppression("main camera disabled");
        }
    }
}
