using System.Text.Json;
using Lanyard.Shared.DTO;
using Microsoft.JSInterop;

namespace Lanyard.Reach.Web.Client.Ordering;

/// <summary>
/// One line in the basket: a dish together with the exact choices made on it.
///
/// Chips-with-beans and chips-with-peas are two different things to cook, so they are two lines,
/// not one line of quantity two. <see cref="Key"/> is what makes that distinction hold.
/// </summary>
public sealed class CartLine
{
    public int MenuItemId { get; set; }

    /// <summary>Kept sorted, so picking the same choices in a different order is the same line.</summary>
    public List<int> OptionIds { get; set; } = [];

    public int Quantity { get; set; }

    public string Key => ComposeKey(MenuItemId, OptionIds);

    public static string ComposeKey(int menuItemId, IEnumerable<int> optionIds) =>
        $"{menuItemId}:{string.Join(',', optionIds.Distinct().OrderBy(id => id))}";
}

/// <summary>
/// The customer's basket, held in the browser and mirrored to localStorage.
///
/// Keeping it client-side is the main reason this island runs on WebAssembly rather than over a
/// server circuit: a phone on venue wifi, carried around a play centre, will drop its connection.
/// A basket that lives in the browser survives that and retries on the next request, instead of
/// needing a reconnect-and-recover flow half way through an order.
///
/// Keyed by table token so two tables scanned from the same phone do not share a basket.
/// </summary>
public class CartState(IJSRuntime jsRuntime, string tableToken)
{
    private readonly IJSRuntime _jsRuntime = jsRuntime;

    // v2: the stored shape changed from a flat item-id -> quantity map when dishes gained
    // choices. A new key rather than a migration, because the cost of a customer losing a
    // half-built basket once is lower than the cost of code that reads two formats forever.
    private readonly string _storageKey = $"lanyard.cart.v2.{tableToken}";

    public Dictionary<string, CartLine> Lines { get; private set; } = [];

    public int TotalItems => Lines.Values.Sum(l => l.Quantity);

    public bool IsEmpty => Lines.Count == 0;

    /// <summary>Total of one dish across every variant of it, for the menu's "in basket" badge.</summary>
    public int QuantityOf(int menuItemId) =>
        Lines.Values.Where(l => l.MenuItemId == menuItemId).Sum(l => l.Quantity);

    public int QuantityOfLine(string key) => Lines.TryGetValue(key, out CartLine? line) ? line.Quantity : 0;

    /// <summary>
    /// Priced from the menu the phone is holding, for display only. The server reprices every
    /// line from its own rows at order time, so a stale or tampered menu here cannot change what
    /// is actually charged.
    /// </summary>
    public int TotalCents(MenuDto menu)
    {
        Dictionary<int, MenuItemDto> items = menu.Categories
            .SelectMany(c => c.Items)
            .ToDictionary(i => i.Id, i => i);

        int total = 0;

        foreach (CartLine line in Lines.Values)
        {
            if (!items.TryGetValue(line.MenuItemId, out MenuItemDto? item))
            {
                continue;
            }

            total += UnitPriceCents(item, line.OptionIds) * line.Quantity;
        }

        return total;
    }

    /// <summary>The price of one of this line: the dish plus whatever the choices add.</summary>
    public static int UnitPriceCents(MenuItemDto item, IEnumerable<int> optionIds)
    {
        HashSet<int> chosen = [.. optionIds];

        return item.PriceCents + item.OptionGroups
            .SelectMany(g => g.Options)
            .Where(o => chosen.Contains(o.Id))
            .Sum(o => o.PriceDeltaCents);
    }

    /// <summary>The chosen option names in menu order, for showing under a basket line.</summary>
    public static List<string> DescribeOptions(MenuItemDto item, IEnumerable<int> optionIds)
    {
        HashSet<int> chosen = [.. optionIds];

        return [.. item.OptionGroups
            .OrderBy(g => g.SortOrder)
            .SelectMany(g => g.Options.Where(o => chosen.Contains(o.Id)).OrderBy(o => o.SortOrder))
            .Select(o => o.Name)];
    }

