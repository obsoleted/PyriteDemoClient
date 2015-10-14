using UnityEngine;
using System.Collections;

public class ShaderFlip : MonoBehaviour
{

    public Shader ReplacementShader;
    public Shader OriginalShader;
    public string ReplacementTag;

    private bool _flipped = false;

    // Use this for initialization
    public void Flip()
    {   if (!_flipped)
        {
            Camera.main.SetReplacementShader(ReplacementShader, ReplacementTag);
        } else
        {
            Camera.main.ResetReplacementShader();
        }
        _flipped = !_flipped;
    }
}
