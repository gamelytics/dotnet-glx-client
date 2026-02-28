using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;


public class GlxClient
{
    // Events (replacing signals)
    public event Action<List<Dictionary<string, object>>> OnScoresFetched;
    public event Action<bool, string, string> OnRequestCompleted;

    // Constants
    private const string ApiBaseUrl = "https://api.gamelytics.io/";
    private const string EventApiEndpoint = "events";
    private const string ScoreApiEndpoint = "scores";

    public enum MetadataType { Event, Score }

    private Dictionary<string, object>[] _defaultMetadata = new Dictionary<string, object>[2] { new(), new() };

    // Settings handled via constructor params
    private readonly string _gameKey;
    private readonly string _gameSecret;
    private readonly string _gameVersion;
    private readonly bool _autoSessionStart;
    private bool _showDebugMessages;

    private bool _credentialsSet = false;
    private readonly HttpClient _httpClient = new HttpClient();
    private bool _initialized = false;

    private string _playerId = "";
    private readonly string _playerIdFilePath;
    private string _sessionId = "";
    private string _lastRequestType = "";
    private readonly object _pendingRequestsLock = new();
    private readonly HashSet<Task> _pendingRequests = new();



    // Constructor
    public GlxClient(string gameKey, string gameSecret, string gameVersion = "1.0.0", bool autoSessionStart = false, bool showDebugMessages = false, string playerIdFilePath = null)
    {
        _gameKey = gameKey;
        _gameSecret = gameSecret;
        _gameVersion = gameVersion;
        _autoSessionStart = autoSessionStart;
        _showDebugMessages = showDebugMessages;

        if (string.IsNullOrEmpty(playerIdFilePath))
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            _playerIdFilePath = Path.Combine(appData, "glx_player_id.cfg");
        }
        else
        {
            _playerIdFilePath = playerIdFilePath;
        }

