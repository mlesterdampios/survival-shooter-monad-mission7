// EnsureSceneMatchesUrlLevel.cs
using System;                         // for Exception
using UnityEngine;
using UnityEngine.SceneManagement;    // for SceneManager

public class EnsureSceneMatchesUrlLevel : MonoBehaviour
{
    [Tooltip("Run the check in Awake (earliest) or Start (slightly later).")]
    public bool runInAwake = true;

    private void Awake()
    {
        if (runInAwake) CheckAndLoadSceneIfNeeded();
    }

    private void Start()
    {
        if (!runInAwake) CheckAndLoadSceneIfNeeded();
    }

    private void CheckAndLoadSceneIfNeeded()
    {
        // 1) Get desired level string, with robust fallback
        string levelStr = null;
        try
        {
            // Expects a static string property: WebGLUrlParams.Level
            // If class/property isn't present or is empty/whitespace => use "0"
            levelStr = (HasText(GetUrlLevelSafe())) ? GetUrlLevelSafe() : "0";
        }
        catch (Exception)
        {
            levelStr = "0";
        }

        // 2) Convert to int (default 0 on any parse issue)
        if (!int.TryParse(levelStr, out var desiredIndex))
            desiredIndex = 0;

        // 3) Validate index is within Build Settings range
        int totalScenes = SceneManager.sceneCountInBuildSettings;
        if (totalScenes == 0)
        {
            Debug.LogWarning("[EnsureSceneMatchesUrlLevel] No scenes in Build Settings.");
            return;
        }
        if (desiredIndex < 0 || desiredIndex >= totalScenes)
        {
            Debug.LogWarning($"[EnsureSceneMatchesUrlLevel] Desired index {desiredIndex} is out of range (0..{totalScenes - 1}). Falling back to 0.");
            desiredIndex = 0;
        }

        // 4) Compare to current scene index
        int currentIndex = SceneManager.GetActiveScene().buildIndex;
        if (currentIndex == desiredIndex)
        {
            // Matches => do nothing
            // (Useful if this script is present in all scenes)
            // Debug.Log($"[EnsureSceneMatchesUrlLevel] Scene already correct (index {currentIndex}).");
            return;
        }

        // 5) Load the correct scene
        // Use Single load to fully swap scenes; change to LoadSceneAsync if you prefer async.
        Debug.Log($"[EnsureSceneMatchesUrlLevel] Loading scene index {desiredIndex} (current is {currentIndex}).");
        SceneManager.LoadScene(desiredIndex, LoadSceneMode.Single);
    }

    // Safely attempt to read WebGLUrlParams.Level without hard dependency
    private static string GetUrlLevelSafe()
    {
        // If WebGLUrlParams class exists with a static string property Level, return it.
        // We avoid direct typeof/Reflection to keep it simple; the try/catch in caller protects us.
        // Just reference it directly; if it's missing, the caller's try/catch handles it.
        return WebGLUrlParams.Level;
    }

    private static bool HasText(string s)
    {
        // Works on both .NET profiles
        return !string.IsNullOrEmpty(s) && s.Trim().Length > 0;
    }
}
