using UnityEngine;
using UnityEngine.UI;


public class GameSettingsManager : MonoBehaviour
{
    [Header("UI Images (swap these)")]
    [SerializeField] private Image soundSettingsImage;
    [SerializeField] private Image joystickSettingsImage;

    [Header("Sound Sprites (0%, 50%, 100%)")]
    [SerializeField] private Sprite sound0Sprite;   // mute
    [SerializeField] private Sprite sound50Sprite;  // half
    [SerializeField] private Sprite sound100Sprite; // full

    [Header("Joystick Sprites (On/Off)")]
    [SerializeField] private Sprite joystickOnSprite;
    [SerializeField] private Sprite joystickOffSprite;

    [Header("Joystick Roots (set BOTH here)")]
    [SerializeField] private GameObject[] joystickGameObjects; // ← drop both joystick roots here

    // >>> State <<<
    // 0 = 0%, 1 = 50%, 2 = 100%
    public static int SoundLevel { get; private set; } = 2;
    public static bool isSoundEnabled => SoundLevel > 0;

    public static bool isVirtualJoystickEnabled { get; private set; } = true;

    // Pref keys
    private const string KEY_SOUND_LEVEL = "settings.sound.level";      // int 0..2
    private const string KEY_JOYSTICK = "settings.joystick.enabled"; // 0/1

    // Volume mapping
    private static readonly float[] SOUND_STEPS = { 0f, 0.5f, 1f };

    private void Awake()
    {
        // Defaults: full sound, joystick ON (change 1 -> 0 if you want default OFF)
        SoundLevel = Mathf.Clamp(PlayerPrefs.GetInt(KEY_SOUND_LEVEL, 2), 0, 2);
        isVirtualJoystickEnabled = PlayerPrefs.GetInt(KEY_JOYSTICK, 1) == 1;

        ApplySound(SoundLevel);
        ApplyJoystick(isVirtualJoystickEnabled);
        UpdateUI();
    }

    // Hook this to your Sound button OnClick
    public void CycleSoundLevel()
    {
        SoundLevel = (SoundLevel + 1) % 3; // 0 -> 1 -> 2 -> 0
        PlayerPrefs.SetInt(KEY_SOUND_LEVEL, SoundLevel);
        PlayerPrefs.Save();

        ApplySound(SoundLevel);
        UpdateUI();
    }

    // Hook this to your Joystick button OnClick
    public void ToggleJoystick()
    {
        isVirtualJoystickEnabled = !isVirtualJoystickEnabled;
        PlayerPrefs.SetInt(KEY_JOYSTICK, isVirtualJoystickEnabled ? 1 : 0);
        PlayerPrefs.Save();

        ApplyJoystick(isVirtualJoystickEnabled);
        UpdateUI();
    }

    // Convenience (optional)
    public void EnableJoysticks() { SetJoysticks(true); }
    public void DisableJoysticks() { SetJoysticks(false); }

    private void ApplySound(int level)
    {
        float vol = SOUND_STEPS[level];
        AudioListener.volume = vol;
        AudioListener.pause = vol == 0f; // optional pause when muted
    }

    private void ApplyJoystick(bool enabled) => SetJoysticks(enabled);

    private void SetJoysticks(bool enabled)
    {
        if (joystickGameObjects == null) return;
        for (int i = 0; i < joystickGameObjects.Length; i++)
        {
            var go = joystickGameObjects[i];
            if (go) go.SetActive(enabled);
        }
    }

    private void UpdateUI()
    {
        if (soundSettingsImage != null)
        {
            soundSettingsImage.sprite = SoundLevel switch
            {
                0 => sound0Sprite,
                1 => sound50Sprite,
                _ => sound100Sprite
            };
        }

        if (joystickSettingsImage != null)
            joystickSettingsImage.sprite = isVirtualJoystickEnabled ? joystickOnSprite : joystickOffSprite;
    }
}
