using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

[DefaultExecutionOrder(-1000)]
public class WebGLUrlParams : MonoBehaviour
{
    // ===== Public static values available to all scripts =====
    public static string WalletAddress { get; private set; } = "";
    public static string Username { get; private set; } = "";
    public static string Character { get; private set; } = "";
    public static string Level { get; private set; } = "";

    // Presence flags: true if the key existed (even if its value is "")
    public static bool HasWalletAddress { get; private set; }
    public static bool HasUsername { get; private set; }
    public static bool HasCharacter { get; private set; }
    public static bool HasLevel { get; private set; }

    // Ready flag + event
    public static bool IsReady { get; private set; } = false;
    public static event Action OnReady;

    private static bool _initialized = false;

#if UNITY_EDITOR
    [Header("Editor only (optional): simulate Application.absoluteURL")]
    [TextArea(1, 4)]
    [SerializeField]
    private string editorTestUrl =
        "https://monad-mission7.rxmsolutions.com/?token=eyJ3YWxsZXRBZGRyZXNzIjoiMHg1MWFDOGI5NDk1RTZBRTQyMzQ5MUQxYTE2Mzc2QjM3ODgwY0ExZmRCIiwidXNlcm5hbWUiOiJtbGQifQ";
#endif

    private void Awake()
    {
        // Keep only the first initializer during the app lifetime.
        if (_initialized) { Destroy(this); return; }
        _initialized = true;

        try
        {
            ParseFromUrl(GetCurrentUrl());
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[WebGLUrlParams] Failed to parse URL: {e.Message}");
        }

        IsReady = true;
        OnReady?.Invoke();

        Debug.Log($"[WebGLUrlParams] Ready. " +
                  $"walletAddress='{WalletAddress}', username='{Username}', " +
                  $"character='{Character}', level='{Level}'");
    }

    private string GetCurrentUrl()
    {
#if UNITY_EDITOR
        if (!string.IsNullOrWhiteSpace(editorTestUrl))
            return editorTestUrl.Trim();
#endif
        return Application.absoluteURL ?? string.Empty;
    }

    private void ParseFromUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return;

        var query = ExtractQuery(url);
        var kv = ParseQuery(query);

        // 1) token: base64/base64url JSON
        if (kv.TryGetValue("token", out var tokenRaw))
        {
            var json = TryDecodeTokenToJson(tokenRaw);
            if (!string.IsNullOrEmpty(json))
                ApplyFromJson(json);
        }

        // 2) Fallback: direct query params (these also set Has* even if empty)
        if (kv.TryGetValue("walletAddress", out var wa))
        {
            HasWalletAddress = true;
            WalletAddress = wa ?? "";
        }
        if (kv.TryGetValue("username", out var un))
        {
            HasUsername = true;
            Username = un ?? "";
        }
        if (kv.TryGetValue("character", out var ch))
        {
            HasCharacter = true;
            Character = ch ?? "";
        }
        if (kv.TryGetValue("level", out var lv))
        {
            HasLevel = true;
            Level = lv ?? "";
        }
    }

    private static string ExtractQuery(string url)
    {
        int q = url.IndexOf('?');
        if (q < 0) return string.Empty;
        var query = url.Substring(q + 1);

        int hash = query.IndexOf('#');
        if (hash >= 0) query = query.Substring(0, hash);

        return query;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(query)) return result;

        var parts = query.Split('&');
        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part)) continue;

            int eq = part.IndexOf('=');
            string key, val;
            if (eq >= 0)
            {
                key = part.Substring(0, eq);
                val = part.Substring(eq + 1);
            }
            else
            {
                key = part;
                val = "";
            }

            key = Uri.UnescapeDataString(key.Replace('+', ' '));
            val = Uri.UnescapeDataString(val.Replace('+', ' '));

            if (!string.IsNullOrEmpty(key))
                result[key] = val;
        }
        return result;
    }

    private static string TryDecodeTokenToJson(string tokenRaw)
    {
        if (string.IsNullOrEmpty(tokenRaw)) return null;

        // Convert Base64URL -> Base64 and fix padding
        string b64 = tokenRaw.Replace('-', '+').Replace('_', '/');
        int mod4 = b64.Length % 4;
        if (mod4 > 0) b64 = b64.PadRight(b64.Length + (4 - mod4), '=');

        try
        {
            var bytes = Convert.FromBase64String(b64);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[WebGLUrlParams] Base64 decode failed: {e.Message}");
            return null;
        }
    }

    [Serializable]
    private class TokenPayload
    {
        public string walletAddress;
        public string username;
        public string character;
        public string level;
    }

    private static void ApplyFromJson(string json)
    {
        try
        {
            var p = JsonUtility.FromJson<TokenPayload>(json ?? "{}");
            if (p == null) return;

            if (p.walletAddress != null) { HasWalletAddress = true; WalletAddress = p.walletAddress ?? ""; }
            if (p.username != null) { HasUsername = true; Username = p.username ?? ""; }
            if (p.character != null) { HasCharacter = true; Character = p.character ?? ""; }
            if (p.level != null) { HasLevel = true; Level = p.level ?? ""; }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[WebGLUrlParams] JSON parse failed: {e.Message}");
        }
    }
}
