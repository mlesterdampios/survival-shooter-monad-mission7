using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class CameraFollow : MonoBehaviour
{
    public float smoothing = 5f;

    Vector3 offset;

    void Start()
    {
        offset = transform.position - PlayerSpawner.PlayerTransform.position;
    }

    void FixedUpdate()
    {
        Vector3 targetCamPos = PlayerSpawner.PlayerTransform.position + offset;
        transform.position = Vector3.Lerp(transform.position, targetCamPos, smoothing * Time.deltaTime);
    }
}
