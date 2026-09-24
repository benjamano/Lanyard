namespace Lanyard.App.Components.Rota;

// "Add a shift here": who (null = let the manager choose) and which local day.
public record RotaCellRef(string? UserId, DateOnly Date);
