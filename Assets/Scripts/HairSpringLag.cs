using UnityEngine;

public class HairSpringLagWithRotationOffset : MonoBehaviour
{
    [Header("Driver")]
    public Transform driver;                // Assign: Hairs

    [Header("Position Spring")]
    public float strength = 0.02f;
    public float stiffness = 120f;
    public float damping = 18f;
    public float maxOffset = 0.025f;

    [Header("Rotation")]
    public float rotationFactor = 200f;     // Offset → degrees
    public float maxRotation = 4f;          // Clamp motion
    public float rotationOffset = 0f;       // <-- DEFAULT rotation (ex: 14 for right ear)

    float offset;
    float velocity;
    float lastX;

    Vector3 baseLocalPos;
    float baseLocalZ;                       // original sprite rotation

    void Start()
    {
        baseLocalPos = transform.localPosition;
        baseLocalZ = transform.localEulerAngles.z;
        if (baseLocalZ > 180f) baseLocalZ -= 360f;

        lastX = driver.position.x;
    }

    void LateUpdate()
    {
        if (!driver) return;

        // Driver movement
        float deltaX = driver.position.x - lastX;
        float target = Mathf.Clamp(-deltaX * strength, -maxOffset, maxOffset);

        // Spring physics
        float force = (target - offset) * stiffness;
        velocity += force * Time.deltaTime;
        velocity *= Mathf.Exp(-damping * Time.deltaTime);
        offset += velocity * Time.deltaTime;

        // Position
        transform.localPosition = baseLocalPos + new Vector3(offset, 0f, 0f);

        // Rotation (relative to base + offset)
        float springRot = Mathf.Clamp(-offset * rotationFactor, -maxRotation, maxRotation);
        float finalRot = baseLocalZ + rotationOffset + springRot;

        transform.localRotation = Quaternion.Euler(0f, 0f, finalRot);

        lastX = driver.position.x;
    }
}
