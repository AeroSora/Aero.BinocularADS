using System;
using System.IO;
using System.Text.RegularExpressions;

namespace Aero.BinocularADS.Tests
{
    // Check tap spacing at the default radius and three others against the tuned float32 values. Also check the radius convention and make sure DefocusKernel.OuterTapSteps matches the shader's AERO_OFFSET_2.
    internal static class Program
    {
        private static int _failures;

        private static void Check(string name, bool passed, string detail)
        {
            Console.WriteLine($"{(passed ? "PASS" : "FAIL")}  {name} ({detail})");
            if (!passed)
                _failures++;
        }

        private static int Main(string[] args)
        {
            // Recorded spacing in blur-target texels for each radius in full-resolution pixels.
            var recorded = new (float Radius, float Step)[]
            {
                (15f, 1.6414979696273804f), // default
                (5f, 0.5471659898757935f),
                (25f, 2.735830068588257f),
                (30f, 3.2829959392547607f), // maximum
            };
            foreach (var (radius, step) in recorded)
            {
                float got = DefocusKernel.StepTexels(radius);
                float relative = Math.Abs(got - step) / step;
                Check($"step at radius {radius} px matches the recorded value", relative <= 1e-4f,
                      $"got {got:R}, recorded {step:R}, relative difference {relative:E2}");
            }

            // The radius equals the outer tap offset times sqrt(Iterations), expressed in weapon-image pixels. The recorded values above check the actual output.
            foreach (float radius in new[] { 1f, 15f, 30f })
            {
                double reachPixels = DefocusKernel.StepTexels(radius) * DefocusKernel.OuterTapSteps *
                                     Math.Sqrt(DefocusKernel.Iterations) * DefocusKernel.Downsample;
                Check($"normalisation convention at radius {radius} px", Math.Abs(reachPixels - radius) <= 1e-4 * radius,
                      $"reach {reachPixels:0.000000} px");
            }
            Check("radius 0 gives step 0", DefocusKernel.StepTexels(0f) == 0f, $"got {DefocusKernel.StepTexels(0f)}");

            string shader = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Shaders",
                                         "AeroWeaponDefocusBlur.shader");
            Match offset = File.Exists(shader)
                ? Regex.Match(File.ReadAllText(shader), @"#define\s+AERO_OFFSET_2\s+([0-9.]+)")
                : Match.Empty;
            Check("OuterTapSteps equals AERO_OFFSET_2 in the blur shader",
                  offset.Success && float.Parse(offset.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) == DefocusKernel.OuterTapSteps,
                  offset.Success ? $"shader {offset.Groups[1].Value}, kernel {DefocusKernel.OuterTapSteps:R}" : $"not found in {Path.GetFullPath(shader)}");

            Console.WriteLine(_failures == 0 ? "DefocusKernelCheck: all passed" : $"DefocusKernelCheck: {_failures} failed");
            return _failures == 0 ? 0 : 1;
        }
    }
}
