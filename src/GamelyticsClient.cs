using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;


namespace Gamelytics.Client;

/// <summary>
/// Gamelytics client for logging game events and scores.
/// </summary>
public class GlxClient
{
    /// <summary>
    /// Fired when scores are successfully fetched from the server.
    /// </summary>
    public event Action<List<Dictionary<string, object>>>? OnScoresFetched;

    /// <summary>
    /// Fired when a request completes (success or failure).
    /// </summary>
    public event Action<bool, string, string>? OnRequestCompleted;

    // Constants
    private const string ApiBaseUrl = "https://api.gamelytics.io/";
    private const string EventApiEndpoint = "events";
    private const string ScoreApiEndpoint = "scores";

    /// <summary>
    /// Metadata type enumeration for event or score metadata.
    /// </summary>
    public enum MetadataType
    {
        /// <summary>Event metadata type.</summary>
        Event,
        /// <summary>Score metadata type.</summary>
        Score
    }

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

    /// <summary>
    /// Gets or sets whether Test Mode is enabled. When true, events and scores are validated
    /// but not stored in the database. Useful for development and testing.
    /// </summary>
    public bool TestMode { get; set; } = false;

    private string _playerId = "";
    private readonly string _playerIdFilePath;
    private string _sessionId = "";
    private string _lastRequestType = "";
    private readonly object _pendingRequestsLock = new();
    private readonly HashSet<Task> _pendingRequests = new();



    // Constructor
    /// <summary>
    /// Initializes a new instance of the Gamelytics client.
    /// </summary>
    /// <param name="gameKey">The game key for authentication.</param>
    /// <param name="gameSecret">The game secret for HMAC signature generation.</param>
    /// <param name="gameVersion">The version of the game (default: "1.0.0").</param>
    /// <param name="autoSessionStart">Whether to automatically log session_start on Init (default: false).</param>
    /// <param name="showDebugMessages">Whether to print debug messages to console (default: false).</param>
    /// <param name="playerIdFilePath">Custom file path for storing player ID (default: AppData directory).</param>
    public GlxClient(string gameKey, string gameSecret, string gameVersion = "1.0.0", bool autoSessionStart = false, bool showDebugMessages = false, string? playerIdFilePath = null)
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

    /// <summary>
    /// Generates and assigns a new player ID, optionally with a random component.
    /// </summary>
    /// <param name="random">Whether to include a random value in the ID generation.</param>
    /// <returns>The newly generated player ID.</returns>
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
                var scores = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(dataElement.GetRawText());
                if (scores != null)
                {
                    OnScoresFetched?.Invoke(scores);
                }
                else
                {
                    throw new Exception("Failed to deserialize scores");
                }
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

    /// <summary>
    /// Mandatory initialization method. Must be called before any other methods.
    /// </summary>
    /// <param name="force">Whether to reinitialize even if already initialized.</param>
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

    /// <summary>
    /// Sets a custom player ID.
    /// </summary>
    /// <param name="customId">The custom player ID to set.</param>
    public void SetPlayerId(string customId)
    {
        _playerId = customId.Trim();
        SavePlayerId();
    }

    /// <summary>
    /// Sets the entire default metadata dictionary for a metadata type.
    /// </summary>
    /// <param name="type">The metadata type (Event or Score).</param>
    /// <param name="meta">The metadata dictionary to set.</param>
    public void SetDefaultMetadata(MetadataType type, Dictionary<string, object> meta)
    {
        _defaultMetadata[(int)type].Clear();
        foreach (var kvp in meta)
        {
            _defaultMetadata[(int)type][kvp.Key] = kvp.Value;
        }
    }

    /// <summary>
    /// Adds or overrides a specific default metadata field.
    /// </summary>
    /// <param name="type">The metadata type (Event or Score).</param>
    /// <param name="key">The metadata key.</param>
    /// <param name="value">The metadata value.</param>
    public void SetDefaultMetadataField(MetadataType type, string key, object value)
    {
        _defaultMetadata[(int)type][key] = value;
    }

    /// <summary>
    /// Resets the default metadata for a metadata type to its initial values.
    /// </summary>
    /// <param name="type">The metadata type to reset.</param>
    public void ResetDefaultMetadata(MetadataType type)
    {
        InitDefaultMetadata(type);
    }

    /// <summary>
    /// Prints the current default metadata dictionary to console.
    /// </summary>
    /// <param name="type">The metadata type to print.</param>
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

