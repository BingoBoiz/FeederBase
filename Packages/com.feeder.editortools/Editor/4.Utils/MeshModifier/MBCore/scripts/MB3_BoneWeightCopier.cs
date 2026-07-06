namespace Feeder.MB {
using UnityEngine;
using System;
using System.Collections;

[UnityEngine.AddComponentMenu("")]
public class MB3_BoneWeightCopier : MonoBehaviour {
	public GameObject inputGameObject;
    public GameObject outputPrefab;
    public float radius = .01f;
    public SkinnedMeshRenderer seamMesh;
    public string outputFolder;
}

}
