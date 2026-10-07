using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class PlayerMovement : MonoBehaviour
{

    public CharacterController controller;
    public float speed = 8f;
    public float gravity = -9.81f;
    public float jumpHeight = 3f;
    public Transform groundCheck;
    public float groundDistance = 0.4f;
    public LayerMask groundMask;
    Vector3 velocity;
    bool isGrounded;

    
     public AudioClip footStepSound;
     public float footStepDelay;
 
     private float nextFootstep = 0;

    // Update is called once per frame
    void Update()
    {
        // The lab floor prefabs use Unity layer 9. Include it even when the
        // ground mask was left unassigned in the inspector.
        int floorLayers = groundMask.value | 1 | (1 << 9);
        isGrounded = controller.isGrounded || Physics.CheckSphere(groundCheck.position, groundDistance, floorLayers, QueryTriggerInteraction.Ignore);

        if (isGrounded && velocity.y <0)
            {
            velocity.y = -2f;
            }

        float x = Input.GetAxis("Horizontal");
        float z = Input.GetAxis("Vertical");

        Vector3 motion = transform.right * x + transform.forward * z;
        controller.Move(motion * speed * Time.deltaTime);

        if(Input.GetButtonDown("Jump") && isGrounded)
        {
            velocity.y = Mathf.Sqrt(jumpHeight * -2f * gravity);
        }

        velocity.y += gravity * Time.deltaTime;
        controller.Move(velocity * Time.deltaTime);

         if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.W) && isGrounded)
            {
             nextFootstep -= Time.deltaTime;
             if (nextFootstep <= 0) 
                {
                 GetComponent<AudioSource>().PlayOneShot(footStepSound, 0.7f);
                 nextFootstep += footStepDelay;
                }
             }
         }
}


