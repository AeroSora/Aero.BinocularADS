using System;
using System.Reflection;
using UnityEngine;

namespace Aero.BinocularADS
{
    // Light the eye images with TraceSix's indirect light so the aimed weapon matches the room. Attach the same light pass TraceSix puts on scope cameras. It swaps the game's flat sky light for TraceSix's light around each eye render and restores it afterwards.
    //
    // The eye renders before the main camera sets up its own pass, so the main view should be unaffected. That hasn't been measured yet.
    //
    // The pass follows TraceSix's "Indirect Light In Scopes" setting. It uses TraceSix's probe light even when ray tracing lights the main view. The second eye image is built from the first, so it gets the same light.
    //
    // If TraceSix isn't loaded or its internals have changed, keep the game's ambient and log why once.
    internal static partial class EyeProbeLight
    {
        // EyeProbeLight.Developer.cs implements a live off switch for A/B tests. Release builds drop the call.
        static partial void DeveloperPassOff(ref bool off);
        static partial void DeveloperPassState(bool on);

        private const string PluginGuid = "com.matsix.tracesix";
        private const string ComponentType = "TraceSix.Source.GiCamera";

        private static bool _resolved;
        private static Behaviour _pass;
        private static bool _passOffLogged;
        private static Type _component;
        private static FieldInfo _scope;

        internal static void Attach(Camera eye)
        {
            if (eye == null || !Resolve())
                return;
            if (eye.GetComponent(_component) != null)
                return;
            try
            {
                Component pass = eye.gameObject.AddComponent(_component);
                _scope.SetValue(pass, true);
                _pass = pass as Behaviour;
                Plugin.Diagnostic($"[BinoADS] probe light: TraceSix's scope light pass attached to the eye camera (follows its \"Indirect Light In Scopes\" setting)");
            }
            catch (Exception e)
            {
                _component = null;
                Plugin.Log.LogWarning($"[BinoADS] probe light: attaching TraceSix's light pass failed, eye images keep the game's ambient ({e.GetType().Name}: {e.Message})");
            }
        }

        // Call before each eye render. Disabling the component removes its command buffers and skips its render callbacks.
        internal static void Sync()
        {
            if (_pass == null)
                return;
            bool off = false;
            DeveloperPassOff(ref off);
            if (_pass.enabled != !off)
            {
                _pass.enabled = !off;
                if (off != _passOffLogged)
                {
                    _passOffLogged = off;
                    Plugin.Diagnostic($"[BinoADS] probe light: TraceSix's light pass on the eye camera {(off ? "switched off" : "switched back on")} at frame {Time.frameCount} (sun readings on this line predate the switch)");
                }
            }
            DeveloperPassState(_pass.enabled);
        }

        private static bool Resolve()
        {
            if (_resolved)
                return _component != null;
            _resolved = true;

            if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(PluginGuid, out var info) || info.Instance == null)
            {
                Plugin.Diagnostic("[BinoADS] probe light: TraceSix not loaded, eye images use the game's ambient");
                return false;
            }

            Type type = info.Instance.GetType().Assembly.GetType(ComponentType, false);
            FieldInfo scope = type?.GetField("Scope", BindingFlags.Public | BindingFlags.Instance);
            if (type == null || !typeof(MonoBehaviour).IsAssignableFrom(type) || scope == null || scope.FieldType != typeof(bool))
            {
                Plugin.Log.LogWarning($"[BinoADS] probe light: TraceSix {info.Metadata.Version} has no {ComponentType} with a bool Scope field, eye images use the game's ambient");
                return false;
            }

            _component = type;
            _scope = scope;
            return true;
        }
    }
}
