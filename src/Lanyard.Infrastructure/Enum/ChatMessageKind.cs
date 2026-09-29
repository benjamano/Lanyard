namespace Lanyard.Infrastructure.Enum;

// Stored, so never renumber. Cards are posted by Lanyard into a location's channel and show the
// live state of the thing they link to (LinkedEntityId).
public enum ChatMessageKind
{
    Text = 0,

    // An open shift (LinkedEntityId = the shift).
    OpenShiftCard = 1,

    // Someone looking for a swap (LinkedEntityId = the Swap claim).
    SwapRequestCard = 2
}
