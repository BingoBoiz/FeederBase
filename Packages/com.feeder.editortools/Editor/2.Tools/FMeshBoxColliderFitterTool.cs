using Sirenix.OdinInspector;
using Sirenix.Serialization;
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
        [ShowInInspector, OdinSerialize]
        private bool overwriteExisting = true;

        [Button(ButtonSizes.Large), GUIColor(0.3f, 0.8f, 1f)]
        public void FitColliders()
        {
            int count = FMeshBoxColliderFitterService.FitAll(TargetPrefabs, overwriteExisting);
            Debug.Log($"<color=green>Fitted {count} BoxCollider(s).</color>");
        }
    }
}