        // Equivalent to part of _ready: resolve player ID
        ResolvePlayerId();
    }



    // Helper methods for metadata     
    private string GetPlatform() => RuntimeInformation.OSDescription;
    private string GetDeviceModel() => Environment.MachineName;
    private string GetLocale() => CultureInfo.CurrentCulture.Name;
    private string GetEngineVersion() => ".NET " + Environment.Version.ToString();
    private string GetUniqueId() => ""; // Godot's OS.get_unique_id() may be empty on desktop; stubbed as empty
    private string GetUserDataDir() => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);



    // Set some of the predefined metadata fields as default.
    private void InitDefaultMetadata(MetadataType type = MetadataType.Event)
    {
        if (type == MetadataType.Event)
        {
            _defaultMetadata[(int)MetadataType.Event] = new Dictionary<string, object>
            {
                { "session_id", _sessionId },
                { "platform", GetPlatform() },
                { "device_model", GetDeviceModel() },
                { "locale", GetLocale() },
                { "engine_version", GetEngineVersion() },
                { "game_version", _gameVersion }
            };
        }

        if (type == MetadataType.Score)
        {
            _defaultMetadata[(int)MetadataType.Score] = new Dictionary<string, object>
            {
                { "session_id", _sessionId }
            };
        }
    }

    // Starts a new session by calculating a new session hash
    private void ComputeNewSessionId()
    {
        _sessionId = GenerateSessionId();
    }

    // Generates a new PlayerID even if there is already one assigned
    public string ComputeNewPlayerId(bool random = false)
    {
        _playerId = GeneratePlayerId(random);
        return _playerId;
    }

    // Generates a random and unique session_id using platform and environment settings
    private string GenerateSessionId()
    {
        string source = $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}|{_playerId}";
        using SHA1 sha1 = SHA1.Create();
        byte[] hash = sha1.ComputeHash(Encoding.UTF8.GetBytes(source));
        return Convert.ToHexString(hash).ToLowerInvariant().Substring(0, 16);
    }

    // Generates and assigns a unique player_id
    private void ResolvePlayerId()
    {
        if (!string.IsNullOrEmpty(_playerId))
            return;

        // Try to reload from disk first
        if (File.Exists(_playerIdFilePath))
        {
            _playerId = File.ReadAllText(_playerIdFilePath).Trim();
        }

        if (!string.IsNullOrEmpty(_playerId))
            return;

        // Not there, create one
        _playerId = GeneratePlayerId();
        SavePlayerId();
    }

    // Persists the player_id on disk
    private void SavePlayerId()
    {
        File.WriteAllText(_playerIdFilePath, _playerId);
    }

    // Generates a player_id using environment values
    private string GeneratePlayerId(bool random = false)
    {
        string fingerprintSource = $"{GetUniqueId()}|{GetDeviceModel()}|{GetUserDataDir()}";

        if (random)
        {
            Random rnd = new Random();
            int randValue = rnd.Next();
            fingerprintSource = $"{fingerprintSource}|{randValue}";
        }

        using SHA1 sha1 = SHA1.Create();
        byte[] digest = sha1.ComputeHash(Encoding.UTF8.GetBytes(fingerprintSource));
        string fingerprint = Convert.ToHexString(digest).ToLowerInvariant();
        return fingerprint; // Full fingerprint as per GDScript (substr commented out)
    }

    // Generates the HMAC hash for the HTTP call
    private string GenerateHmacSignature(string method, string endpoint, string querystring, string body, string gameSecret, string timestamp)
    {
        // Build the canonical input message
        string inputStr = $"{method.ToUpperInvariant()}/{endpoint}{(string.IsNullOrEmpty(querystring) ? "" : "?" + querystring)}{body}{gameSecret}{timestamp}";

        byte[] keyBytes = Encoding.UTF8.GetBytes(gameSecret);
        byte[] msgBytes = Encoding.UTF8.GetBytes(inputStr);

        using HMACSHA256 hmac = new HMACSHA256(keyBytes);
        byte[] hmacBytes = hmac.ComputeHash(msgBytes);
        return Convert.ToHexString(hmacBytes).ToLowerInvariant();
    }

    // Called when the HTTP request finishes (handled in async tasks)
    private async Task HandleRequestCompleted(HttpResponseMessage response, string requestType)
    {
        string bodyText = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"GLX: Error {(int)response.StatusCode}: {bodyText}");
            OnRequestCompleted?.Invoke(false, requestType, $"HTTP error: {(int)response.StatusCode}. {bodyText}");
            return;
        }

        // At this point, request is OK
        if (requestType == "fetch_scores")
        {
            try
            {
                JsonDocument doc = JsonDocument.Parse(bodyText);
                if (!doc.RootElement.TryGetProperty("data", out var dataElement))
                {
                    throw new Exception("Missing 'data' property");
                }
                List<Dictionary<string, object>> scores = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(dataElement.GetRawText());
                OnScoresFetched?.Invoke(scores);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"GLX: Failed to parse JSON response after calling 'fetch_scores': {ex.Message}");
                OnRequestCompleted?.Invoke(false, requestType, "Failed to parse JSON response after calling 'fetch_scores'");
                return;
            }
        }

        if (_showDebugMessages)
        {
            Console.WriteLine("GLX: request sent successfully.");
        }
        OnRequestCompleted?.Invoke(true, requestType, "OK");
    }

    // Checks if the required params (creds) were set
    private void ValidateCredentialsSet()
    {
        _credentialsSet = true;
        if (string.IsNullOrEmpty(_gameKey))
        {
            Console.Error.WriteLine("GLX: Game key is not set.");
            _credentialsSet = false;
        }
        if (string.IsNullOrEmpty(_gameSecret))
        {
            Console.Error.WriteLine("GLX: Game secret is not set.");
            _credentialsSet = false;
        }
    }

    // Basic URL encoding for query components
    private string UrlEncode(string value)
    {
        return Uri.EscapeDataString(value);
    }

    // Mandatory initialization method. Must be called before any other methods can be called.
    public void Init(bool force = false)
    {
        if (_initialized && !force)
            return;

        ValidateCredentialsSet();
        _initialized = true;

        if (_credentialsSet)
        {
            ComputeNewSessionId();
            InitDefaultMetadata();
            if (_autoSessionStart)
            {
                LogEvent("session_start");
            }
        }
    }

    // Developer override for player ID
    public void SetPlayerId(string customId)
    {
        _playerId = customId.Trim();
        SavePlayerId();
    }

    // Developer option to set (override) the entire default metadata (default metadata persists across calls)
    public void SetDefaultMetadata(MetadataType type, Dictionary<string, object> meta)
    {
        _defaultMetadata[(int)type].Clear();
        foreach (var kvp in meta)
        {
            _defaultMetadata[(int)type][kvp.Key] = kvp.Value;
        }
    }

    // Developer option to add or override specific (default) metadata values (default metadata persists across calls)
    public void SetDefaultMetadataField(MetadataType type, string key, object value)
    {
        _defaultMetadata[(int)type][key] = value;
    }

    // Resets the default_metadata dictionary to their default values
    public void ResetDefaultMetadata(MetadataType type)
    {
        InitDefaultMetadata(type);
    }

    // Prints the current default_metadata dictionary
    public void PrintDefaultMetadata(MetadataType type)
    {
        string output = "{ ";
        foreach (var kvp in _defaultMetadata[(int)type])
        {
            output += $"{kvp.Key}: {kvp.Value}, ";
        }
        output = output.TrimEnd(',', ' ') + " }";
        Console.WriteLine(output);
    }

    // Lets the user enable/disable debug (print) messages. Can also be set in the constructor
    public void SetDebugMessages(bool show = true)
    {
        _showDebugMessages = show;
    }

    // Log a game EVENT
    public void LogEvent(string eventType, Dictionary<string, object> metadata = null)
    {
        if (metadata == null) metadata = new Dictionary<string, object>();
        const string requestType = "log_event";

        if (!_initialized)
        {
            string msg = "GLX: Init() must be called before logging events.";
            Console.Error.WriteLine(msg);
            OnRequestCompleted?.Invoke(false, requestType, msg);
            return;
        }

        if (!_credentialsSet)
        {
            string msg = "GLX: Credentials are not set or invalid.";
            Console.Error.WriteLine(msg);
            OnRequestCompleted?.Invoke(false, requestType, msg);
            return;
        }

        var mergedMetadata = new Dictionary<string, object>(_defaultMetadata[(int)MetadataType.Event]);
        foreach (var kvp in metadata)
        {
            mergedMetadata[kvp.Key] = kvp.Value;
        }

        var payload = new Dictionary<string, object>
        {
            { "event_type", eventType },
            { "player_id", _playerId },
            { "metadata", mergedMetadata }
        };

        string jsonBody = JsonSerializer.Serialize(payload);
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // HMAC signature
        string method = "POST";
        string endpoint = EventApiEndpoint;
        string querystring = ""; // No query params for this POST
        string signature = GenerateHmacSignature(method, endpoint, querystring, jsonBody, _gameSecret, timestamp.ToString());

        // Prepare headers
        var headers = new List<string>
        {
            "Content-Type: application/json",
            $"GLX-GameKey: {_gameKey}",
            $"GLX-Timestamp: {timestamp}",
            $"GLX-Signature: {signature}"
        };

        // Start async request
        _lastRequestType = requestType;
        TrackPendingRequest(SendPostRequestAsync(ApiBaseUrl + EventApiEndpoint, jsonBody, headers, _lastRequestType));
    }

    // Helper to send POST request async
    private async Task SendPostRequestAsync(string url, string jsonBody, List<string> headers, string requestType)
    {
        try
        {
            var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };

            foreach (var header in headers)
            {
                var parts = header.Split(new[] { ": " }, 2, StringSplitOptions.None);
                if (parts.Length == 2 && parts[0] != "Content-Type")
                {
                    request.Headers.Add(parts[0], parts[1]);
                }
            }

            var response = await _httpClient.SendAsync(request);
            await HandleRequestCompleted(response, requestType);
        }
        catch (Exception ex)
        {
            OnRequestCompleted?.Invoke(false, requestType, $"Network error: {ex.Message}");
        }
    }

    private void TrackPendingRequest(Task requestTask)
    {
        lock (_pendingRequestsLock)
        {
            _pendingRequests.Add(requestTask);
        }

        _ = requestTask.ContinueWith(task =>
        {
            lock (_pendingRequestsLock)
            {
                _pendingRequests.Remove(task);
            }
        }, TaskScheduler.Default);
    }

    // Waits for all currently in-flight requests to complete.
    public Task WaitForPendingRequestsAsync()
    {
        Task[] snapshot;
        lock (_pendingRequestsLock)
        {
            snapshot = _pendingRequests.ToArray();
        }

        return snapshot.Length == 0 ? Task.CompletedTask : Task.WhenAll(snapshot);
    }

    // Logs an event of the type 'screen_view'
    public void LogScreenView(string screenId, Dictionary<string, object> extra = null)
    {
        if (extra == null) extra = new Dictionary<string, object>();
        var meta = new Dictionary<string, object> { { "screen_id", screenId } };
        foreach (var kvp in extra) meta[kvp.Key] = kvp.Value;
        LogEvent("screen_view", meta);
    }

    // Logs an event of the type 'level_start'
    public void LogLevelStart(string level, string difficulty = "", Dictionary<string, object> extra = null)
    {
        if (extra == null) extra = new Dictionary<string, object>();
        var meta = new Dictionary<string, object> { { "level", level } };
        if (!string.IsNullOrEmpty(difficulty)) meta["difficulty"] = difficulty;
        foreach (var kvp in extra) meta[kvp.Key] = kvp.Value;
        LogEvent("level_start", meta);
    }

    // Logs an event of the type 'level_complete'
    public void LogLevelComplete(string level, bool success = true, Dictionary<string, object> extra = null)
    {
        if (extra == null) extra = new Dictionary<string, object>();
        var meta = new Dictionary<string, object> { { "level_id", level }, { "success", success } };
        foreach (var kvp in extra) meta[kvp.Key] = kvp.Value;
        LogEvent("level_complete", meta);
    }

    // Logs an event of the type 'item_acquired'
    public void LogItemAcquired(string itemId, string itemName = "", string method = "pickup", int quantity = 1, Dictionary<string, object> extra = null)
    {
        if (extra == null) extra = new Dictionary<string, object>();
        var meta = new Dictionary<string, object> { { "item_id", itemId }, { "acquisition_method", method }, { "quantity", quantity } };
        if (!string.IsNullOrEmpty(itemName)) meta["item_name"] = itemName;
        foreach (var kvp in extra) meta[kvp.Key] = kvp.Value;
        LogEvent("item_acquired", meta);
    }

    // Logs an event of the type 'session_start'. Generates a new session_id by default.
    public void LogSessionStart(bool newSessionId = true, Dictionary<string, object> extra = null)
    {
        if (extra == null) extra = new Dictionary<string, object>();
        if (newSessionId)
        {
            ComputeNewSessionId();
            ResetDefaultMetadata(MetadataType.Event);
        }
        LogEvent("session_start", extra);
    }

    // Logs an event of the type 'session_end'
    public void LogSessionEnd(Dictionary<string, object> extra = null)
    {
        if (extra == null) extra = new Dictionary<string, object>();
        LogEvent("session_end", extra);
    }

    // Logs an event of the type 'menu_open'
    public void LogMenuOpen(string screenId, string menuId, Dictionary<string, object> extra = null)
    {
        if (extra == null) extra = new Dictionary<string, object>();
        var meta = new Dictionary<string, object> { { "screen_id", screenId }, { "menu_id", menuId } };
        foreach (var kvp in extra) meta[kvp.Key] = kvp.Value;
        LogEvent("menu_open", meta);
    }

    // Logs an event of the type 'new_game'
    public void LogNewGame(string matchId, Dictionary<string, object> extra = null)
    {
        if (extra == null) extra = new Dictionary<string, object>();
        var meta = new Dictionary<string, object> { { "match_id", matchId } };
        foreach (var kvp in extra) meta[kvp.Key] = kvp.Value;
        LogEvent("new_game", meta);
    }

    // Logs an event of the type 'game_over'
    public void LogGameOver(string matchId, float duration = 0, string level = "", Dictionary<string, object> extra = null)
    {
        if (extra == null) extra = new Dictionary<string, object>();
        var meta = new Dictionary<string, object> { { "match_id", matchId } };
        if (duration > 0) meta["duration"] = duration;
        if (!string.IsNullOrEmpty(level)) meta["level"] = level;
        foreach (var kvp in extra) meta[kvp.Key] = kvp.Value;
        LogEvent("game_over", meta);
    }

    // Logs an event of the type 'friend_invite'
    public void LogFriendInvite(string friendId, Dictionary<string, object> extra = null)
    {
        if (extra == null) extra = new Dictionary<string, object>();
        var meta = new Dictionary<string, object> { { "friend_id", friendId } };
        foreach (var kvp in extra) meta[kvp.Key] = kvp.Value;
        LogEvent("friend_invite", meta);
    }

    // Logs an event of the type 'time_spent'
    public void LogTimeSpent(string activityId, float duration, Dictionary<string, object> extra = null)
    {
        if (extra == null) extra = new Dictionary<string, object>();
        var meta = new Dictionary<string, object> { { "activity_id", activityId } };
        if (duration > 0) meta["duration"] = duration;
        foreach (var kvp in extra) meta[kvp.Key] = kvp.Value;
        LogEvent("time_spent", meta);
    }

    // Sends a game SCORE log request to the server
    public (bool, string) LogScore(int score, string name = "", Dictionary<string, object> metadata = null)
    {
        if (metadata == null) metadata = new Dictionary<string, object>();

        if (!_initialized)
        {
            string msg = "GLX: Init() must be called before logging events.";
            Console.Error.WriteLine(msg);
            OnRequestCompleted?.Invoke(false, "log_score", msg);
            return (false, msg);
        }

        if (!_credentialsSet)
        {
            string msg = "GLX: Credentials are not set or invalid.";
            Console.Error.WriteLine(msg);
            OnRequestCompleted?.Invoke(false, "log_score", msg);
            return (false, msg);
        }

        var mergedMetadata = new Dictionary<string, object>(_defaultMetadata[(int)MetadataType.Score]);
        foreach (var kvp in metadata)
        {
            mergedMetadata[kvp.Key] = kvp.Value;
        }

        var payload = new Dictionary<string, object>
        {
            { "player_id", _playerId },
            { "player_name", name },
            { "score", score },
            { "metadata", mergedMetadata }
        };

        string jsonBody = JsonSerializer.Serialize(payload);
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // HMAC signature
        string method = "POST";
        string endpoint = ScoreApiEndpoint;
        string querystring = ""; // No query params for this POST
        string signature = GenerateHmacSignature(method, endpoint, querystring, jsonBody, _gameSecret, timestamp.ToString());

        // Prepare headers
        var headers = new List<string>
        {
            "Content-Type: application/json",
            $"GLX-GameKey: {_gameKey}",
            $"GLX-Timestamp: {timestamp}",
            $"GLX-Signature: {signature}"
        };

        // Start async request
        _lastRequestType = "log_score";
        if (_showDebugMessages)
        {
            Console.WriteLine("request sent to 'scores' endpoint:");
            Console.WriteLine("\t\t" + jsonBody);
        }
        TrackPendingRequest(SendPostRequestAsync(ApiBaseUrl + endpoint, jsonBody, headers, _lastRequestType));

        return (true, "Save score request sent to Gamelytics");
    }

    // Fetches the score table for the current game and/or specific player
    public void FetchScores(string playerId = "", int page = 1, int rows = 20, string timeRange = "all_time")
    {
        const string requestType = "fetch_scores";
        if (!_initialized)
        {
            string msg = "GLX: Init() must be called before fetching scores.";
            Console.Error.WriteLine(msg);
            OnRequestCompleted?.Invoke(false, requestType, msg);
            return;
        }

        if (!_credentialsSet)
        {
            string msg = "GLX: Credentials are not set or invalid.";
            Console.Error.WriteLine(msg);
            OnRequestCompleted?.Invoke(false, requestType, msg);
            return;
        }

        var queryDict = new Dictionary<string, object>
        {
            { "page", page },
            { "rows", rows },
            { "time_range", timeRange }
        };

        if (!string.IsNullOrEmpty(playerId))
        {
            queryDict["player_id"] = playerId;
        }

        // Sort keys for consistent ordering
        var sortedKeys = new List<string>(queryDict.Keys);
        sortedKeys.Sort();

        var queryComponents = new List<string>();
        foreach (var key in sortedKeys)
        {
            string value = queryDict[key].ToString();
            queryComponents.Add($"{UrlEncode(key)}={UrlEncode(value)}");
        }

        string queryString = string.Join("&", queryComponents);
        string endpoint = ScoreApiEndpoint;
        string fullUrl = $"{ApiBaseUrl}{endpoint}?{queryString}";

        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string method = "GET";
        string body = ""; // GET requests have no body
        string signature = GenerateHmacSignature(method, endpoint, queryString, body, _gameSecret, timestamp.ToString());

        // Prepare headers
        var headers = new List<string>
        {
            "Content-Type: application/json",
            $"GLX-GameKey: {_gameKey}",
            $"GLX-Timestamp: {timestamp}",
            $"GLX-Signature: {signature}"
        };

        // Start async request
        _lastRequestType = requestType;
        TrackPendingRequest(SendGetRequestAsync(fullUrl, headers, _lastRequestType));
    }

    // Helper to send GET request async
    private async Task SendGetRequestAsync(string url, List<string> headers, string requestType)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);

            foreach (var header in headers)
            {
                var parts = header.Split(new[] { ": " }, 2, StringSplitOptions.None);
                if (parts.Length == 2 && parts[0] != "Content-Type")
                {
                    request.Headers.Add(parts[0], parts[1]);
                }
            }

            var response = await _httpClient.SendAsync(request);
            await HandleRequestCompleted(response, requestType);
        }
        catch (Exception ex)
        {
            OnRequestCompleted?.Invoke(false, requestType, $"Network error: {ex.Message}");
        }
    }
}
