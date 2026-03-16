# Gamelytics.io Client Library for .NET

[![NuGet Version](https://img.shields.io/nuget/v/Gamelytics.Client.svg)](https://www.nuget.org/packages/Gamelytics.Client/) [![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

## Overview

The Gamelytics (GLX) Client is a C# (.NET 9) library for integrating with the Gamelytics.io API. It allows you to log game events, submit player scores, and fetch leaderboards from your .NET applications. This library is a port from the original GDScript version designed for Godot, adapted for general .NET use (e.g., Unity, console apps, or web services).

Key features:
- Log custom and predefined game events (e.g., session start, level complete).
- Submit player scores with metadata.
- Fetch score leaderboards with filtering options.
- Persistent player ID management using a simple `.cfg` file.
- HMAC-secured API requests for authentication.
- Event-based callbacks for asynchronous operations.

This library uses `System.Net.Http` for API calls and handles asynchronous requests with `Task`s.

## Installation

### Via NuGet
Install the package via NuGet Package Manager:

```
dotnet add package GlxClient
```

Or via the .NET CLI:

```
dotnet add package GlxClient
```

### Manual Installation
1. Download the `GlxClient.cs` file from the repository.
2. Add it to your .NET project.
3. Ensure your project targets .NET 9 or compatible (e.g., .NET 8+).

Dependencies: None (uses built-in .NET libraries like `System.Text.Json` and `System.Security.Cryptography`).

## Configuration

### Constructor Parameters
Initialize the `GlxClient` with required credentials and optional settings:

```csharp
var client = new GlxClient(
    gameKey: "your-game-key",          // Required: Obtained from Gamelytics dashboard
    gameSecret: "your-game-secret",    // Required: Obtained from Gamelytics dashboard
    gameVersion: "1.0.0",              // Optional: Your game's version (default: "1.0.0")
    autoSessionStart: true,            // Optional: Automatically log "session_start" on init (default: false)
    showDebugMessages: true,           // Optional: Enable console debug output (default: false)
    playerIdFilePath: "path/to/glx_player_id.cfg"  // Optional: Custom path for player ID storage (default: AppData folder)
);
```

- **gameKey** and **gameSecret**: API credentials from your Gamelytics account. These are used for HMAC signing of requests.
- **playerIdFilePath**: Path to a `.cfg` file for persisting the player ID. This is a simple text file containing the player ID string. If not provided, it defaults to `%APPDATA%/glx_player_id.cfg` on Windows (or equivalent on other OS). The library automatically loads/saves the player ID here to maintain consistency across sessions.

After instantiation, call `Init()` to validate credentials and start a session:

```csharp
client.Init();  // Or client.Init(force: true) to reinitialize
```

If credentials are invalid, errors will be logged to the console.

### Test Mode
Enable Test Mode to validate requests without storing events or scores in the database. This is useful for development, testing, and CI/CD pipelines:

```csharp
client.TestMode = true;

client.LogEvent("test_event");  // Validated but not stored
client.LogScore(100, "TestPlayer");  // Validated but not stored
```

When `TestMode` is `true`, the client adds a `GLX-TestMode: true` header to both LogEvent and LogScore requests. The API fully processes and validates these requests but discards them after processing.

### Player ID Management (.cfg File)
The library generates a unique player ID based on device fingerprints (e.g., machine name, OS details). This ID is persisted in a `.cfg` file for cross-session continuity.

- **File Format**: A plain text file containing only the player ID string (e.g., "a1b2c3d4e5f67890").
- **Loading/Saving**: Automatically handled on initialization. If the file exists, the ID is loaded; otherwise, a new one is generated and saved.
- **Custom ID**: Override with `client.SetPlayerId("custom-id")` – this saves to the `.cfg` file.
- **New ID**: Generate a fresh ID with `client.ComputeNewPlayerId(random: true)` (adds randomness).

Ensure your app has read/write permissions to the file path. On mobile or restricted environments, provide a custom path.

## Usage

### Events (Callbacks)
Subscribe to events for handling API responses:

```csharp
client.OnRequestCompleted += (success, requestType, message) => {
    Console.WriteLine($"Request '{requestType}' completed: {success} - {message}");
};

client.OnScoresFetched += (scores) => {
    foreach (var score in scores) {
        Console.WriteLine($"Player: {score["player_name"]}, Score: {score["score"]}");
    }
};
```

- `OnRequestCompleted`: Fired after any API request (log event, log score, etc.), including validation failures.
- `OnScoresFetched`: Fired after fetching scores, providing a list of score dictionaries.

### Logging Events
Log custom or predefined game events. All logs include default metadata (e.g., session ID, platform, game version) which can be customized.

#### Custom Event
```csharp
client.LogEvent("custom_event", new Dictionary<string, object> {
    { "key1", "value1" },
    { "key2", 42 }
});
```

#### Predefined Events
Use convenience methods for common events:

```csharp
client.LogSessionStart(newSessionId: true);  // Starts a new session
client.LogScreenView("main_menu");
client.LogLevelStart("level_1", difficulty: "hard");
client.LogLevelComplete("level_1", success: true);
client.LogItemAcquired("sword", itemName: "Excalibur", method: "quest", quantity: 1);
client.LogSessionEnd();
client.LogMenuOpen("settings", "options_menu");
client.LogNewGame("match_123");
client.LogGameOver("match_123", duration: 300.5f, level: "boss");
client.LogFriendInvite("friend456");
client.LogTimeSpent("tutorial", 120.0f);

// In short-lived apps, wait so async callbacks can fire before process exit.
await client.WaitForPendingRequestsAsync();
```

Extra metadata can be passed as a dictionary to any predefined method.

### Metadata Customization
Modify default metadata persisted across calls:

```csharp
// Set a single field
client.SetDefaultMetadataField(GlxClient.MetadataType.Event, "custom_field", "value");

// Replace entire metadata
client.SetDefaultMetadata(GlxClient.MetadataType.Score, new Dictionary<string, object> {
    { "session_id", client._sessionId },  // Note: _sessionId is private; access via reflection or extend class if needed
    { "extra", "data" }
});

// Reset to defaults
client.ResetDefaultMetadata(GlxClient.MetadataType.Event);

// Print current metadata
client.PrintDefaultMetadata(GlxClient.MetadataType.Score);
```

Refer to Gamelytics API docs for valid metadata fields.

### Scores
#### Log a Score
```csharp
var (success, message) = client.LogScore(1000, name: "PlayerOne", metadata: new Dictionary<string, object> {
    { "level", "expert" }
});
Console.WriteLine(message);  // "Save score request sent to Gamelytics"
```

Returns a tuple `(bool success, string message)`.

#### Fetch Scores
```csharp
client.FetchScores(playerId: "specific-player-id", page: 1, rows: 10, timeRange: "week");
// Results arrive via OnScoresFetched event
```

- `timeRange`: "all_time", "day", "week", "month".
- If `playerId` is empty, fetches global leaderboard.

### Debug Mode
Enable/disable console logging:

```csharp
client.SetDebugMessages(true);
```

## Error Handling
- API errors trigger `OnRequestCompleted` with `success: false`.
- Console errors for invalid credentials or network issues.
- Ensure HTTPS is available; the API uses TLS.

## Support
For issues, reach out at: contact@gamelytics.io
