using Lockpick.Platform.Windows;

namespace Lockpick.App.Services;

/// <summary>
/// The player's speedrun.com account. The API key (which lets an app submit runs as the player) is checked against
/// speedrun.com before it is kept, then kept only in <see cref="SecretStore"/> (encrypted for this Windows user),
/// never in settings, logs, captures or Lockpick's own API, and only ever sent to speedrun.com. Lockpick never shows it
/// again; the player can remove it here or revoke it on speedrun.com at any time. A name alone (no key) is enough to
/// find the player's runs on the leaderboards.
/// </summary>
public sealed class SpeedrunAccount(SpeedrunCom client, SecretStore secrets, SettingsStore settings)
{
    private const string KeyName = "speedrun-com-api-key";

    public bool HasKey => secrets.Exists(KeyName);

    /// <summary>The account Lockpick shows runs for (from the key, or the name the player typed), or null.</summary>
    public SpeedrunComUser? User => settings.Current.SpeedrunComUserId is { } id && settings.Current.SpeedrunComUser is { } name
        ? new SpeedrunComUser(id, name)
        : null;

    public event EventHandler? Changed;

    /// <summary>Checks <paramref name="key"/> with speedrun.com and keeps it when it works; returns the account.</summary>
    public async Task<SpeedrunComUser> SaveKeyAsync(string key, CancellationToken cancel = default)
    {
        key = key.Trim();
        if (key.Length is < 8 or > 250 || !key.All(char.IsAsciiLetterOrDigit))
        {
            throw new SpeedrunComException("That doesn't look like a speedrun.com API key (letters and digits, from your settings page).");
        }

        var user = await client.ProfileAsync(key, cancel).ConfigureAwait(false);
        secrets.Save(KeyName, key);
        Remember(user);
        return user;
    }

    /// <summary>Forgets the key (the name stays, for the leaderboards).</summary>
    public void RemoveKey()
    {
        secrets.Delete(KeyName);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Finds the account by name (no key needed) for the leaderboards.</summary>
    public async Task<SpeedrunComUser?> UseNameAsync(string name, CancellationToken cancel = default)
    {
        name = name.Trim();
        if (name.Length == 0)
        {
            settings.Save(settings.Current with { SpeedrunComUser = null, SpeedrunComUserId = null });
            Changed?.Invoke(this, EventArgs.Empty);
            return null;
        }

        var user = await client.FindUserAsync(name, cancel).ConfigureAwait(false);
        if (user is not null)
        {
            Remember(user);
        }

        return user;
    }

    /// <summary>The kept key, for a request that needs it (submitting a run). Null when there is none or it can't be read.</summary>
    internal string? Key() => secrets.Load(KeyName);

    private void Remember(SpeedrunComUser user)
    {
        settings.Save(settings.Current with { SpeedrunComUser = user.Name, SpeedrunComUserId = user.Id });
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
