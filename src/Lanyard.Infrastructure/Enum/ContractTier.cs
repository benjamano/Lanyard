namespace Lanyard.Infrastructure.Enum;

// Which tier of the company -> position -> user override chain a resolved contract value came
// from, so the editor can say "inherited from position" next to an empty field.
public enum ContractTier
{
    None = 0,
    Company = 1,
    Position = 2,
    User = 3
}
