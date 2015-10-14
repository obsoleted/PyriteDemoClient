using UnityEngine;
using System.Collections;

public class TagAlong : MonoBehaviour
{
    public Camera CameraToTagAlongWith;
    public float DistanceAway = 10f;

    // Use this for initialization
    void Start () {
    
    }
    
    // Update is called once per frame
    void Update ()
    {
        transform.position = CameraToTagAlongWith.transform.position;
        transform.position += CameraToTagAlongWith.transform.forward * 569.5f;
        transform.LookAt(transform.position + CameraToTagAlongWith.transform.rotation * Vector3.forward,
            CameraToTagAlongWith.transform.rotation * Vector3.up);
    }
}
