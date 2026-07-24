using Sirenix.OdinInspector;
using UnityEngine;

namespace Feeder
{
    public sealed class FMeshBoxColliderFitterTool : FTargetPrefabsToolBase
    {
        protected override string GetDescription() =>
            "Kéo thả các GameObject vào list, bấm Fit để tạo child _Col chứa BoxCollider khớp chính xác với mesh (OBB). " +
            "Tất cả MeshRenderer trong hierarchy đều được xử lý, mỗi cái có một _Col riêng.";

        [Title("Settings")]
        [LabelText("Overwrite Existing _Col")]
        [ShowInInspector]
        private bool overwriteExisting
        {
            get => FToolPrefs.GetBool(nameof(FMeshBoxColliderFitterTool), nameof(overwriteExisting), true);
            set => FToolPrefs.SetBool(nameof(FMeshBoxColliderFitterTool), nameof(overwriteExisting), value);
        }

        [Button(ButtonSizes.Large), GUIColor(0.3f, 0.8f, 1f)]
        public void FitColliders()
        {
            int count = FMeshBoxColliderFitterService.FitAll(TargetPrefabs, overwriteExisting);
            FSelectionUtils.SelectAndPing(TargetPrefabs);
            Debug.Log($"<color=green>Fitted {count} BoxCollider(s).</color>");
        }
    }
}
