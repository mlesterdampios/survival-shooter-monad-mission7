// LeaderboardManager.cs
// Unity 2022.3.x
// Immediate send; if NO HTTP RESPONSE, resend at 30s since attempt start (network-only).
// API rejections (any HTTP non-2xx) are dropped immediately (no retry).
// Submission display: last 5 successful lines (oldest→newest), no auto-clear.
// Logs ALL JSON responses (2xx/4xx/5xx) and transport failures when verboseLogging = true.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using TMPro;

public class LeaderboardManager : MonoBehaviour
{
    // ---------- Scene-local singleton (no DontDestroyOnLoad) ----------
    private static LeaderboardManager _instance;
    public static LeaderboardManager Instance
    {
        get
        {
            if (_instance == null)
            {
                var go = new GameObject("[LeaderboardManager]");
                _instance = go.AddComponent<LeaderboardManager>();
            }
            return _instance;
        }
    }

    private void Awake()
    {
        if (_instance == null) _instance = this;
        else if (_instance != this) { Destroy(gameObject); return; }
    }

    // ---------- Config ----------
    [Header("API")]
    public string baseApiUrl = "https://monad-mission7-api.rxmsolutions.com/api/v1";

    [Header("Leaderboard Defaults")]
    public int defaultGameId = 64;
    public int defaultPage = 1;
    public int defaultLimit = 5;

    [Header("Networking")]
    [Tooltip("UnityWebRequest per-request timeout (seconds)")]
    public int requestTimeoutSeconds = 10;

    [Tooltip("Print all HTTP/JSON replies and transport errors to Console")]
    public bool verboseLogging = true;

    [Header("Resend Watchdog")]
    [Tooltip("If no HTTP response (network issue), resend the same submission at 30s since attempt start")]
    public float resendIntervalSeconds = 30f;

    [Tooltip("Max number of resends after the first send (avoid infinite loop)")]
    public int maxResendAttempts = 10;

    [Header("UI - Leaderboard (optional)")]
    public LeaderboardRow[] uiRows = new LeaderboardRow[5];

    [Header("UI - Submission Log (last 5, oldest→newest)")]
    [Tooltip("A TextMeshProUGUI that will show the last 5 successful submissions, each on a new line.")]
    public TextMeshProUGUI submissionToast; // now acts as a running log

    // Keep a rolling buffer of the last 5 lines (oldest at index 0)
    private readonly List<string> _submissionLines = new List<string>(5);

    // ========= STATIC API =========

    /// <summary>
    /// Send immediately (no queue). If NO HTTP RESPONSE, resend at 30s since attempt start.
    /// On success/ack, appends a line to the submission log; failures are silent.
    /// </summary>
    public static void SubmitScoreImmediate(int score, bool showToast = true)
    {
        var wal = string.IsNullOrWhiteSpace(WebGLUrlParams.WalletAddress) ? "(none)" : WebGLUrlParams.WalletAddress;
        Instance.StartCoroutine(Instance.SubmitWithWatchdog(wal, score, showToast));
    }

    // Back-compat alias
    public static void SubmitScoreAndShowToast(int score) =>
        SubmitScoreImmediate(score, true);

    public static void GetLeaderboard(
        int gameId, int page, int limit,
        Action<LeaderboardResponse> onSuccess, Action<ApiError> onError)
    {
        Instance.StartCoroutine(Instance.GetLeaderboardRoutine(gameId, page, limit, onSuccess, onError));
    }

    public static void FetchAndPopulateDefault()
    {
        Instance.StartCoroutine(Instance.GetLeaderboardRoutine(
            Instance.defaultGameId, Instance.defaultPage, Instance.defaultLimit,
            lb => Instance.PopulateUI(lb),
            err =>
            {
                if (Instance.verboseLogging) Debug.LogError(err.ToString());
                Instance.ClearUIWithMessage("—");
            }));
    }

    // ========= Submission + Watchdog =========

    private class SubmitOutcome
    {
        public bool isAck;            // true => 202 ack ; false => 200 mined
        public long httpStatus;
        public string rawJson;
        public string xJobId;         // header if present
        public Submit200 tx;          // 200 model
        public Submit202 ack;         // 202 model
    }

