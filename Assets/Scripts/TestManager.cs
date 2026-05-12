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

public enum DebugAVCouplingMode
{
    Baseline,
    Congruent,
    Incongruent
}

[Serializable]
public struct TrialCondition
{
    public SensoryMode mode;
    public SurfaceType surface;
    // A–L + M anonymized codes used during the trial (same for all participants)
    public string code;
}

/// <summary>
/// Manages trial sequences for participants with balanced conditions and sensory mode gating.
/// Supports debug mode for quick testing and normal mode with Latin-square ordering.
/// 13 conditions: 12 factorial (3×4) coded A–L plus one neutral baseline coded M.
/// </summary>
[DefaultExecutionOrder(12000)]
public class TestManager : MonoBehaviour
{
    [Header("Debug Mode")]
    public bool debugMode = false;  // Simple AV-V-A sequence with all surfaces in order

    [SerializeField]
    DebugAVCouplingMode debugAVCouplingMode = DebugAVCouplingMode.Congruent;

    [Header("Participants / Trials")]
    public int participantCount = 30;
    public int trialsPerParticipant = 13;  // Fixed: 12 (3×4) + 1 neutral baseline (M)
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

    [Header("Participant Button (Normal Mode)")]
    [Tooltip("Max gap (seconds) between taps to count as a double press.")]
    public float participantDoublePressWindowSec = 0.35f;
    [Tooltip("Hold duration (seconds) to reset to Participant 1.")]
    public float participantHoldResetSec = 0.8f;

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

    private Dictionary<ConditionKey, char> _codeByCondition;      // Maps conditions to codes A–L + M
    private Dictionary<char, ConditionKey> _conditionByCode;      // Maps codes A–L + M to conditions

    /// <summary>13th condition: neutral surface, both modalities on (baseline).</summary>
    static readonly ConditionKey NeutralBaselineKey =
        new ConditionKey(SensoryMode.AudioVisual, SurfaceType.Neutral);
    const char NeutralBaselineCode = 'M';
    const int TotalConditionCodes = 13;

    private int  _participantIndex;       // 0..participantCount-1
    private int  _trialIndex;             // 0..trialsPerParticipant-1
    private bool _showingTransitionScreen;

    // Debug-only telemetry routing: publish remapped snapshots for audio consumers,
    // while visuals continue to use the original global channels.
    HandTelemetryChannel _leftAudioTelemetry;
    HandTelemetryChannel _rightAudioTelemetry;
    bool _debugAudioTelemetryRouted;

    bool _participantButtonIsDown;
    bool _participantButtonHoldHandled;
    float _participantButtonDownTime;
    float _participantLastTapReleaseTime;
    int _participantTapCount;

    void Awake()
    {
        if (!testSurface)
            Debug.LogError("[TestManager] Test SurfaceData is not assigned.");

        if (participantCount <= 0) participantCount = 1;
        trialsPerParticipant = TotalConditionCodes; // fixed design

        EnsureDesignGenerated();
        ApplyCurrentTrialState();
    }

    void OnValidate()
    {
        if (participantCount <= 0) participantCount = 1;
        trialsPerParticipant = TotalConditionCodes;

        // In editor: keep debug info / mapping visible
        EnsureDesignGenerated();
    }

    void Update()
    {
        ProcessParticipantButtonInput();

        if (debugMode)
        {
            // Publish remapped telemetry early in the frame so audio consumers (LateUpdate) read it.
            PublishDebugAudioTelemetry();
        }
    }

    void ProcessParticipantButtonInput()
    {
        if (debugMode)
            return;

        float now = Time.time;

        if (_participantButtonIsDown &&
            !_participantButtonHoldHandled &&
            now - _participantButtonDownTime >= participantHoldResetSec)
        {
            _participantButtonHoldHandled = true;
            _participantTapCount = 0;
            JumpToParticipant(0);
        }

        if (_participantTapCount == 1 &&
            now - _participantLastTapReleaseTime >= participantDoublePressWindowSec)
        {
            _participantTapCount = 0;
            AdvanceParticipant(1);
        }
    }

