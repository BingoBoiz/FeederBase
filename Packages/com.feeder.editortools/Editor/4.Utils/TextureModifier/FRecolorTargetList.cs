#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using Sirenix.OdinInspector;
using UnityEngine;

namespace Feeder
{
    /// <summary>
    /// Target list shared by both Feeder Texture Modifier tabs, drawn with Odin like the
    /// Feeder menu tools. The active entry drives the single-target analyze/edit/preview
    /// session; batch apply runs over every entry.
    /// </summary>
    [Serializable]
    public sealed class FRecolorTargetList
    {
        [PropertyOrder(0)]
        [LabelText("Targets (dùng chung 2 tab)")]
        [ListDrawerSettings(ShowFoldout = true, DraggableItems = true, ShowIndexLabels = true, NumberOfItemsPerPage = 10)]
        [SerializeField]
        private List<GameObject> targets = new List<GameObject>();

        [SerializeField, HideInInspector]
        private GameObject activeTarget;

        [PropertyOrder(0)]
        [ButtonGroup("RecolorSelRow"), Button("Add Selection", ButtonSizes.Small)]
        private void AddSelection()
        {
            var picked = FSelectionUtils.CollectGameObjects();
            if (picked.Count == 0)
            {
                Debug.LogWarning("[Texture Modifier] Chưa chọn GameObject nào.");
                return;
            }
            foreach (var go in picked)
                if (!targets.Contains(go))
                    targets.Add(go);
        }

        [PropertyOrder(0)]
        [ButtonGroup("RecolorSelRow"), Button("Replace With Selection", ButtonSizes.Small)]
        private void ReplaceWithSelection()
        {
            targets.Clear();
            AddSelection();
        }

        [PropertyOrder(0)]
        [ButtonGroup("RecolorSelRow"), Button("Clear", ButtonSizes.Small)]
        private void ClearTargets()
        {
            targets.Clear();
        }

        public int Count => targets.Count;

        /// <summary>
        /// The entry driving the single-target session. Falls back to the first valid entry
        /// when nothing was picked yet or the picked entry left the list.
        /// </summary>
        [PropertyOrder(1)]
        [ShowInInspector]
        [LabelText("Target đang chỉnh")]
        [ValueDropdown(nameof(GetActiveOptions))]
        public GameObject ActiveTarget
        {
            get
            {
                if (activeTarget != null && targets.Contains(activeTarget))
                    return activeTarget;

                for (int i = 0; i < targets.Count; i++)
                    if (targets[i] != null)
                        return targets[i];
                return null;
            }
            set => activeTarget = value;
        }

        public Renderer ActiveRenderer => ResolveRenderer(ActiveTarget);

        /// <summary>Resolves the renderer of a target: its own Renderer first, then the first child's.</summary>
        public static Renderer ResolveRenderer(GameObject go)
        {
            if (go == null) return null;
            var own = go.GetComponent<Renderer>();
            return own != null ? own : go.GetComponentInChildren<Renderer>(true);
        }

        /// <summary>All resolvable renderers in list order, without nulls or duplicates.</summary>
        public List<Renderer> ResolveAllRenderers()
        {
            var result = new List<Renderer>();
            for (int i = 0; i < targets.Count; i++)
            {
                var r = ResolveRenderer(targets[i]);
                if (r != null && !result.Contains(r))
                    result.Add(r);
            }
            return result;
        }

        private IEnumerable<GameObject> GetActiveOptions()
        {
            var seen = new HashSet<GameObject>();
            for (int i = 0; i < targets.Count; i++)
                if (targets[i] != null && seen.Add(targets[i]))
                    yield return targets[i];
        }
    }
}
#endif
