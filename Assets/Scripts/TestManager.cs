using System;
using System.Collections.Generic;
using UnityEngine;
using TMPro;

public enum SensoryMode
{
    AudioOnly,
    VisualOnly,
    AudioVisual
}

[Serializable]
public struct TrialCondition
{
    public SensoryMode mode;
    public SurfaceType surface;
    // A–L anonymized code used during the trial (same for all participants)
    public string code;
}

/// <summary>
/// Manages trial sequences for participants with balanced conditions and sensory mode gating.
/// Supports debug mode for quick testing and normal mode with Latin-square ordering.
/// </summary>
[DefaultExecutionOrder(12000)]
public class TestManager : MonoBehaviour
{
    [Header("Debug Mode")]
    public bool debugMode = false;  // Simple AV-V-A sequence with all surfaces in order

    [Header("Participants / Trials")]
    public int participantCount = 12;
    public int trialsPerParticipant = 12;  // Fixed at 12 conditions (3 modes × 4 surfaces)
    public int randomSeed = 12345;

    [Header("Target Test Object")]
    public SurfaceData testSurface;

    [Header("Hand Systems (Two Hands)")]
    // VISUAL – texture (rough/smooth)
    public HandTextureDriver leftHandTextureDriver;
    public HandTextureDriver rightHandTextureDriver;

    // VISUAL – thermal overlay
    public HandThermalDriver leftHandThermalDriver;
    public HandThermalDriver rightHandThermalDriver;

    // AUDIO – texture (rough/smooth)
    public HandTextureAudio leftHandTextureAudio;
    public HandTextureAudio rightHandTextureAudio;

    // AUDIO – thermal (hot/cold)
    public HandThermalAudio leftHandThermalAudio;
    public HandThermalAudio rightHandThermalAudio;

    [Header("UI")]
    public TextMeshProUGUI label;  // Shows current condition, modality, and effect

    [Header("Debug (Inspector only)")]
    [TextArea(15, 30)]
    [SerializeField]
    private string debugTrialPlan = "";

    [TextArea(5, 10)]
    [SerializeField]
    private string conditionCodeLegend = "";

    // Static design constants
    private static readonly SurfaceType[] s_surfaces =
    {
        SurfaceType.Hot,
        SurfaceType.Rough,
        SurfaceType.Cold,
        SurfaceType.Smooth
    };

    // Latin square: different sensory mode order for each participant group
    private static readonly SensoryMode[][] s_sensoryLatinSquare =
    {
        new[] { SensoryMode.AudioOnly,  SensoryMode.VisualOnly,  SensoryMode.AudioVisual },
        new[] { SensoryMode.VisualOnly, SensoryMode.AudioVisual, SensoryMode.AudioOnly  },
        new[] { SensoryMode.AudioVisual,SensoryMode.AudioOnly,   SensoryMode.VisualOnly }
    };

    private struct ConditionKey : IEquatable<ConditionKey>
    {
        public SensoryMode mode;
        public SurfaceType surface;

        public ConditionKey(SensoryMode m, SurfaceType s)
        {
            mode = m;
            surface = s;
        }

        public bool Equals(ConditionKey other)
        {
            return mode == other.mode && surface == other.surface;
        }