    // Design generation
    void EnsureDesignGenerated()
    {
        if (_codeByCondition == null || _conditionByCode == null ||
            _codeByCondition.Count != TotalConditionCodes ||
            _conditionByCode.Count != TotalConditionCodes ||
            !_codeByCondition.ContainsKey(NeutralBaselineKey))
        {
            GenerateConditionCodeMapping();
        }

        if (_trialsByParticipant == null ||
            _trialsByParticipant.Length != participantCount ||
            TrialSequencesOutOfDate())
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

    bool TrialSequencesOutOfDate()
    {
        if (_trialsByParticipant == null)
            return true;
        for (int p = 0; p < _trialsByParticipant.Length; p++)
        {
            TrialCondition[] row = _trialsByParticipant[p];
            if (row == null || row.Length != trialsPerParticipant)
                return true;
        }

        return false;
    }

    void GenerateConditionCodeMapping()
    {
        _codeByCondition = new Dictionary<ConditionKey, char>(TotalConditionCodes);
        _conditionByCode = new Dictionary<char, ConditionKey>(TotalConditionCodes);

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

        // Neutral baseline: always M (not mixed into A–L shuffle).
        _codeByCondition[NeutralBaselineKey] = NeutralBaselineCode;
        _conditionByCode[NeutralBaselineCode] = NeutralBaselineKey;
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
        var trialList = new List<TrialCondition>(trialsPerParticipant);

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

                trialList.Add(new TrialCondition
                {
                    mode    = mode,
                    surface = surfaces[i],
                    code    = letter.ToString()
                });
            }
        }

        if (!_codeByCondition.TryGetValue(NeutralBaselineKey, out char mLetter))
            mLetter = NeutralBaselineCode;

        var neutralBaselineTrial = new TrialCondition
        {
            mode    = NeutralBaselineKey.mode,
            surface = NeutralBaselineKey.surface,
            code    = mLetter.ToString()
        };

        // Random position so M feels like the other shuffled conditions.
        trialList.Insert(rng.Next(0, trialList.Count + 1), neutralBaselineTrial);

