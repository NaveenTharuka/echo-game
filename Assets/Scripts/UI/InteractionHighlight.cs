using System.Collections.Generic;
using Echo.Interaction;
using Echo.Navigation;
using UnityEngine;
using UnityEngine.Rendering;

namespace Echo.UI
{
    /// Soft rim glow on the interactable under the crosshair (Echo/InteractRim). It draws a second copy of each of the
    /// object's meshes with the rim material, as a child of the mesh, so the level's own materials never change and
    /// every sub-mesh glows. Only rebuilt when the focus changes (no per-frame allocations); hidden while the object is
    /// held. Doors glow on their moving leaf only, not on the whole frame and wall.
    public class InteractionHighlight : MonoBehaviour
    {
        public PlayerInteractor interactor;
        [Tooltip("Echo/InteractRim material.")]
        public Material rimMaterial;

        readonly List<GameObject> shells = new();
        readonly List<MeshFilter> filters = new();
        IInteractable lit;
        bool shown;

        void Awake()
        {
            if (interactor == null) interactor = FindFirstObjectByType<PlayerInteractor>();
        }

        void OnDisable() => Clear();

        void LateUpdate()
        {
            IInteractable focus = interactor != null && interactor.isActiveAndEnabled ? interactor.Focus : null;
            if (focus is Object o && o == null) focus = null;
            if (!ReferenceEquals(focus, lit)) Build(focus);

            // Hands full: the held prop is the focus (E drops it) but needs no "you can use this" glow.
            bool show = lit != null && !(lit is Carryable c && c.CurrentPhase == Carryable.Phase.Held);
            if (show != shown)
            {
                shown = show;
                foreach (var s in shells) if (s != null) s.SetActive(show);
            }
        }

        void Build(IInteractable focus)
        {
            Clear();
            lit = focus;
            if (rimMaterial == null || !(focus is Component target)) return;

            Transform root = focus is Door door && door.leaf != null ? door.leaf.transform : target.transform;
            root.GetComponentsInChildren(filters);
            foreach (var mf in filters)
            {
                if (mf.sharedMesh == null || mf.name == ShellName) continue;
                if (!mf.TryGetComponent(out MeshRenderer source) || !source.enabled) continue;
                var shell = new GameObject(ShellName);
                shell.layer = mf.gameObject.layer;
                shell.transform.SetParent(mf.transform, false);
                shell.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
                var mr = shell.AddComponent<MeshRenderer>();
                var mats = new Material[mf.sharedMesh.subMeshCount];
                for (int i = 0; i < mats.Length; i++) mats[i] = rimMaterial;
                mr.sharedMaterials = mats;
                mr.shadowCastingMode = ShadowCastingMode.Off;
                mr.receiveShadows = false;
                mr.lightProbeUsage = LightProbeUsage.Off;
                mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
                shell.SetActive(false);
                shells.Add(shell);
            }
            shown = false;
        }

        const string ShellName = "_InteractHighlight";

        void Clear()
        {
            foreach (var s in shells) if (s != null) Destroy(s);
            shells.Clear();
            lit = null;
            shown = false;
        }

        /// Test hook: how many highlight meshes are showing now.
        public int VisibleShells
        {
            get { int n = 0; foreach (var s in shells) if (s != null && s.activeSelf) n++; return n; }
        }
    }
}
