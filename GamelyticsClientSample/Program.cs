
// 1: Initialize the client with your game credentials and optional settings

using Gamelytics.Client;


var client = new GlxClient
(
    gameKey: "your-game-key",          // Required: Obtained from Gamelytics Portal
    gameSecret: "your-game-secret",    // Required: Obtained from Gamelytics Portal
    gameVersion: "1.0.0",              // Optional: Your game's version (default: "1.0.0")
    autoSessionStart: false,            // Optional: Automatically log "session_start" on init (default: false)
    showDebugMessages: true,           // Optional: Enable console debug output (default: false)
    playerIdFilePath: "./glx_player_id.cfg"  // Optional: Custom path for player ID storage (default: AppData folder)
);


// 2: Call Init() to start the client and establish connection with Gamelytics servers
client.Init(); // Or client.Init(force: true) to reinitialize


// 3: Subscribe to events for request completion and data fetching
client.OnRequestCompleted += (success, requestType, message) =>
{
    Console.WriteLine($"Request '{requestType}' completed: {success} - {message}");
};

client.OnScoresFetched += (scores) =>
{
    foreach (var score in scores)
    {
        Console.WriteLine($"Player: {score["player_name"]}, Score: {score["score"]}");
    }
};


//  4: Log custom events, scores, and screen views as needed
client.LogScreenView("main_menu");
await client.WaitForPendingRequestsAsync();



// client.LogEvent("level_up", new Dictionary<string, object>
// {
//     { "level", 5 },
//     { "character_class", "mage" }
// });

// client.LogScore(123, "Player one");



Console.WriteLine("Done !");