    private IEnumerator SubmitWithWatchdog(string walletAddress, int score, bool showToast)
    {
        int resends = 0;
        bool done = false;

        while (!done && resends <= maxResendAttempts)
        {
            float attemptStart = Time.realtimeSinceStartup;

            // One attempt
            yield return SendOnce(walletAddress, score,
                onSuccess: outcome =>
                {
                    HandleSubmitSuccess(outcome, score, showToast);
                    done = true; // success (200/202)
                },
                onNon2xx: _ =>
                {
                    // API rejected (any HTTP non-2xx) → drop immediately, no retry
                    done = true;
                },
                onNoResponse: () =>
                {
                    // No HTTP response (transport error / timeout): we will wait remaining time to 30s mark.
                });

            if (done) break;

            // Wait until 30 seconds since attempt start before resending
            float elapsed = Time.realtimeSinceStartup - attemptStart;
            float remaining = Mathf.Max(0f, resendIntervalSeconds - elapsed);
            if (remaining > 0f)
                yield return new WaitForSecondsRealtime(remaining);

            resends++;
        }
        // If resends exceeded with no response, we drop silently.
    }

    private void HandleSubmitSuccess(SubmitOutcome outcome, int score, bool showToast)
    {
        // We already log the raw response in SendOnce when verboseLogging = true
        if (!showToast) return;

        if (outcome.isAck)
        {
            // Prefer header; fall back to body.jobId
            string jid = !string.IsNullOrEmpty(outcome.xJobId) ? outcome.xJobId : outcome.ack?.jobId;
            AppendSubmissionLine(BuildAckToast(score, jid));
        }
        else
        {
            AppendSubmissionLine(BuildSuccessToast(score, outcome.tx?.txHash));
        }
    }

