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

    public string? Get(string puuid) => _cards.TryGetValue(puuid, out var id) ? id : null;

    public void Remember(string puuid, string cardId)
    {
        if (_cards.TryGetValue(puuid, out var old) && old == cardId) return;
        _cards[puuid] = cardId;
        _dirty = true;
    }

    public void Save()
    {
        if (!_dirty) return;
        _dirty = false;
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_cards));
        }
        catch (Exception ex)
        {
            Log.Warn("Failed to save friend cards: " + ex.Message);
        }
    }
}