    /// <summary>
    /// Enables or disables debug (print) messages to console.
    /// </summary>
    /// <param name="show">Whether to show debug messages.</param>
    public void SetDebugMessages(bool show = true)
    {
        _showDebugMessages = show;
    }

    /// <summary>
    /// Logs a custom game event with optional metadata.
    /// </summary>
    /// <param name="eventType">The type of event to log.</param>
    /// <param name="metadata">Optional metadata to include with the event.</param>
    public void LogEvent(string eventType, Dictionary<string, object>? metadata = null)
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

        if (TestMode)
        {
            headers.Add("GLX-TestMode: true");
        }

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

    /// <summary>
    /// Waits for all currently in-flight requests to complete.
    /// </summary>
    /// <returns>A task that completes when all pending requests are done.</returns>
    public Task WaitForPendingRequestsAsync()
    {
        Task[] snapshot;
        lock (_pendingRequestsLock)
        {
            snapshot = _pendingRequests.ToArray();
        }

        return snapshot.Length == 0 ? Task.CompletedTask : Task.WhenAll(snapshot);
    }

    /// <summary>
    /// Logs a screen_view event.
    /// </summary>
    /// <param name="screenId">The ID of the screen being viewed.</param>
    /// <param name="extra">Optional additional metadata.</param>
    public void LogScreenView(string screenId, Dictionary<string, object>? extra = null)
    {
        if (extra == null) extra = new Dictionary<string, object>();
        var meta = new Dictionary<string, object> { { "screen_id", screenId } };
        foreach (var kvp in extra) meta[kvp.Key] = kvp.Value;
        LogEvent("screen_view", meta);
    }

    /// <summary>
    /// Logs a level_start event.
    /// </summary>
    /// <param name="level">The level identifier.</param>
    /// <param name="difficulty">The difficulty level.</param>
    /// <param name="extra">Optional additional metadata.</param>
    public void LogLevelStart(string level, string difficulty = "", Dictionary<string, object>? extra = null)
    {
        if (extra == null) extra = new Dictionary<string, object>();
        var meta = new Dictionary<string, object> { { "level", level } };
        if (!string.IsNullOrEmpty(difficulty)) meta["difficulty"] = difficulty;
        foreach (var kvp in extra) meta[kvp.Key] = kvp.Value;
        LogEvent("level_start", meta);
    }

    /// <summary>
    /// Logs a level_complete event.
    /// </summary>
    /// <param name="level">The level identifier.</param>
    /// <param name="success">Whether the level was completed successfully.</param>
    /// <param name="extra">Optional additional metadata.</param>
    public void LogLevelComplete(string level, bool success = true, Dictionary<string, object>? extra = null)
    {
        if (extra == null) extra = new Dictionary<string, object>();
        var meta = new Dictionary<string, object> { { "level_id", level }, { "success", success } };
        foreach (var kvp in extra) meta[kvp.Key] = kvp.Value;
        LogEvent("level_complete", meta);
    }

    /// <summary>
    /// Logs an item_acquired event.
    /// </summary>
    /// <param name="itemId">The item identifier.</param>
    /// <param name="itemName">The item name.</param>
    /// <param name="method">The acquisition method (e.g., "pickup").</param>
    /// <param name="quantity">The quantity acquired.</param>
    /// <param name="extra">Optional additional metadata.</param>
    public void LogItemAcquired(string itemId, string itemName = "", string method = "pickup", int quantity = 1, Dictionary<string, object>? extra = null)
    {
        if (extra == null) extra = new Dictionary<string, object>();
        var meta = new Dictionary<string, object> { { "item_id", itemId }, { "acquisition_method", method }, { "quantity", quantity } };
        if (!string.IsNullOrEmpty(itemName)) meta["item_name"] = itemName;
        foreach (var kvp in extra) meta[kvp.Key] = kvp.Value;
        LogEvent("item_acquired", meta);
    }

    /// <summary>
    /// Logs a session_start event. Generates a new session ID by default.
    /// </summary>
    /// <param name="newSessionId">Whether to generate a new session ID.</param>
    /// <param name="extra">Optional additional metadata.</param>
    public void LogSessionStart(bool newSessionId = true, Dictionary<string, object>? extra = null)
    {
        if (extra == null) extra = new Dictionary<string, object>();
        if (newSessionId)
        {
            ComputeNewSessionId();
            ResetDefaultMetadata(MetadataType.Event);
        }
        LogEvent("session_start", extra);
    }

