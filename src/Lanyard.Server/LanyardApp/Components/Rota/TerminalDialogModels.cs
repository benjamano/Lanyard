namespace Lanyard.App.Components.Rota;

public record TerminalLocationOption(int Id, string Name);

public record PairTerminalRequest(string Name, int LocationId, bool SignOutAfterPairing);

public record EntryStaffOption(string UserId, string Name);
