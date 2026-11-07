using System.Collections.Generic;
using EFT.Animations;
using HarmonyLib;
using UnityEngine;

namespace Aero.BinocularADS
{
    // Use EFT's iron-sight alignment markers as the focus anchor. FindAimTransforms picks the deepest mod_align_rear and mod_align_front under the weapon for first-person iron sights. ProceduralWeaponAnimation keeps them in _bone0 and _bone1 and uses them to align the camera and shot direction when CurrentScope.AlignIfAvailable is set. The front marker stays on the intended sight line even when bulky sights confuse a screen-space coverage search.
    //
    // Project its live world position through the dominant eye each frame without a readback. Keep nearby weapon surfaces sharp. If the current scope does not align, the pair is missing or a marker is inactive, blur the dominant image uniformly.
    internal static class NativeFrontMarker
    {
        private static readonly AccessTools.FieldRef<ProceduralWeaponAnimation, Transform> RearRef =
            GameFields.TryRef<ProceduralWeaponAnimation, Transform>("_bone0");
        private static readonly AccessTools.FieldRef<ProceduralWeaponAnimation, Transform> FrontRef =
            GameFields.TryRef<ProceduralWeaponAnimation, Transform>("_bone1");

        internal static string Status { get; private set; } = "not checked";
        internal static Transform Marker { get; private set; }
        internal static Transform Rear { get; private set; }

        // Return the front marker for the active scope, or null with the reason in Status.
        internal static Transform Front(ProceduralWeaponAnimation pwa)
        {
            Marker = null;
            Rear = null;
            if (pwa == null) { Status = "no weapon animation"; return null; }
            var scope = BinocularController.CurrentScopeOrNull(pwa);
            if (scope == null) { Status = "no aim transforms"; return null; }
            if (!scope.AlignIfAvailable) { Status = "current scope does not use alignment"; return null; }
            if (FrontRef == null || RearRef == null) { Status = "alignment marker fields unavailable in this game version"; return null; }
            Transform front = FrontRef(pwa), rear = RearRef(pwa);
            if (front == null || rear == null) { Status = "no mod_align_rear/mod_align_front pair on this weapon"; return null; }
            // FindAimTransforms checks active transforms on equip and attachment changes. OnScopesModeUpdated does not rerun it, so folding a sight can leave a cached marker inactive.
            if (!front.gameObject.activeInHierarchy || !rear.gameObject.activeInHierarchy)
            {
                Status = $"marker inactive since the game found it (front '{front.name}' active={front.gameObject.activeInHierarchy}, rear '{rear.name}' active={rear.gameObject.activeInHierarchy})";
                return null;
            }
            Status = $"front '{front.name}' rear '{rear.name}'";
            Marker = front;
            Rear = rear;
            return front;
        }

        // Project this frame's marker into coverage UV and camera depth using the eye's view and projection.
        internal static bool Project(Vector3 world, Matrix4x4 view, Matrix4x4 projection, out Vector2 uv, out float depth)
        {
            uv = default;
            Vector3 viewPoint = view.MultiplyPoint3x4(world);
            depth = -viewPoint.z;
            if (depth <= 1e-3f)
                return false;
            Vector4 clip = projection * new Vector4(viewPoint.x, viewPoint.y, viewPoint.z, 1f);
            uv = new Vector2(clip.x / clip.w * 0.5f + 0.5f, clip.y / clip.w * 0.5f + 0.5f);
            return true;
        }

        internal static void Invalidate()
        {
            Marker = null;
            Rear = null;
        }

        private static readonly AccessTools.FieldRef<ProceduralWeaponAnimation, bool> HasPairRef =
            GameFields.TryRef<ProceduralWeaponAnimation, bool>("_hasPairOfIronSightTransforms");

        private static string PathOf(Transform t, Transform stop)
        {
            if (t == null)
                return "<null>";
            var names = new List<string>();
            for (Transform a = t; a != null && a != stop; a = a.parent)
                names.Add(a.name);
            names.Reverse();
            return string.Join("/", names);
        }

        // Dump each ScopeAimTransforms entry (bone path, sight item and AlignIfAvailable), the current index, both marker paths and active states, and EFT's pair flag.
        internal static string DescribeAimPoints(ProceduralWeaponAnimation pwa)
        {
            if (pwa == null)
                return "aim points: no weapon animation";
            Transform root = pwa.HandsContainer?.Weapon;
            var list = pwa.ScopeAimTransforms;
            var sb = new System.Text.StringBuilder();
            sb.Append($"aim points: AimIndex={pwa.AimIndex} count={list?.Count ?? 0}");
            if (list != null)
                for (int i = 0; i < list.Count; i++)
                {
                    var e = list[i];
                    string item = e.Mod?.Item?.Name ?? "<no sight item>";
                    sb.Append($" | [{i}] bone='{PathOf(e.Bone, root)}' item='{item}' align={e.AlignIfAvailable} optic={e.IsOptic}");
                }
            Transform rear = RearRef != null ? RearRef(pwa) : null, front = FrontRef != null ? FrontRef(pwa) : null;
            sb.Append($" | _bone0(rear)='{PathOf(rear, root)}' active={(rear != null && rear.gameObject.activeInHierarchy)}");
            sb.Append($" _bone1(front)='{PathOf(front, root)}' active={(front != null && front.gameObject.activeInHierarchy)}");
            sb.Append($" hasPair={(HasPairRef != null ? HasPairRef(pwa).ToString() : "unavailable")}");
            return sb.ToString();
        }

        internal static string Describe() =>
            $"native marker: {Status}";
    }
}
