using UnityEngine;
using TMPro;

/// <summary>
/// Displays the total accumulated score and fading +/- delta indicators.
/// Subscribes to "/step_score" (Float32Message sent from Python each step).
/// Always active — not gated by human control mode.
/// </summary>
public class ScoreVisualizer : MonoBehaviour
{
    [Header("UI References")]
    public TextMeshProUGUI totalScoreText;
    public TextMeshProUGUI positiveDeltaText;
    public TextMeshProUGUI negativeDeltaText;

    [Header("Settings")]
    public string stepScoreTopic = "/step_score";
    public float fadeDuration = 2.0f;
    public float bumpScale = 1.5f;

    [Header("Colors")]
    public Color positiveColor = new Color(0.2f, 1.0f, 0.2f, 1f);
    public Color negativeColor = new Color(1.0f, 0.2f, 0.2f, 1f);

    private float totalScore = 0f;
    private float positiveFadeTimer = 0f;
    private float negativeFadeTimer = 0f;

    void Start()
    {
        RoslikeTCPServer.GetInstance().Subscribe<Float32Message>(stepScoreTopic, OnStepScore);
        RoslikeTCPServer.GetInstance().Subscribe<BoolMessage>("/sim_control/reset_episode", OnReset);

        UpdateDisplay();

        if (positiveDeltaText != null) positiveDeltaText.alpha = 0f;
        if (negativeDeltaText != null) negativeDeltaText.alpha = 0f;
    }

    void OnReset(BoolMessage msg)
    {
        totalScore = 0f;
        positiveFadeTimer = 0f;
        negativeFadeTimer = 0f;
        UpdateDisplay();
        if (positiveDeltaText != null) positiveDeltaText.alpha = 0f;
        if (negativeDeltaText != null) negativeDeltaText.alpha = 0f;
    }

    void OnStepScore(Float32Message msg)
    {
        float delta = msg.data;
        if (Mathf.Approximately(delta, 0f)) return;

        totalScore += delta;

        if (delta > 0f)
        {
            positiveFadeTimer = fadeDuration;
            if (positiveDeltaText != null)
            {
                positiveDeltaText.text = $"+{delta:F2}";
                positiveDeltaText.color = positiveColor;
                positiveDeltaText.alpha = 1f;
            }
        }
        else
        {
            negativeFadeTimer = fadeDuration;
            if (negativeDeltaText != null)
            {
                negativeDeltaText.text = $"{delta:F2}";
                negativeDeltaText.color = negativeColor;
                negativeDeltaText.alpha = 1f;
            }
        }

        UpdateDisplay();
    }

    void Update()
    {
        if (positiveFadeTimer > 0f)
        {
            positiveFadeTimer -= Time.deltaTime;
            float t = Mathf.Clamp01(positiveFadeTimer / fadeDuration);
            if (positiveDeltaText != null)
            {
                positiveDeltaText.alpha = t;
                float scale = Mathf.Lerp(1f, bumpScale, t * t);
                positiveDeltaText.transform.localScale = Vector3.one * scale;
            }
        }

        if (negativeFadeTimer > 0f)
        {
            negativeFadeTimer -= Time.deltaTime;
            float t = Mathf.Clamp01(negativeFadeTimer / fadeDuration);
            if (negativeDeltaText != null)
            {
                negativeDeltaText.alpha = t;
                float scale = Mathf.Lerp(1f, bumpScale, t * t);
                negativeDeltaText.transform.localScale = Vector3.one * scale;
            }
        }
    }

    void UpdateDisplay()
    {
        if (totalScoreText != null)
            totalScoreText.text = $"Score: {totalScore:F2}";
    }
}