    /// <summary>
    /// Logs a session_end event.
    /// </summary>
    /// <param name="extra">Optional additional metadata.</param>
    public void LogSessionEnd(Dictionary<string, object>? extra = null)
    {
        if (extra == null) extra = new Dictionary<string, object>();
        LogEvent("session_end", extra);
    }

    /// <summary>
    /// Logs a menu_open event.
    /// </summary>
    /// <param name="screenId">The screen ID.</param>
    /// <param name="menuId">The menu identifier.</param>
    /// <param name="extra">Optional additional metadata.</param>
    public void LogMenuOpen(string screenId, string menuId, Dictionary<string, object>? extra = null)
    {
        if (extra == null) extra = new Dictionary<string, object>();
        var meta = new Dictionary<string, object> { { "screen_id", screenId }, { "menu_id", menuId } };
        foreach (var kvp in extra) meta[kvp.Key] = kvp.Value;
        LogEvent("menu_open", meta);
    }

    /// <summary>
    /// Logs a new_game event.
    /// </summary>
    /// <param name="matchId">The match identifier.</param>
    /// <param name="extra">Optional additional metadata.</param>
    public void LogNewGame(string matchId, Dictionary<string, object>? extra = null)
    {
        if (extra == null) extra = new Dictionary<string, object>();
        var meta = new Dictionary<string, object> { { "match_id", matchId } };
        foreach (var kvp in extra) meta[kvp.Key] = kvp.Value;
        LogEvent("new_game", meta);
    }

    /// <summary>
    /// Logs a game_over event.
    /// </summary>
    /// <param name="matchId">The match identifier.</param>
    /// <param name="duration">The game duration in seconds.</param>
    /// <param name="level">The level information.</param>
    /// <param name="extra">Optional additional metadata.</param>
    public void LogGameOver(string matchId, float duration = 0, string level = "", Dictionary<string, object>? extra = null)
    {
        if (extra == null) extra = new Dictionary<string, object>();
        var meta = new Dictionary<string, object> { { "match_id", matchId } };
        if (duration > 0) meta["duration"] = duration;
        if (!string.IsNullOrEmpty(level)) meta["level"] = level;
        foreach (var kvp in extra) meta[kvp.Key] = kvp.Value;
        LogEvent("game_over", meta);
    }

    /// <summary>
    /// Logs a friend_invite event.
    /// </summary>
    /// <param name="friendId">The friend identifier.</param>
    /// <param name="extra">Optional additional metadata.</param>
    public void LogFriendInvite(string friendId, Dictionary<string, object>? extra = null)
    {
        if (extra == null) extra = new Dictionary<string, object>();
        var meta = new Dictionary<string, object> { { "friend_id", friendId } };
        foreach (var kvp in extra) meta[kvp.Key] = kvp.Value;
        LogEvent("friend_invite", meta);
    }

    /// <summary>
    /// Logs a time_spent event.
    /// </summary>
    /// <param name="activityId">The activity identifier.</param>
    /// <param name="duration">The duration in seconds.</param>
    /// <param name="extra">Optional additional metadata.</param>
    public void LogTimeSpent(string activityId, float duration, Dictionary<string, object>? extra = null)
    {
        if (extra == null) extra = new Dictionary<string, object>();
        var meta = new Dictionary<string, object> { { "activity_id", activityId } };
        if (duration > 0) meta["duration"] = duration;
        foreach (var kvp in extra) meta[kvp.Key] = kvp.Value;
        LogEvent("time_spent", meta);
    }

    /// <summary>
    /// Logs a score to the server.
    /// </summary>
    /// <param name="score">The score value.</param>
    /// <param name="name">The player name.</param>
    /// <param name="metadata">Optional metadata to include with the score.</param>
    /// <returns>A tuple containing success status and a message.</returns>
    public (bool, string) LogScore(int score, string name = "", Dictionary<string, object>? metadata = null)
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

        if (TestMode)
        {
            headers.Add("GLX-TestMode: true");
        }

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

    /// <summary>
    /// Fetches the leaderboard/score table from the server.
    /// </summary>
    /// <param name="playerId">Optional player ID to filter scores.</param>
    /// <param name="page">The page number of results.</param>
    /// <param name="rows">The number of rows per page.</param>
    /// <param name="timeRange">The time range filter (e.g., "all_time", "monthly").</param>
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
            string? value = queryDict[key]?.ToString();
            if (value != null)
            {
                queryComponents.Add($"{UrlEncode(key)}={UrlEncode(value)}");
            }
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
