namespace Lanyard.Infrastructure.Enum;

// Stored, so never renumber. Channels arrive with the next chat work; the values are reserved here.
public enum ChatConversationKind
{
    Direct = 0,
    Group = 1,
    LocationChannel = 2,
    CompanyChannel = 3
}
