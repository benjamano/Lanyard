namespace Lanyard.Shared.Enum;

/// <summary>
/// The fourteen allergens that must be declared under the Food Information Regulations 2014.
///
/// A fixed legal list, not a free-text field: selling food at a distance requires these to be
/// declared before the customer buys, and prose nobody parses cannot be shown consistently on a
/// menu, carried onto a kitchen ticket, or checked for completeness.
///
/// Stored as a bit flag so an item's declaration is one column rather than a join table. Fourteen
/// members leaves plenty of headroom in an int; the list is set by regulation and does not grow
/// on a whim.
/// </summary>
[Flags]
public enum Allergen
{
    None = 0,
    Celery = 1 << 0,
    CerealsContainingGluten = 1 << 1,
    Crustaceans = 1 << 2,
    Eggs = 1 << 3,
    Fish = 1 << 4,
    Lupin = 1 << 5,
    Milk = 1 << 6,
    Molluscs = 1 << 7,
    Mustard = 1 << 8,
    TreeNuts = 1 << 9,
    Peanuts = 1 << 10,
    SesameSeeds = 1 << 11,
    Soybeans = 1 << 12,
    SulphurDioxideAndSulphites = 1 << 13
}

public static class AllergenExtensions
{
    /// <summary>Every allergen in the order they are conventionally listed, for building a picker.</summary>
    public static readonly Allergen[] All =
    [
        Allergen.Celery,
        Allergen.CerealsContainingGluten,
        Allergen.Crustaceans,
        Allergen.Eggs,
        Allergen.Fish,
        Allergen.Lupin,
        Allergen.Milk,
        Allergen.Molluscs,
        Allergen.Mustard,
        Allergen.TreeNuts,
        Allergen.Peanuts,
        Allergen.SesameSeeds,
        Allergen.Soybeans,
        Allergen.SulphurDioxideAndSulphites
    ];

    /// <summary>
    /// What a customer should actually read. The enum names are C#-shaped; these are the words
    /// used on menus and in the regulations.
    /// </summary>
    public static string DisplayName(this Allergen allergen) => allergen switch
    {
        Allergen.Celery => "Celery",
        Allergen.CerealsContainingGluten => "Gluten",
        Allergen.Crustaceans => "Crustaceans",
        Allergen.Eggs => "Eggs",
        Allergen.Fish => "Fish",
        Allergen.Lupin => "Lupin",
        Allergen.Milk => "Milk",
        Allergen.Molluscs => "Molluscs",
        Allergen.Mustard => "Mustard",
        Allergen.TreeNuts => "Tree nuts",
        Allergen.Peanuts => "Peanuts",
        Allergen.SesameSeeds => "Sesame",
        Allergen.Soybeans => "Soya",
        Allergen.SulphurDioxideAndSulphites => "Sulphites",
        _ => allergen.ToString()
    };

    public static IEnumerable<Allergen> Split(this Allergen allergens) =>
        All.Where(a => allergens.HasFlag(a));

    public static string ToDisplayList(this Allergen allergens) =>
        string.Join(", ", allergens.Split().Select(a => a.DisplayName()));
}
