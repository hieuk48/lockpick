using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lockpick.App.Services;

namespace Lockpick.App.ViewModels;

/// <summary>
/// Settings → speedrun.com: the player's name (for their runs on the leaderboards) and, if they want, their API key.
/// The key box is cleared as soon as Save is pressed, the key is checked with speedrun.com before it is kept, and
/// afterwards Lockpick only says that it has one and whose it is — it never shows it again.
/// </summary>
public sealed partial class SpeedrunAccountViewModel : ObservableObject
{
    private readonly SpeedrunAccount _account;

    public SpeedrunAccountViewModel(SpeedrunAccount account)
    {
        _account = account;
        UserName = account.User?.Name ?? string.Empty;
        _account.Changed += (_, _) => Dispatcher.UIThread.Post(Sync);
        Sync();
    }

    /// <summary>The player's speedrun.com name (no key needed for the leaderboards).</summary>
    [ObservableProperty]
    public partial string UserName { get; set; }

    /// <summary>What the player pasted; cleared when they press Save.</summary>
    [ObservableProperty]
    public partial string KeyInput { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasKey { get; private set; }

    [ObservableProperty]
    public partial string KeyStatus { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string NameStatus { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveKeyCommand), nameof(UseNameCommand))]
    public partial bool Busy { get; private set; }

    public string KeyPage => SpeedrunCom.KeyPage;

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task SaveKeyAsync()
    {
        var key = KeyInput;
        KeyInput = string.Empty; // off the screen and out of the box at once, whatever happens next
        if (string.IsNullOrWhiteSpace(key))
        {
            KeyStatus = "Paste your API key from speedrun.com first.";
            return;
        }

        Busy = true;
        KeyStatus = "Checking the key with speedrun.com…";
        try
        {
            var user = await _account.SaveKeyAsync(key);
            UserName = user.Name;
            Sync();
        }
        catch (Exception ex) when (ex is SpeedrunComException or IOException)
        {
            KeyStatus = $"Not saved: {ex.Message}";
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    private void RemoveKey()
    {
        _account.RemoveKey();
        KeyStatus = "Key removed from this PC. To stop it working anywhere, make a new one on speedrun.com (your settings → API key).";
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task UseNameAsync()
    {
        Busy = true;
        NameStatus = "Looking for you on speedrun.com…";
        try
        {
            var user = await _account.UseNameAsync(UserName);
            NameStatus = user is null
                ? (UserName.Trim().Length == 0 ? "No name: the leaderboards won't mark your runs." : $"No speedrun.com account is called {UserName.Trim()}.")
                : $"Found {user.Name}: your runs are marked on the leaderboards.";
            if (user is not null)
            {
                UserName = user.Name;
            }
        }
        catch (SpeedrunComException ex)
        {
            NameStatus = ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    private bool NotBusy() => !Busy;

    private void Sync()
    {
        HasKey = _account.HasKey;
        if (HasKey)
        {
            KeyStatus = _account.User is { } user
                ? $"Key saved for {user.Name}. It's encrypted for your Windows account on this PC and only ever sent to speedrun.com."
                : "Key saved. It's encrypted for your Windows account on this PC and only ever sent to speedrun.com.";
        }
    }
}
