using UnityEngine;
using System.Collections;

public class CameraFix : MonoBehaviour {

    public float TranslationDeltaRate = 50.0f;
    public float RotationDeltaRate = 30.0f; 
    public bool InvertY = false;
    private Camera CameraObj;
    private GameObject targetPosition;

    void Start()
    {
        CameraObj = Camera.main;
        targetPosition = new GameObject("targetPosition");
    }

    private void Update()
    {
        //float keyboardYaw = Input.GetAxis("HorizontalTurn") *  RotationDeltaRate;
        //float keyboardPitch = -Input.GetAxis("VerticalTurn") * RotationDeltaRate * (InvertY ? -1f : 1f);
        
        //Quaternion newRotation = Quaternion.Euler(transform.rotation.eulerAngles.x + LimitAngles(keyboardPitch), transform.rotation.eulerAngles.y + LimitAngles(keyboardYaw), 0.0f);
        //transform.rotation = Quaternion.Slerp(transform.rotation, newRotation, Time.deltaTime * 2.0f);

        //Vector3 move = Input.GetAxis("Horizontal") * transform.right + Input.GetAxis("Forward") * transform.forward +
        //    Input.GetAxis("Vertical") * transform.up;
        //targetPosition.transform.rotation = CameraObj.transform.rotation;
        //transform.Translate(move * Time.deltaTime * TranslationDeltaRate, Space.World);
        ////transform.position = targetPosition.transform.position;


        Vector3 move = Input.GetAxis("Horizontal") * Vector3.right + Input.GetAxis("Forward") * Vector3.forward +
        Input.GetAxis("Vertical") * Vector3.up;
        targetPosition.transform.rotation = CameraObj.transform.rotation;
        targetPosition.transform.Translate(move * Time.deltaTime * TranslationDeltaRate, Space.Self);
        transform.position = targetPosition.transform.position;
    }

    private static float LimitAngles(float angle)
    {
        var result = angle;

        while (result > 360)
            result -= 360;

        while (result < 0)
            result += 360;

        return result;
    }
}
