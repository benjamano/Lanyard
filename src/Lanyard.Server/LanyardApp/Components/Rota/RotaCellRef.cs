namespace Lanyard.App.Components.Rota;

// "Add a shift here": who (null = let the manager choose, OpenShift = nobody yet) and which local day.
public record RotaCellRef(string? UserId, DateOnly Date)
{
    // Stands in for "no person" in the staff picker, whose values can't be null.
    public const string OpenShift = "__open__";
}