    public async Task AddAsync(int menuItemId, IEnumerable<int>? optionIds = null)
    {
        List<int> options = [.. (optionIds ?? []).Distinct().OrderBy(id => id)];
        string key = CartLine.ComposeKey(menuItemId, options);

        if (Lines.TryGetValue(key, out CartLine? line))
        {
            line.Quantity++;
        }
        else
        {
            Lines[key] = new CartLine { MenuItemId = menuItemId, OptionIds = options, Quantity = 1 };
        }

        await SaveAsync();
    }

    public async Task RemoveAsync(string key)
    {
        if (!Lines.TryGetValue(key, out CartLine? line))
        {
            return;
        }

        line.Quantity--;

        if (line.Quantity <= 0)
        {
            Lines.Remove(key);
        }

        await SaveAsync();
    }

    public async Task ClearAsync()
    {
        Lines.Clear();

        await SaveAsync();
    }

    /// <summary>
    /// Drops anything no longer orderable. Called after a menu refresh so a basket cannot keep
    /// something the kitchen has since run out of - the customer sees it disappear while browsing
    /// rather than having their whole order rejected at the checkout.
    ///
    /// A line goes if the dish is gone or unavailable, and equally if any single choice on it is:
    /// running out of beans makes "chips with beans" unorderable even though chips are fine.
    /// </summary>
    public async Task<List<string>> ReconcileWithMenuAsync(MenuDto menu)
    {
        Dictionary<int, MenuItemDto> allItems = menu.Categories
            .SelectMany(c => c.Items)
            .ToDictionary(i => i.Id, i => i);

        List<string> droppedNames = [];
        List<string> droppedKeys = [];

        foreach (CartLine line in Lines.Values)
        {
            if (!allItems.TryGetValue(line.MenuItemId, out MenuItemDto? item) || !item.IsAvailable)
            {
                droppedKeys.Add(line.Key);
                droppedNames.Add(item?.Name ?? "An item");

                continue;
            }

            Dictionary<int, MenuItemOptionDto> options = item.OptionGroups
                .SelectMany(g => g.Options)
                .ToDictionary(o => o.Id, o => o);

            List<string> goneOptions = [.. line.OptionIds
                .Where(id => !options.TryGetValue(id, out MenuItemOptionDto? o) || !o.IsAvailable)
                .Select(id => options.TryGetValue(id, out MenuItemOptionDto? o) ? o.Name : "a choice")];

            if (goneOptions.Count > 0)
            {
                droppedKeys.Add(line.Key);
                droppedNames.Add($"{item.Name} with {string.Join(" and ", goneOptions)}");
            }
        }

        if (droppedKeys.Count == 0)
        {
            return [];
        }

        foreach (string key in droppedKeys)
        {
            Lines.Remove(key);
        }

        await SaveAsync();

        return droppedNames;
    }

    public List<CreateOrderLineDto> ToLines() =>
        [.. Lines.Values.Select(l => new CreateOrderLineDto
        {
            MenuItemId = l.MenuItemId,
            Quantity = l.Quantity,
            SelectedOptionIds = [.. l.OptionIds]
        })];

    public async Task LoadAsync()
    {
        try
        {
            string? json = await _jsRuntime.InvokeAsync<string?>("localStorage.getItem", _storageKey);

            if (!string.IsNullOrWhiteSpace(json))
            {
                Lines = JsonSerializer.Deserialize<Dictionary<string, CartLine>>(json) ?? [];
            }
        }
        catch
        {
            // Private browsing and storage-blocked contexts throw here, as does anything stored
            // in a shape this version no longer understands. An empty basket is a perfectly
            // usable fallback, so this must never take the ordering page down with it.
            Lines = [];
        }
    }

    private async Task SaveAsync()
    {
        try
        {
            await _jsRuntime.InvokeVoidAsync("localStorage.setItem", _storageKey, JsonSerializer.Serialize(Lines));
        }
        catch
        {
            // As above: losing persistence is a degraded experience, not a broken one.
        }
    }
}
