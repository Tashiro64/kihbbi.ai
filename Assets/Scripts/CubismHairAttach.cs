using Live2D.Cubism.Core;
using UnityEngine;

public class HairFollowParams : MonoBehaviour
{
    CubismParameter angleX, angleY, angleZ;

    void Start()
    {
        var model = FindFirstObjectByType<CubismModel>();

        foreach (var p in model.Parameters)
        {
            if (p.Id == "ParamAngleX") angleX = p;
            if (p.Id == "ParamAngleY") angleY = p;
            if (p.Id == "ParamAngleZ") angleZ = p;
        }
    }

    void Update()
    {
        if (angleX == null) return;

        float x = angleX.Value;
        float y = angleY.Value;
        float z = angleZ.Value;

        transform.localRotation = Quaternion.Euler(-y * 0.7f, x * 0.8f, -z * 0.6f);
        transform.localPosition = new Vector3(x * 0.002f, y * 0.002f, 0);
    }
}