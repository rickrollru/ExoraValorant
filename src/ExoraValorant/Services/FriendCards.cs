using System.Text.Json;
using Exora.Core;

namespace Exora.Services;

// Player card of each friend as last seen online, kept on disk so offline friends still get an avatar.
public sealed class FriendCards
{
    private static readonly string FilePath = Path.Combine(AppInfo.CacheDir, "friend_cards.json");

    private readonly Dictionary<string, string> _cards;
    private bool _dirty;

    public FriendCards()
    {
        try
        {
            _cards = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath)) ?? new()
                : new();
        }
        catch
        {
            _cards = new();
        }
    }

    // Used from the polling loop and from background downloads.
    private readonly object _gate = new();

    public string? Get(string puuid)
    {
        lock (_gate) return _cards.TryGetValue(puuid, out var id) ? id : null;
    }

    public void Remember(string puuid, string cardId)
    {
        lock (_gate)
        {
            if (_cards.TryGetValue(puuid, out var old) && old == cardId) return;
            _cards[puuid] = cardId;
            _dirty = true;
        }
    }

    public void Save()
    {
        string json;
        lock (_gate)
        {
            if (!_dirty) return;
            _dirty = false;
            json = JsonSerializer.Serialize(_cards);
        }
        try
        {
            File.WriteAllText(FilePath, json);
        }
        catch (Exception ex)
        {
            Log.Warn("Failed to save friend cards: " + ex.Message);
        }
    }
}