    /// <summary>
    /// Sends exactly one HTTP POST and classifies the outcome.
    /// Always logs the HTTP/JSON (or transport failure) when verboseLogging is true.
    /// </summary>
    private IEnumerator SendOnce(
    string walletAddress, int score,
    Action<SubmitOutcome> onSuccess,
    Action<ApiError> onNon2xx,
    Action onNoResponse)
    {
        string url = $"{baseApiUrl}/submitscore";
        var bodyObj = new SubmitScoreRequest { walletAddress = walletAddress, score = score };
        var json = JsonUtility.ToJson(bodyObj);
        var bytes = Encoding.UTF8.GetBytes(json);

        using (var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
        {
            req.timeout = requestTimeoutSeconds;
            req.uploadHandler = new UploadHandlerRaw(bytes);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.SetRequestHeader("Accept", "application/json");

            yield return req.SendWebRequest();

            // Log everything
            if (verboseLogging)
            {
                if (req.responseCode != 0) LogHttp(req);
                else LogTransportFailure(req);
            }

            // ✅ Only no-response when there's truly no HTTP status.
            if (req.responseCode == 0)
            {
                onNoResponse?.Invoke();
                yield break;
            }

            long status = req.responseCode;
            string text = req.downloadHandler?.text ?? "";
            string xJobId = req.GetResponseHeader("X-Job-Id");

            if (status == 200)
            {
                try
                {
                    var ok = JsonUtility.FromJson<Submit200>(text);
                    if (ok == null || !ok.ok)
                    {
                        onNon2xx?.Invoke(ApiError.BadPayload("200 body invalid/ok=false", text, status));
                        yield break;
                    }
                    onSuccess?.Invoke(new SubmitOutcome
                    {
                        isAck = false,
                        httpStatus = status,
                        rawJson = text,
                        xJobId = xJobId,
                        tx = ok
                    });
                }
                catch (Exception ex)
                {
                    onNon2xx?.Invoke(ApiError.JsonParse(text, ex));
                }
            }
            else if (status == 202)
            {
                try
                {
                    var ack = JsonUtility.FromJson<Submit202>(text);
                    string job = !string.IsNullOrEmpty(xJobId) ? xJobId : ack?.jobId;
                    if (string.IsNullOrEmpty(job))
                    {
                        onNon2xx?.Invoke(ApiError.BadPayload("202 missing job id", text, status));
                        yield break;
                    }
                    onSuccess?.Invoke(new SubmitOutcome
                    {
                        isAck = true,
                        httpStatus = status,
                        rawJson = text,
                        xJobId = xJobId,
                        ack = ack
                    });
                }
                catch (Exception ex)
                {
                    onNon2xx?.Invoke(ApiError.JsonParse(text, ex));
                }
            }
            else
            {
                // Any HTTP non-2xx = final rejection → no retry
                onNon2xx?.Invoke(ApiError.FromRequest(req));
            }
        }
    }


    // ========= Leaderboard =========

    private IEnumerator GetLeaderboardRoutine(
        int gameId, int page, int limit,
        Action<LeaderboardResponse> onSuccess, Action<ApiError> onError)
    {
        string url = $"{baseApiUrl}/getleaderboard?gameId={gameId}&page={page}&limit={limit}";

        using (var req = UnityWebRequest.Get(url))
        {
            req.timeout = requestTimeoutSeconds;
            req.SetRequestHeader("Accept", "application/json");

            yield return req.SendWebRequest();

            // ---- Log EVERYTHING (HTTP or transport) ----
            if (verboseLogging)
            {
                if (req.responseCode != 0) LogHttp(req);
                else LogTransportFailure(req);
            }

            if (req.result != UnityWebRequest.Result.Success || req.responseCode < 200 || req.responseCode >= 300)
            {
                onError?.Invoke(ApiError.FromRequest(req));
                yield break;
            }

            var text = req.downloadHandler?.text ?? "";

            try
            {
                var data = JsonUtility.FromJson<LeaderboardResponse>(text);
                if (data == null || !data.ok)
                {
                    onError?.Invoke(ApiError.BadPayload("Leaderboard ok=false/invalid", text, req.responseCode));
                    yield break;
                }
                onSuccess?.Invoke(data);
            }
            catch (Exception ex)
            {
                onError?.Invoke(ApiError.JsonParse(text, ex));
            }
        }
    }

    // ========= UI helpers =========

    public void PopulateUI(LeaderboardResponse lb)
    {
        ClearUIWithMessage("");

        if (lb?.scoreData == null || lb.scoreData.Length == 0)
        {
            ClearUIWithMessage("—");
            return;
        }

        int count = Mathf.Min(uiRows?.Length ?? 0, lb.scoreData.Length);
        for (int i = 0; i < count; i++)
        {
            var row = uiRows[i];
            if (row == null) continue;

            var e = lb.scoreData[i];
            string formattedName = FormatUserAndWallet(e?.username, e?.walletAddress);
            string scoreText = e != null ? e.score.ToString("N0") : "0";

            if (row.nameAndWallet != null) row.nameAndWallet.text = formattedName;
            if (row.score != null) row.score.text = scoreText;
        }
    }

    private void ClearUIWithMessage(string message)
    {
        if (uiRows == null) return;
        foreach (var r in uiRows)
        {
            if (r == null) continue;
            if (r.nameAndWallet != null) r.nameAndWallet.text = message;
            if (r.score != null) r.score.text = "";
        }
    }

    // ===== Submission log helpers (no auto-clear) =====

    private void AppendSubmissionLine(string message)
    {
        if (string.IsNullOrEmpty(message)) return;

        // Keep last 5, oldest at top, newest at bottom
        if (_submissionLines.Count == 5) _submissionLines.RemoveAt(0);
        _submissionLines.Add(message);

        if (submissionToast != null)
        {
            submissionToast.text = string.Join("\n", _submissionLines);
        }
    }

    private string FormatUserAndWallet(string username, string wallet)
    {
        string u = username ?? "Unknown";
        const int maxUser = 10;
        string trimmed = u.Length <= maxUser ? u : u.Substring(0, maxUser) + "...";

        string w = wallet ?? "0x";
        string hex = w.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? w.Substring(2) : w;
        hex = KeepHex(hex);

        string first6 = hex.Length <= 6 ? hex : hex.Substring(0, 6);
        string walletShort = $"0x{first6}{(hex.Length > 6 ? "..." : "")}";

        return $"{trimmed} ({walletShort})";
    }

    private string KeepHex(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            bool isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (isHex) sb.Append(c);
        }
        return sb.ToString();
    }

    private string BuildSuccessToast(int score, string txHash)
    {
        // TX: 9 max hex chars
        string hex = string.IsNullOrEmpty(txHash)
            ? ""
            : (txHash.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? txHash.Substring(2) : txHash);
        hex = KeepHex(hex).ToUpperInvariant();
        string first9 = hex.Length <= 9 ? hex : hex.Substring(0, 9);
        return $"Score: {score} TX: 0X{first9}{(hex.Length > 9 ? "..." : "")}";
    }

    private string BuildAckToast(int score, string jobId)
    {
        // Compact job id (first 10 chars)
        string j = jobId ?? "";
        string shortId = j.Length <= 10 ? j : j.Substring(0, 10) + "...";
        return $"Score: {score} X-Job-Id: {shortId}";
    }

    // ========= Logging helpers =========

    private const int LOG_BODY_MAX = 4000; // avoid spamming the Console

