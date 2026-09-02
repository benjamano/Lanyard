namespace Lanyard.App.Components.Manager;

/// <summary>
/// What the change-payout dialog hands back: the new account, and the second-factor code proving
/// who asked for it. Carried together because the service verifies them together - a code with no
/// change to make, or a change with no code, is not a valid request.
/// </summary>
public record PayoutAccountChange(string AccountId, string Code);
