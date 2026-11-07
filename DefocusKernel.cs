using System;

namespace Aero.BinocularADS
{
    // AeroWeaponDefocusBlur.shader uses five linear samples to implement a nine-tap Gaussian. Run a horizontal and vertical pass per Iterations count at 1/Downsample resolution. The math can be tested without Unity.
    internal static class DefocusKernel
    {
        // Outermost paired tap of the nine-tap Gaussian, in steps. Keep this in sync with AERO_OFFSET_2 in the shader. See Daniel Rakos, "Efficient Gaussian blur with linear sampling" (RasterGrid, 2010).
        public const float OuterTapSteps = 3.23076923f;

        // Divide each weapon-image dimension by Downsample for the blur target.
        public const int Downsample = 2;

        // Number of horizontal/vertical pairs. RecordDefocusBlur should record twice as many passes.
        public const int Iterations = 2;

        // Convert Maximum Radius from full-size weapon pixels to the spacing between taps in blur-target texels.
        //
        // The radius is defined as OuterTapSteps × sqrt(Iterations), converted to full-size pixels. Variances add across passes, so the square-root factor keeps the blur strength consistent when the iteration count changes. One pass has a standard deviation of about 1.637 steps, putting the radius at about 1.97 standard deviations for the combined blur. Divide by Downsample to convert full-size pixels to blur-target texels.
        public static float StepTexels(float radiusPixels)
        {
            float radiusInBlurTexels = radiusPixels / Downsample;
            float iterationScale = (float)Math.Sqrt(Iterations);
            float tapSpacing = radiusInBlurTexels / OuterTapSteps;

            return tapSpacing / iterationScale;
        }
    }
}
