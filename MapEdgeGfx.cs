using UnityEngine;
using UnityEngine.Rendering;
#if UNITY_EDITOR
using UnityEditor;
#endif

// Component for adding line rendering to map edges
// Only one LineRenderer is allowed per GameObject for some reason so each
// outline needs to be a separate GameObject
[ExecuteInEditMode] // to ensure that we set up material property block in editor
public class MapEdgeGfx : MonoBehaviour {
    Vector3[] points;
    Material material;
    LineRenderer lineRenderer;
    MaterialPropertyBlock matBlock;
    float lineWidth;
    bool materialBlockIsSetUp;

    // Need to save this so we can use it when loading serialized renderer
    [SerializeField]
    float lineSqrLen;
    // Everything the line setup does is serialized
    [SerializeField]
    bool lineIsSetUp;

    void GetReferences() {
        if (lineRenderer == null) {
            lineRenderer = GetComponent<LineRenderer>();
        }
        if (lineRenderer == null) {
#if UNITY_EDITOR
            lineRenderer = Application.isEditor ? Undo.AddComponent<LineRenderer>(gameObject) : gameObject.AddComponent<LineRenderer>();
#else
            lineRenderer = gameObject.AddComponent<LineRenderer>();
#endif

            lineRenderer.receiveShadows = false;
            lineRenderer.shadowCastingMode = ShadowCastingMode.Off;
        }
        if (matBlock == null) {
            matBlock = new MaterialPropertyBlock();
        }
    }

    void Awake() {
        GetReferences();
    }
    void Start()
    {
        UpdateLine();
        UpdateMaterialPropertyBlock();
    }

    public void SetEdges(Vector3[] pts, Material mat, float width, float sqrLen) {
        GetReferences();
        points = pts;
        material = mat;
        lineWidth = width;
        lineSqrLen = sqrLen;
        lineIsSetUp = false;
        UpdateLine();
        materialBlockIsSetUp = false;
        UpdateMaterialPropertyBlock();
    }
    void UpdateLine() {
        if (lineRenderer != null && !lineIsSetUp && points != null) {
#if UNITY_EDITOR
            if (Application.isEditor)
                Undo.RecordObject(lineRenderer, "Change LineRenderer points");
#endif
            lineRenderer.numPositions = points.Length;
            lineRenderer.startWidth = lineWidth;
            lineRenderer.endWidth = lineWidth;
            lineRenderer.SetPositions(points);
            lineRenderer.sharedMaterial = material;
            lineIsSetUp = true;
        }
    }
    void UpdateMaterialPropertyBlock() {
        if (lineRenderer != null && !materialBlockIsSetUp) {
            // Instead of instantiating a material, use MaterialPropertyBlock
            // to set instance-specific parameters
            // Changes here are not serialized, so we have to do it each time
            lineRenderer.GetPropertyBlock(matBlock);
            matBlock.SetFloat("_GlowScale", lineSqrLen * 0.03f);
            lineRenderer.SetPropertyBlock(matBlock);

        }
    }


}