        return trialList.ToArray();
    }

    TrialCondition[] GenerateDebugTrialSequence()
    {
        var trialList = new List<TrialCondition>(trialsPerParticipant);
        var rng = new System.Random(randomSeed);

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

                trialList.Add(new TrialCondition
                {
                    mode    = mode,
                    surface = surface,
                    code    = letter.ToString()
                });
            }
        }

        if (!_codeByCondition.TryGetValue(NeutralBaselineKey, out char mLetter))
            mLetter = NeutralBaselineCode;

        trialList.Insert(rng.Next(0, trialList.Count + 1), new TrialCondition
        {
            mode    = NeutralBaselineKey.mode,
            surface = NeutralBaselineKey.surface,
            code    = mLetter.ToString()
        });

        return trialList.ToArray();
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
        sb.AppendLine("=== CONDITION CODE LEGEND (A–L + M) ===");

        for (char c = 'A'; c <= 'L'; c++)
        {
            if (_conditionByCode.TryGetValue(c, out ConditionKey key))
            {
                string shortCode = BuildShortConditionCode(key.mode, key.surface);
                sb.AppendLine($"{c}: {shortCode}");
            }
        }

        if (_conditionByCode.TryGetValue(NeutralBaselineCode, out ConditionKey mKey))
        {
            string mShort = BuildShortConditionCode(mKey.mode, mKey.surface);
            sb.AppendLine($"{NeutralBaselineCode}: {mShort}");
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
    public void OnParticipantButtonReleased()
    {
        if (debugMode)
            return;

        if (!_participantButtonIsDown)
            return;

        _participantButtonIsDown = false;

        // Hold already handled reset while button was down.
        if (_participantButtonHoldHandled)
            return;

        float now = Time.time;
        if (_participantTapCount == 1 &&
            now - _participantLastTapReleaseTime < participantDoublePressWindowSec)
        {
            _participantTapCount = 0;
            AdvanceParticipant(10);
            return;
        }

        _participantTapCount = 1;
        _participantLastTapReleaseTime = now;
    }

    // Navigation
    void StepForward()
    {
        int trialCount = DebugEffectiveTrialCount();
        if (trialCount <= 0)
            return;

        _showingTransitionScreen = false;
        _trialIndex = (_trialIndex + 1) % trialCount;
        ApplyCurrentTrialState();
    }

    void StepBackward()
    {
        int trialCount = DebugEffectiveTrialCount();
        if (trialCount <= 0)
            return;

        _showingTransitionScreen = false;
        _trialIndex = (_trialIndex - 1 + trialCount) % trialCount;
        ApplyCurrentTrialState();
    }

    void AdvanceParticipant(int delta)
    {
        if (participantCount <= 0)
            return;

        int next = Mathf.Clamp(_participantIndex + delta, 0, participantCount - 1);
        if (next == _participantIndex)
            return;

        JumpToParticipant(next);
    }

    void JumpToParticipant(int participantIndex)
    {
        _participantIndex = Mathf.Clamp(participantIndex, 0, Mathf.Max(0, participantCount - 1));
        _trialIndex = 0;
        _showingTransitionScreen = false;
        ApplyCurrentTrialState();
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

        // Incongruent mode publishes remapped telemetry into transient channels. If we leave that
        // wiring active when switching to Baseline/Congruent, audio reads stale SOs and goes silent.
        if (_debugAudioTelemetryRouted &&
            (!debugMode || debugAVCouplingMode != DebugAVCouplingMode.Incongruent))
            TeardownDebugAudioTelemetryRouting();

        SurfaceType visualSurface = cond.surface;
        SurfaceType audioSurface = cond.surface;
        SensoryMode appliedMode = cond.mode;

        int trialCount = DebugEffectiveTrialCount();
        int effectiveTrialIndex = _trialIndex;
        if (debugMode && trialCount > 0)
            effectiveTrialIndex = Mathf.Clamp(_trialIndex, 0, trialCount - 1);

        if (debugMode && debugAVCouplingMode == DebugAVCouplingMode.Baseline)
        {
            visualSurface = SurfaceType.Neutral;
            audioSurface = SurfaceType.Neutral;
            if (testSurface)
                testSurface.surfaceType = visualSurface;
            DisableAllCues();
        }
        else
        {
            if (debugMode && debugAVCouplingMode == DebugAVCouplingMode.Incongruent)
            {
                // Debug incongruent: force AV, but swap ONLY audio relative to visual.
                appliedMode = SensoryMode.AudioVisual;

                // In this mode we want exactly 4 conditions:
                // Rough audio + Smooth visual
                // Smooth audio + Rough visual
                // Hot audio + Cold visual
                // Cold audio + Hot visual
                visualSurface = IncongruentVisualSurfaceByIndex(effectiveTrialIndex);
                audioSurface = SwapSurfaceWithinTextureThermal(visualSurface);
            }

            // Set surface and apply sensory mode to both hands
            if (testSurface)
                testSurface.surfaceType = visualSurface;
            ApplySensoryMode(appliedMode);

            if (debugMode && debugAVCouplingMode == DebugAVCouplingMode.Incongruent)
                EnsureDebugAudioTelemetryRouting();
        }

        // Update UI label
        if (label)
        {
            string modeText = appliedMode switch
            {
                SensoryMode.AudioOnly    => "Audio Only",
                SensoryMode.VisualOnly   => "Visual Only",
                SensoryMode.AudioVisual  => "Audio-Visual",
                _                        => "Unknown"
            };

            string visualSurfaceText = visualSurface switch
            {
                SurfaceType.Hot    => "Hot",
                SurfaceType.Cold   => "Cold",
                SurfaceType.Rough  => "Rough",
                SurfaceType.Smooth => "Smooth",
                _                  => "Neutral"
            };

            int participantDisplay = _participantIndex + 1;
            int trialDisplay       = effectiveTrialIndex + 1;

            if (debugMode)
            {
                string couplingText = debugAVCouplingMode switch
                {
                    DebugAVCouplingMode.Baseline => "Baseline",
                    DebugAVCouplingMode.Congruent => "Congruent",
                    DebugAVCouplingMode.Incongruent => "Incongruent",
                    _ => "Unknown"
                };

                if (debugAVCouplingMode == DebugAVCouplingMode.Incongruent)
                {
                    string audioSurfaceText = audioSurface switch
                    {
                        SurfaceType.Hot => "Hot",
                        SurfaceType.Cold => "Cold",
                        SurfaceType.Rough => "Rough",
                        SurfaceType.Smooth => "Smooth",
                        _ => "Neutral"
                    };

                    label.text =
                        $"Condition: {trialDisplay} / {trialCount}\n" +
                        $"Mode: {couplingText}\n" +
                        $"Audio Effect: {audioSurfaceText}\n" +
                        $"Visual Effect: {visualSurfaceText}";
                }
                else
                {
                    label.text =
                        $"Condition: {trialDisplay} / {trialCount}\n" +
                        $"Mode: {couplingText}\n" +
                        $"Modality: {modeText} \n Effect: {visualSurfaceText}";
                }
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

    int DebugEffectiveTrialCount()
    {
        if (!debugMode)
            return trialsPerParticipant;
        return debugAVCouplingMode == DebugAVCouplingMode.Incongruent ? 4 : trialsPerParticipant;
    }

    static SurfaceType IncongruentVisualSurfaceByIndex(int i)
    {
        int idx = Mathf.Abs(i) % 4;
        return idx switch
        {
            0 => SurfaceType.Smooth, // rough audio
            1 => SurfaceType.Rough,  // smooth audio
            2 => SurfaceType.Cold,   // hot audio
            3 => SurfaceType.Hot,    // cold audio
            _ => SurfaceType.Neutral
        };
    }

    static SurfaceType SwapSurfaceWithinTextureThermal(SurfaceType surface)
    {
        return surface switch
        {
            SurfaceType.Hot => SurfaceType.Cold,
            SurfaceType.Cold => SurfaceType.Hot,
            SurfaceType.Rough => SurfaceType.Smooth,
            SurfaceType.Smooth => SurfaceType.Rough,
            _ => surface
        };
    }

    [ContextMenu("Debug/Cycle AV Coupling Mode (Baseline → Congruent → Incongruent)")]
    public void CycleDebugAVCouplingMode()
    {
        if (!debugMode)
        {
            _participantButtonIsDown = true;
            _participantButtonHoldHandled = false;
            _participantButtonDownTime = Time.time;
            return;
        }

        debugAVCouplingMode = debugAVCouplingMode switch
        {
            DebugAVCouplingMode.Baseline => DebugAVCouplingMode.Congruent,
            DebugAVCouplingMode.Congruent => DebugAVCouplingMode.Incongruent,
            DebugAVCouplingMode.Incongruent => DebugAVCouplingMode.Baseline,
            _ => DebugAVCouplingMode.Congruent
        };

        ApplyCurrentTrialState();
    }

    void EnsureDebugAudioTelemetryRouting()
    {
        if (_debugAudioTelemetryRouted)
            return;

        // Create per-hand transient channels for debug audio. These are not saved as assets.
        _leftAudioTelemetry = ScriptableObject.CreateInstance<HandTelemetryChannel>();
        _rightAudioTelemetry = ScriptableObject.CreateInstance<HandTelemetryChannel>();
        _leftAudioTelemetry.name = "LeftHandTelemetry_DebugAudio";
        _rightAudioTelemetry.name = "RightHandTelemetry_DebugAudio";

        if (leftHandTextureAudio)
        {
            leftHandTextureAudio.useGlobalTelemetry = true;
            leftHandTextureAudio.aggregateTelemetry = new[] { _leftAudioTelemetry };
        }

        if (rightHandTextureAudio)
        {
            rightHandTextureAudio.useGlobalTelemetry = true;
            rightHandTextureAudio.aggregateTelemetry = new[] { _rightAudioTelemetry };
        }

        if (leftHandThermalAudio)
        {
            leftHandThermalAudio.useGlobalTelemetry = true;
            leftHandThermalAudio.aggregateTelemetry = new[] { _leftAudioTelemetry };
        }

        if (rightHandThermalAudio)
        {
            rightHandThermalAudio.useGlobalTelemetry = true;
            rightHandThermalAudio.aggregateTelemetry = new[] { _rightAudioTelemetry };
        }

        _debugAudioTelemetryRouted = true;
    }

    void TeardownDebugAudioTelemetryRouting()
    {
        if (!_debugAudioTelemetryRouted)
            return;

        // Restore scene defaults: per-hand telemetry from HapticsGlobalData (see Main.unity useGlobalTelemetry: 0).
        void ResetTextureAudio(HandTextureAudio a)
        {
            if (!a) return;
            a.useGlobalTelemetry = false;
            a.aggregateTelemetry = null;
        }

        void ResetThermalAudio(HandThermalAudio a)
        {
            if (!a) return;
            a.useGlobalTelemetry = false;
            a.aggregateTelemetry = null;
        }

        ResetTextureAudio(leftHandTextureAudio);
        ResetTextureAudio(rightHandTextureAudio);
        ResetThermalAudio(leftHandThermalAudio);
        ResetThermalAudio(rightHandThermalAudio);

        if (_leftAudioTelemetry)
        {
            Destroy(_leftAudioTelemetry);
            _leftAudioTelemetry = null;
        }

        if (_rightAudioTelemetry)
        {
            Destroy(_rightAudioTelemetry);
            _rightAudioTelemetry = null;
        }

        _debugAudioTelemetryRouted = false;
    }

    void PublishDebugAudioTelemetry()
    {
        if (!debugMode || debugAVCouplingMode != DebugAVCouplingMode.Incongruent)
            return;

        EnsureDebugAudioTelemetryRouting();

        if (_leftAudioTelemetry == null || _rightAudioTelemetry == null)
            return;

        var globals = HapticsGlobalData.Instance;
        if (!globals)
            return;

        var leftSrc = globals.leftHandTelemetryChannel;
        var rightSrc = globals.rightHandTelemetryChannel;
        if (!leftSrc || !rightSrc)
            return;

        _leftAudioTelemetry.Publish(RemapSurfaceForIncongruentAudio(leftSrc.Latest));
        _rightAudioTelemetry.Publish(RemapSurfaceForIncongruentAudio(rightSrc.Latest));
    }

    static HandTelemetrySnapshot RemapSurfaceForIncongruentAudio(in HandTelemetrySnapshot s)
    {
        SurfaceType mapped = SwapSurfaceWithinTextureThermal(s.SurfaceType);
        if (mapped == s.SurfaceType)
            return s;

        return new HandTelemetrySnapshot(
            s.IsTouching,
            s.IsTouchingRaw,
            s.IsSliding,
            s.ContactEnvelope01,
            s.TangentialSpeed,
            s.TangentialVelocity,
            s.ContactNormal,
            s.ContactPoint,
            s.ContactCoverage01,
            s.CurrentRoughness01,
            mapped,
            s.Surface
        );
    }
}