    private void LogHttp(UnityWebRequest req)
    {
        string body = req.downloadHandler?.text ?? "";
        if (body.Length > LOG_BODY_MAX) body = body.Substring(0, LOG_BODY_MAX) + "...";
        string xJobId = req.GetResponseHeader("X-Job-Id");
        string jobInfo = string.IsNullOrEmpty(xJobId) ? "" : $" | X-Job-Id={xJobId}";
        Debug.Log($"[LeaderboardManager] {req.method} {req.url} -> HTTP {req.responseCode} ({req.result}){jobInfo}\n{body}");
    }

    private void LogTransportFailure(UnityWebRequest req)
    {
        // Typically responseCode==0 on connection/timeout
        string body = req.downloadHandler?.text ?? "";
        if (body.Length > LOG_BODY_MAX) body = body.Substring(0, LOG_BODY_MAX) + "...";
        Debug.LogWarning($"[LeaderboardManager] {req.method} {req.url} -> NO HTTP RESPONSE ({req.result}) : {req.error}\n{body}");
    }

    // ========= Models =========

    [Serializable] public class LeaderboardRow { public TextMeshProUGUI nameAndWallet; public TextMeshProUGUI score; }

    // Submit request
    [Serializable] public class SubmitScoreRequest { public string walletAddress; public int score; }

    // ✅ 200 — mined ≤5s
    [Serializable]
    public class Submit200
    {
        public bool ok;
        public string txHash;
        public long blockNumber;
        public int status;
        public string gasUsed;
        public string to;
        public string from;
        public int nonce;
    }

    // 🟨 202 — early-ack / failsafe queued
    [Serializable]
    public class Submit202
    {
        public bool ok;
        public bool queued;
        public string message;
        public string jobId;
        public string statusUrl;
        public int nonce;              // early-ack variant (may be present)
        public int ackMs;              // early-ack variant (may be present)
        public int approxBatchInMs;    // failsafe variant (may be present)
    }

    // Leaderboard models
    [Serializable]
    public class LeaderboardResponse
    {
        public bool ok;
        public int gameId;
        public string gameName;
        public string lastUpdated;
        public Pagination scorePagination;
        public Pagination transactionPagination;
        public ScoreEntry[] scoreData;
        public TransactionEntry[] transactionData;
        public SourceInfo source;

        [Serializable] public class Pagination { public int page; public int limit; public string total; public int totalPages; public int TotalAsInt() => int.TryParse(total, out var v) ? v : -1; }
        [Serializable] public class ScoreEntry { public int userId; public string username; public string walletAddress; public int score; public int gameId; public string gameName; public int rank; }
        [Serializable] public class TransactionEntry { public int userId; public string username; public string walletAddress; public int transactionCount; public int gameId; public string gameName; public int rank; }
        [Serializable]
        public class SourceInfo
        {
            // Old field kept for backwards compatibility (older server builds used "url")
            public string url;

            // Map JSON property "base" using C# escaped identifier
            public string @base;

            // New pages array
            public string[] pages;

            public string fetchedAt;

            // Helper: prefer explicit url, else fall back to base
            public string EffectiveUrl() => !string.IsNullOrEmpty(url) ? url : @base;
        }
    }

    [Serializable]
    public class ApiError
    {
        public bool isNetworkError;
        public long statusCode;
        public string message;
        public string rawBody;
        public int? retryAfterSeconds;

        public static ApiError FromRequest(UnityWebRequest req)
        {
            var err = new ApiError
            {
                isNetworkError = req.result == UnityWebRequest.Result.ConnectionError,
                statusCode = req.responseCode,
                message = string.IsNullOrEmpty(req.error) ? $"HTTP {req.responseCode}" : req.error,
                rawBody = req.downloadHandler?.text
            };
            var retryAfter = req.GetResponseHeader("Retry-After");
            if (int.TryParse(retryAfter, out int s)) err.retryAfterSeconds = s;
            return err;
        }

        public static ApiError JsonParse(string json, Exception ex) =>
            new ApiError { isNetworkError = false, statusCode = 200, message = $"JSON parse error: {ex.GetType().Name}: {ex.Message}", rawBody = json };

        public static ApiError BadPayload(string msg, string json, long statusCode) =>
            new ApiError { isNetworkError = false, statusCode = statusCode, message = msg, rawBody = json };

        public override string ToString()
        {
            var bodyPreview = string.IsNullOrEmpty(rawBody) ? "(no body)" :
                (rawBody.Length > 300 ? rawBody.Substring(0, 300) + "..." : rawBody);
            string retryNote = retryAfterSeconds.HasValue ? $" | Retry-After: {retryAfterSeconds.Value}s" : "";
            return $"[LeaderboardManager Error] Net:{isNetworkError} HTTP:{statusCode}{retryNote} | {message}\nBody: {bodyPreview}";
        }
    }
}
