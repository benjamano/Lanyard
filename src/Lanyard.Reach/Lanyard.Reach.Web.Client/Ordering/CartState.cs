using System.Text.Json;
using Lanyard.Shared.DTO;
using Microsoft.JSInterop;

namespace Lanyard.Reach.Web.Client.Ordering;

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
    private readonly string _storageKey = $"lanyard.cart.{tableToken}";

    public Dictionary<int, int> Quantities { get; private set; } = [];

    public int TotalItems => Quantities.Values.Sum();

    public bool IsEmpty => Quantities.Count == 0;

    public int QuantityOf(int menuItemId) => Quantities.TryGetValue(menuItemId, out int quantity) ? quantity : 0;

    public int TotalCents(MenuDto menu)
    {
        Dictionary<int, int> prices = menu.Categories
            .SelectMany(c => c.Items)
            .ToDictionary(i => i.Id, i => i.PriceCents);

        return Quantities.Sum(kv => prices.TryGetValue(kv.Key, out int price) ? price * kv.Value : 0);
    }

    public async Task AddAsync(int menuItemId)
    {
        Quantities[menuItemId] = QuantityOf(menuItemId) + 1;

        await SaveAsync();
    }

    public async Task RemoveAsync(int menuItemId)
    {
        int next = QuantityOf(menuItemId) - 1;

        if (next <= 0)
        {
            Quantities.Remove(menuItemId);
        }
        else
        {
            Quantities[menuItemId] = next;
        }

        await SaveAsync();
    }

    public async Task ClearAsync()
    {
        Quantities.Clear();

        await SaveAsync();
    }

    /// <summary>
    /// Drops anything no longer orderable. Called after a menu refresh so a basket cannot keep
    /// an item the kitchen has since run out of - the customer sees it disappear while browsing
    /// rather than having their whole order rejected at the checkout.
    /// </summary>
    public async Task<List<string>> ReconcileWithMenuAsync(MenuDto menu)
    {
        Dictionary<int, MenuItemDto> orderable = menu.Categories
            .SelectMany(c => c.Items)
            .Where(i => i.IsAvailable)
            .ToDictionary(i => i.Id, i => i);

        Dictionary<int, string> allById = menu.Categories
            .SelectMany(c => c.Items)
            .ToDictionary(i => i.Id, i => i.Name);

        List<int> dropped = [.. Quantities.Keys.Where(id => !orderable.ContainsKey(id))];

        if (dropped.Count == 0)
        {
            return [];
        }

        foreach (int id in dropped)
        {
            Quantities.Remove(id);
        }

        await SaveAsync();

        return [.. dropped.Select(id => allById.TryGetValue(id, out string? name) ? name : "An item")];
    }

    public List<CreateOrderLineDto> ToLines() =>
        [.. Quantities.Select(kv => new CreateOrderLineDto { MenuItemId = kv.Key, Quantity = kv.Value })];

    public async Task LoadAsync()
    {
        try
        {
            string? json = await _jsRuntime.InvokeAsync<string?>("localStorage.getItem", _storageKey);

            if (!string.IsNullOrWhiteSpace(json))
            {
                Quantities = JsonSerializer.Deserialize<Dictionary<int, int>>(json) ?? [];
            }
        }
        catch
        {
            // Private browsing and storage-blocked contexts throw here. An empty basket is a
            // perfectly usable fallback, so this must never take the ordering page down with it.
            Quantities = [];
        }
    }

    private async Task SaveAsync()
    {
        try
        {
            await _jsRuntime.InvokeVoidAsync("localStorage.setItem", _storageKey, JsonSerializer.Serialize(Quantities));
        }
        catch
        {
            // As above: losing persistence is a degraded experience, not a broken one.
        }
    }
}
