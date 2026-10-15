using Lockpick.Core.State;

namespace Lockpick.Core.Trainer;

/// <summary>
/// A game's trainer features, in terms of its profile fields. The adapter supplies it (the core never knows what a
/// field means); every write still goes through <see cref="TrainerService"/>, which validates, reads back and logs.
/// </summary>
public interface IGameTrainer
{
    /// <summary>The writable field holding the player's health.</summary>
    string HealthField { get; }

    /// <summary>The field holding the player's maximum health (the upper bound for <see cref="HealthField"/>).</summary>
    string MaxHealthField { get; }

    /// <summary>
    /// A rule for <see cref="TrainerService.SetRule"/> that keeps the equipped weapon's rounds from going down: each
    /// call returns the writes that restore the highest count seen for that weapon (reloads raise it). Each rule keeps
    /// its own memory, so create a new one each time the feature is switched on.
    /// </summary>
    Func<GameSnapshot, IReadOnlyList<FieldWrite>> CreateInfiniteAmmoRule();
}
