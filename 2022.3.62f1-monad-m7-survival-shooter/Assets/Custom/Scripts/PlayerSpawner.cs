using System;
using UnityEngine;
using UnityEngine.UI;

[DefaultExecutionOrder(-50)] // spawn early so others can read the static refs
public class PlayerSpawner : MonoBehaviour
{
    [Header("Assign in index order: 0 = Player0, 1 = Player1, 2 = Player2")]
    [Tooltip("Size this to 3 and drop in Player0, Player1, Player2 (in that order).")]
    public GameObject[] playerPrefabs;

    [Header("Optional")]
    [Tooltip("If set >= 0, this overrides URL selection (useful in Editor).")]
    [Range(-1, 10)]
    public int overrideCharacterIndex = -1;

    [Tooltip("If provided, spawn at this transform; otherwise uses this GameObject's transform.")]
    public Transform spawnPoint;

    [Tooltip("If true, spawns automatically in Start().")]
    public bool autoSpawn = true;

    // --- Static references to the spawned player ---
    public static GameObject Player { get; private set; }
    public static Transform PlayerTransform { get; private set; }

    public static PlayerHealth _PlayerHealth { get; private set; }

    public static Animator PlayerAnimator { get; private set; }

    // Optional event: raised after we spawn
    public static event Action<GameObject> OnPlayerSpawned;

    public Slider _healthSlider;
    public static Slider healthSlider;

    public Image _damageImage;
    public static Image damageImage;

    void Start()
    {
        healthSlider = _healthSlider;

        damageImage = _damageImage;

        if (autoSpawn)
        {
            TrySpawn();
        }
    }

    /// <summary>
    /// Spawns the player according to WebGLUrlParams.character or overrideCharacterIndex.
    /// If a player already exists, it will be destroyed and replaced.
    /// </summary>
    public void TrySpawn()
    {
        if (playerPrefabs == null || playerPrefabs.Length == 0)
        {
            Debug.LogError("[PlayerSpawner] No prefabs assigned.");
            return;
        }

        int index = ResolveCharacterIndex();
        if (index < 0 || index >= playerPrefabs.Length)
        {
            Debug.LogWarning($"[PlayerSpawner] Resolved index {index} is out of range; clamping.");
            index = Mathf.Clamp(index, 0, playerPrefabs.Length - 1);
        }

        var prefab = playerPrefabs[index];
        if (prefab == null)
        {
            Debug.LogError($"[PlayerSpawner] Prefab at index {index} is null.");
            return;
        }

        // Replace existing player if present
        if (Player != null)
        {
            Destroy(Player);
            Player = null;
            PlayerTransform = null;
            _PlayerHealth = null;
            PlayerAnimator = null;
        }

        Vector3 pos = spawnPoint ? spawnPoint.position : transform.position;
        Quaternion rot = spawnPoint ? spawnPoint.rotation : transform.rotation;

        Player = Instantiate(prefab, pos, rot);
        PlayerTransform = Player.transform;
        _PlayerHealth = Player.GetComponent<PlayerHealth>();
        PlayerAnimator = Player.GetComponent<Animator>();
        if (PlayerAnimator == null)
            PlayerAnimator = Player.GetComponentInChildren<Animator>();

        Debug.Log($"[PlayerSpawner] Spawned '{prefab.name}' at index {index}.");

        OnPlayerSpawned?.Invoke(Player);
    }

    /// <summary>
    /// Despawns the current player, if any.
    /// </summary>
    public static void Despawn()
    {
        if (Player != null)
        {
            Destroy(Player);
            Player = null;
            PlayerTransform = null;
            _PlayerHealth = null;
            PlayerAnimator = null;
        }
    }

    /// <summary>
    /// Respawns the player at a specific position/rotation (keeps same selected prefab).
    /// </summary>
    public void RespawnAt(Vector3 position, Quaternion rotation)
    {
        // Temporarily use a custom spawn point
        bool hadTemp = spawnPoint != null;
        var temp = spawnPoint;

        var holder = new GameObject("~TempSpawnPoint").transform;
        holder.position = position;
        holder.rotation = rotation;
        spawnPoint = holder;

        TrySpawn();

        // Clean up temp holder
        spawnPoint = hadTemp ? temp : null;
        Destroy(holder.gameObject);
    }

    /// <summary>
    /// Resolves the desired character index from either the override or WebGLUrlParams.character.
    /// </summary>
    private int ResolveCharacterIndex()
    {
        // Inspector override takes priority if set
        if (overrideCharacterIndex >= 0)
            return overrideCharacterIndex;

        // Fallback to URL param: string -> int
        string characterStr = null;
        try
        {
            // Expects a static string property: WebGLUrlParams.character
            characterStr = string.IsNullOrWhiteSpace(WebGLUrlParams.Character)
                ? "0"
                : WebGLUrlParams.Character;
        }
        catch (Exception)
        {
            // If WebGLUrlParams isn't present in this build, default to "0"
            characterStr = "0";
        }

        if (!int.TryParse(characterStr, out int idx))
            idx = 0;

        return idx;
    }

    // --- Convenience helpers for other scripts ---

    /// <summary>
    /// Quick check if a player has been spawned.
    /// </summary>
    public static bool HasPlayer => Player != null;

    /// <summary>
    /// Safely get a component from the spawned player.
    /// </summary>
    public static T GetOnPlayer<T>() where T : Component
    {
        return Player ? Player.GetComponent<T>() : null;
    }
}