        public override bool Equals(object obj)
        {
            return obj is ConditionKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + (int)mode;
                hash = hash * 31 + (int)surface;
                return hash;
            }
        }
    }

    // Runtime state
    // _trialsByParticipant[participant][trial]
    private TrialCondition[][] _trialsByParticipant;

    private Dictionary<ConditionKey, char> _codeByCondition;      // Maps conditions to codes A–L
    private Dictionary<char, ConditionKey> _conditionByCode;      // Maps codes A–L to conditions

    private int  _participantIndex;       // 0..participantCount-1
    private int  _trialIndex;             // 0..trialsPerParticipant-1
    private bool _showingTransitionScreen;

    void Awake()
    {
        if (!testSurface)
            Debug.LogError("[TestManager] Test SurfaceData is not assigned.");

        if (participantCount <= 0) participantCount = 1;
        trialsPerParticipant = 12; // fixed design

        EnsureDesignGenerated();
        ApplyCurrentTrialState();
    }

    void OnValidate()
    {
        if (participantCount <= 0) participantCount = 1;
        trialsPerParticipant = 12;

        // In editor: keep debug info / mapping visible
        EnsureDesignGenerated();
    }

    // Design generation
    void EnsureDesignGenerated()
    {
        if (_codeByCondition == null || _codeByCondition.Count == 0 ||
            _conditionByCode == null || _conditionByCode.Count == 0)
        {
            GenerateConditionCodeMapping();
        }

        if (_trialsByParticipant == null || _trialsByParticipant.Length != participantCount)
        {
            GenerateAllTrialSequences();
        }

        // Clamp runtime indices to safe ranges
        _participantIndex        = Mathf.Clamp(_participantIndex, 0, Mathf.Max(0, participantCount - 1));
        _trialIndex              = Mathf.Clamp(_trialIndex,       0, trialsPerParticipant - 1);
        _showingTransitionScreen = false;

        UpdateDebugTrialPlan();
        UpdateConditionCodeLegendText();
    }

    void GenerateConditionCodeMapping()
    {
        _codeByCondition = new Dictionary<ConditionKey, char>(12);
        _conditionByCode = new Dictionary<char, ConditionKey>(12);

        // Build all 12 unique condition combinations
        var allConditions = new List<ConditionKey>(12);
        foreach (var mode in new[] { SensoryMode.AudioOnly, SensoryMode.VisualOnly, SensoryMode.AudioVisual })
        {
            foreach (var surface in s_surfaces)
            {
                allConditions.Add(new ConditionKey(mode, surface));
            }
        }

        // Assign codes A–L to shuffled conditions
        var rng = new System.Random(randomSeed);
        Shuffle(allConditions, rng);

        for (int i = 0; i < allConditions.Count; i++)
        {
            char letter = (char)('A' + i); // A..L
            var key = allConditions[i];

            _codeByCondition[key] = letter;
            _conditionByCode[letter] = key;
        }
    }

    void GenerateAllTrialSequences()
    {
        _trialsByParticipant = new TrialCondition[participantCount][];

        if (debugMode)
        {
            // Debug mode: single participant with simple sequence
            _trialsByParticipant[0] = GenerateDebugTrialSequence();
            return;
        }

        var rng = new System.Random(randomSeed);

        for (int p = 0; p < participantCount; p++)
        {
            _trialsByParticipant[p] = GenerateTrialsForParticipant(p, rng);
        }
    }

    TrialCondition[] GenerateTrialsForParticipant(int participantIndex, System.Random rng)
    {
        var trials = new TrialCondition[trialsPerParticipant];
        int writeIndex = 0;

        // Select Latin square row for this participant
        int row = participantIndex % s_sensoryLatinSquare.Length;
        SensoryMode[] groupModes = s_sensoryLatinSquare[row];

        for (int g = 0; g < groupModes.Length; g++)
        {
            SensoryMode mode = groupModes[g];

            // Shuffle surfaces so each modality gets all 4 in random order
            SurfaceType[] surfaces = (SurfaceType[])s_surfaces.Clone();
            Shuffle(surfaces, rng);

            for (int i = 0; i < surfaces.Length; i++)
            {
                var key = new ConditionKey(mode, surfaces[i]);
                if (!_codeByCondition.TryGetValue(key, out char letter))
                {
                    Debug.LogError($"[TestManager] Missing code for {mode}/{surfaces[i]}.");
                    letter = '?';
                }

                trials[writeIndex++] = new TrialCondition
                {
                    mode    = mode,
                    surface = surfaces[i],
                    code    = letter.ToString()
                };
            }
        }

        return trials;
    }

    TrialCondition[] GenerateDebugTrialSequence()
    {
        var trials = new TrialCondition[trialsPerParticipant];
        int writeIndex = 0;

        // Simple sequence: AV-V-AV through all surfaces in order
        SensoryMode[] debugModes = new[] { SensoryMode.AudioVisual, SensoryMode.VisualOnly, SensoryMode.AudioOnly };

        foreach (var mode in debugModes)
        {
            foreach (var surface in s_surfaces)
            {
                var key = new ConditionKey(mode, surface);
                if (!_codeByCondition.TryGetValue(key, out char letter))
                {
                    letter = '?';
                }

                trials[writeIndex++] = new TrialCondition
                {
                    mode    = mode,
                    surface = surface,
                    code    = letter.ToString()
                };
            }
        }

        return trials;
    }

    static void Shuffle<T>(IList<T> list, System.Random rng)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    // Helpers
    string BuildShortConditionCode(SensoryMode mode, SurfaceType surface)
    {
        string modeShort = mode switch
        {
            SensoryMode.AudioOnly    => "A",
            SensoryMode.VisualOnly   => "V",
            SensoryMode.AudioVisual  => "AV",
            _                        => "?"
        };

        string surfaceShort = surface switch
        {
            SurfaceType.Hot    => "H",
            SurfaceType.Cold   => "C",
            SurfaceType.Rough  => "R",
            SurfaceType.Smooth => "S",
            _                  => "N"
        };

        return $"{modeShort}-{surfaceShort}";
    }

    // Inspector debug display
    void UpdateConditionCodeLegendText()
    {
        if (debugMode || _conditionByCode == null || _conditionByCode.Count == 0)
        {
            conditionCodeLegend = "";
            return;
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== CONDITION CODE LEGEND (A–L) ===");

        for (char c = 'A'; c <= 'L'; c++)
        {
            if (_conditionByCode.TryGetValue(c, out ConditionKey key))
            {
                string shortCode = BuildShortConditionCode(key.mode, key.surface);
                sb.AppendLine($"{c}: {shortCode}");
            }
        }

        conditionCodeLegend = sb.ToString();
    }

    void UpdateDebugTrialPlan()
    {
        if (_trialsByParticipant == null)
        {
            debugTrialPlan = "";
            return;
        }

        var sb = new System.Text.StringBuilder();

        for (int p = 0; p < _trialsByParticipant.Length; p++)
        {
            TrialCondition[] trials = _trialsByParticipant[p];
            if (trials == null) continue;  // Skip unpopulated slots in debug mode

            sb.AppendLine($"=== PARTICIPANT {p + 1} ===");

            for (int t = 0; t < trials.Length; t++)
            {
                TrialCondition cond = trials[t];

                var shortCode = BuildShortConditionCode(cond.mode, cond.surface);
                sb.AppendLine($"  Trial {t + 1}: [{cond.code}] {shortCode}");
            }

            sb.AppendLine();
        }

        debugTrialPlan = sb.ToString();
    }

    // Input handlers (XRI buttons)
    public void OnNextPressed()  => StepForward();
    public void OnPrevPressed()  => StepBackward();

    // Navigation
    void StepForward()
    {
        // Transition screen: jump to next participant's first trial
        if (_showingTransitionScreen)
        {
            if (_participantIndex < participantCount - 1)
            {
                _participantIndex++;
                _trialIndex = 0;
                _showingTransitionScreen = false;
                ApplyCurrentTrialState();
            }
            else
            {
                // Last participant: stay on end screen
                ApplyFinalEndScreen();
            }
            return;
        }

        // Inside trials
        if (_trialIndex < trialsPerParticipant - 1)
        {
            _trialIndex++;
            ApplyCurrentTrialState();
        }
        else
        {
            // At last trial: debug mode stays, normal mode shows transition
            if (debugMode)
            {
                // Debug: stay at last condition
                return;
            }

            if (_participantIndex < participantCount - 1)
            {
                _showingTransitionScreen = true;
                ApplyTransitionScreen();
            }
            else
            {
                // End of all trials
                _showingTransitionScreen = true;
                ApplyFinalEndScreen();
            }
        }
    }

    void StepBackward()
    {
        // Transition screen: go back to last trial
        if (_showingTransitionScreen)
        {
            _showingTransitionScreen = false;
            _trialIndex = trialsPerParticipant - 1;
            ApplyCurrentTrialState();
            return;
        }

        // Inside trials
        if (_trialIndex > 0)
        {
            _trialIndex--;
            ApplyCurrentTrialState();
        }
        else
        {
            // At first trial: go to previous participant's end
            if (_participantIndex > 0)
            {
                // Move to previous participant's transition
                _participantIndex--;
                _trialIndex = trialsPerParticipant - 1;
                _showingTransitionScreen = true;
                ApplyTransitionScreen();
            }
            else
            {
                // Already at start
            }
        }
    }

    // State management
    void ApplyCurrentTrialState()
    {
        if (_showingTransitionScreen)
        {
            if (_participantIndex < participantCount - 1)
                ApplyTransitionScreen();
            else
                ApplyFinalEndScreen();
            return;
        }

        if (_trialsByParticipant == null ||
            _trialsByParticipant.Length == 0 ||
            _participantIndex < 0 ||
            _participantIndex >= _trialsByParticipant.Length)
        {
            Debug.LogError("[TestManager] Trial sequences not generated or participant index out of range.");
            return;
        }

        TrialCondition[] trials = _trialsByParticipant[_participantIndex];
        if (_trialIndex < 0 || _trialIndex >= trials.Length)
        {
            Debug.LogError("[TestManager] Trial index out of range.");
            return;
        }

        TrialCondition cond = trials[_trialIndex];

        // Set surface and apply sensory mode to both hands
        if (testSurface)
            testSurface.surfaceType = cond.surface;
        ApplySensoryMode(cond.mode);

        // Update UI label
        if (label)
        {
            string modeText = cond.mode switch
            {
                SensoryMode.AudioOnly    => "Audio Only",
                SensoryMode.VisualOnly   => "Visual Only",
                SensoryMode.AudioVisual  => "Audio-Visual",
                _                        => "Unknown"
            };

            string surfaceText = cond.surface switch
            {
                SurfaceType.Hot    => "Hot",
                SurfaceType.Cold   => "Cold",
                SurfaceType.Rough  => "Rough",
                SurfaceType.Smooth => "Smooth",
                _                  => "Neutral"
            };

            int participantDisplay = _participantIndex + 1;
            int trialDisplay       = _trialIndex + 1;

            if (debugMode)
            {
                label.text =
                    $"Condition: {trialDisplay} / {trialsPerParticipant}\n" +
                    $"Modality: {modeText} \n Effect: {surfaceText}";
            }
            else
            {
                label.text =
                    $"Participant: {participantDisplay}\n" +
                    $"Condition: {trialDisplay} / {trialsPerParticipant}\n" +
                    $"Code: {cond.code}";
            }
        }
    }

    void ApplyTransitionScreen()
    {
        if (testSurface)
            testSurface.surfaceType = SurfaceType.Neutral;
        DisableAllCues();

        int currentP = _participantIndex + 1;
        int nextP    = Mathf.Min(participantCount, _participantIndex + 2);

        if (label)
        {
            if (_participantIndex < participantCount - 1)
            {
                label.text = $"Participant {currentP} complete\nNext: Participant {nextP}";
            }
            else
            {
                label.text = $"Participant {currentP} complete";
            }
        }
    }

    void ApplyFinalEndScreen()
    {
        if (testSurface)
            testSurface.surfaceType = SurfaceType.Neutral;
        DisableAllCues();
        if (label)
            label.text = $"All trials complete";
    }

    // Sensory mode gating (both hands)
    void ApplySensoryMode(SensoryMode mode)
    {
        bool audioOn =
            mode == SensoryMode.AudioOnly || mode == SensoryMode.AudioVisual;
        bool visualOn =
            mode == SensoryMode.VisualOnly || mode == SensoryMode.AudioVisual;

        // Audio cues (texture and thermal)
        if (leftHandTextureAudio)  leftHandTextureAudio.enabled = audioOn;
        if (rightHandTextureAudio) rightHandTextureAudio.enabled = audioOn;
        if (leftHandThermalAudio)  leftHandThermalAudio.enabled = audioOn;
        if (rightHandThermalAudio) rightHandThermalAudio.enabled = audioOn;

        // Visual feedback (texture motion and thermal)
        if (leftHandThermalDriver)  leftHandThermalDriver.enabled = visualOn;
        if (rightHandThermalDriver) rightHandThermalDriver.enabled = visualOn;
        if (leftHandTextureDriver)  leftHandTextureDriver.useMotionEffects = visualOn;
        if (rightHandTextureDriver) rightHandTextureDriver.useMotionEffects = visualOn;
    }

    void DisableAllCues()
    {
        if (leftHandTextureAudio)  leftHandTextureAudio.enabled = false;
        if (rightHandTextureAudio) rightHandTextureAudio.enabled = false;
        if (leftHandThermalAudio)  leftHandThermalAudio.enabled = false;
        if (rightHandThermalAudio) rightHandThermalAudio.enabled = false;
        if (leftHandThermalDriver)  leftHandThermalDriver.enabled = false;
        if (rightHandThermalDriver) rightHandThermalDriver.enabled = false;
        if (leftHandTextureDriver)  leftHandTextureDriver.useMotionEffects = false;
        if (rightHandTextureDriver) rightHandTextureDriver.useMotionEffects = false;
    }
}
