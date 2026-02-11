using System.Collections;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;

public class FadeMaterial : MonoBehaviour
{
    [Header("3D Objects to Fade")]
    public GameObject Environment;         // existing
    public GameObject Table;               // new

    [Header("Canvas Parent to Fade Images Under")]
    public GameObject CanvasParent;

    [Header("Fade Duration (s)")]
    public float fadeSpeed = 1f;

    // cached
    Renderer _envR, _tableR;
    float    _envStartA, _tableStartA;

    Image[] _ui;
    float[] _uiStartA;
    Coroutine _co;

    void Awake()
    {
        CacheRenderer(Environment, ref _envR,   ref _envStartA, tintBlack:false);
        CacheRenderer(Table,       ref _tableR, ref _tableStartA, tintBlack:true);

        if (CanvasParent)
        {
            _ui = CanvasParent.GetComponentsInChildren<Image>(true);
            _uiStartA = new float[_ui.Length];
            for (int i = 0; i < _ui.Length; i++)
                _uiStartA[i] = _ui[i].color.a;
        }
    }

    /* ───────── API ───────── */
    public void FadeAll(bool fadeOut)
    {
        if (_co != null) StopCoroutine(_co);
        _co = StartCoroutine(FadeCo(fadeOut));
    }
    public void FadeSkybox(bool visible) => FadeAll(visible);

    /* ───────── INTERNALS ───────── */
    void CacheRenderer(GameObject go, ref Renderer rend,
                       ref float startA, bool tintBlack)
    {
        if (!go) return;
        rend = go.GetComponent<Renderer>();
        if (!rend) return;

        var mat = rend.material;
        if (mat.HasProperty("_Alpha"))            // custom transparent shader
        {
            startA = mat.GetFloat("_Alpha");
        }
        else                                      // URP/Lit or other opaque
        {
            // clone & convert to URP/Lit Transparent
            var newMat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            newMat.CopyPropertiesFromMaterial(mat);
            newMat.SetFloat("_Surface", 1f);      // 1 = Transparent
            newMat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            Color c = tintBlack ? Color.black : newMat.GetColor("_BaseColor");
            c.a = tintBlack ? 0.88f : 1f;
            newMat.SetColor("_BaseColor", c);
            rend.material = newMat;
            startA = c.a;
        }
    }

    IEnumerator FadeCo(bool fadeOut)
    {
        float envTgt   = fadeOut ? 0f : _envStartA;
        float tableTgt = fadeOut ? 0f : _tableStartA;

        var uiTgt = new float[_ui?.Length ?? 0];
        for (int i = 0; i < uiTgt.Length; i++)
            uiTgt[i] = fadeOut ? 0f : _uiStartA[i];

        bool busy;
        do
        {
            busy  = LerpRenderer(_envR,   envTgt);
            busy |= LerpRenderer(_tableR, tableTgt);

            for (int i = 0; i < uiTgt.Length; i++)
                busy |= LerpImage(_ui[i], uiTgt[i]);

            if (busy) yield return null;
        }
        while (busy);
    }

    bool LerpRenderer(Renderer r, float tgtA)
    {
        if (!r) return false;
        var m = r.material;
        float cur = m.HasProperty("_Alpha")
            ? m.GetFloat("_Alpha")
            : m.GetColor("_BaseColor").a;

        float next = Mathf.MoveTowards(cur, tgtA, Time.deltaTime / fadeSpeed);
        if (Mathf.Approximately(cur, next)) return false;

        if (m.HasProperty("_Alpha"))
            m.SetFloat("_Alpha", next);
        else
        {
            var c = m.GetColor("_BaseColor");
            c.a = next;
            m.SetColor("_BaseColor", c);
        }
        return true;
    }

    bool LerpImage(Image img, float tgtA)
    {
        if (!img) return false;
        var c = img.color;
        float next = Mathf.MoveTowards(c.a, tgtA, Time.deltaTime / fadeSpeed);
        if (Mathf.Approximately(c.a, next)) return false;
        c.a = next;
        img.color = c;
        return true;
    }
}
