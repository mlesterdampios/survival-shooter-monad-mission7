using System.Collections;
using System.Collections.Generic;
using Unity.IO.LowLevel.Unsafe;
using UnityEngine;
using Terresquall;

public class PlayerMovement : MonoBehaviour
{
    public float speed = 6f;

    [Header("Tuning")]
    public float turnSpeedDegPerSec = 540f;   // how fast to turn, in deg/s
    public float deadZone = 0.15f;            // ignore tiny thumb jitters

    Vector3 movement;
    Animator anim;
    Rigidbody playerRigidbody;
    int floorMask;
    float camRayLength = 100f;

    void Awake()
    {
        floorMask = LayerMask.GetMask("Floor");
        anim = GetComponent<Animator>();
        if (anim == null)
            anim = GetComponentInChildren<Animator>();
        playerRigidbody = GetComponent<Rigidbody>();
    }

    void FixedUpdate()
    {
        float h = GameSettingsManager.isVirtualJoystickEnabled ? VirtualJoystick.GetAxisRaw("Horizontal", 0) : Input.GetAxisRaw("Horizontal");
        float v = GameSettingsManager.isVirtualJoystickEnabled ? VirtualJoystick.GetAxisRaw("Vertical", 0) : Input.GetAxisRaw("Vertical");

        Move(h, v);
        Turning();
        Animating(h, v);
    }

    void Move (float h, float v)
    {
        movement.Set(h, 0f, v);
        movement = movement.normalized * speed * Time.deltaTime;
        playerRigidbody.MovePosition(transform.position + movement);
    }

    void Turning()
    {
        if (!GameSettingsManager.isVirtualJoystickEnabled)
        {
            Ray camRay = Camera.main.ScreenPointToRay(Input.mousePosition);
            RaycastHit floorHit;

            if (Physics.Raycast(camRay, out floorHit, camRayLength, floorMask))
            {
                Vector3 playerToMouse = floorHit.point - transform.position;
                playerToMouse.y = 0f;

                Quaternion newRotation = Quaternion.LookRotation(playerToMouse);
                playerRigidbody.MoveRotation(newRotation);
            }
        }
        else
        {
            float h = VirtualJoystick.GetAxisRaw("Horizontal", 1);
            float v = VirtualJoystick.GetAxisRaw("Vertical", 1);

            Vector3 input = new Vector3(h, 0f, v);

            // Ignore tiny input so we don't wiggle
            if (input.sqrMagnitude < deadZone * deadZone) return;

            // Make direction unit-length so input magnitude doesn't change turn rate
            input.Normalize();

            // Where we want to face
            float targetY = Mathf.Atan2(input.x, input.z) * Mathf.Rad2Deg;

            // Current yaw
            float currentY = transform.eulerAngles.y;

            // Turn at a constant speed (deg/sec), with wrap-around handled
            float newY = Mathf.MoveTowardsAngle(currentY, targetY, turnSpeedDegPerSec * Time.deltaTime);

            // Apply
            transform.rotation = Quaternion.Euler(0f, newY, 0f);
        }
    }

    void Animating(float h, float v)
    {
        bool walking = h != 0f || v != 0f;
        anim.SetBool("IsWalking", walking);
    }
}
