using System.Reflection;
using Lanyard.Infrastructure.Models;

namespace Lanyard.App.Components.Dashboards.Designer;

// Copies a widget with all of its settings, as a new widget (new Id, not yet saved). Copies every
// public read/write property rather than listing them per type, so a setting added to a widget
// later is duplicated without anyone remembering to add it here.
public static class DashboardWidgetCloner
{
    private static readonly HashSet<string> NotCopied =
    [
        nameof(DashboardWidget.Id),
        nameof(DashboardWidget.DashboardId),
        nameof(DashboardWidget.Dashboard),
        nameof(DashboardWidget.PhoneOrder)
    ];

    public static DashboardWidget Clone(DashboardWidget source)
    {
        DashboardWidget copy = NewOfSameType(source);

        foreach (PropertyInfo property in CopiedProperties(source.GetType()))
        {
            if (!NotCopied.Contains(property.Name))
            {
                property.SetValue(copy, property.GetValue(source));
            }
        }

        copy.IsActive = true;

        return copy;
    }

    // An exact copy, Id and all, for the designer's undo history.
    public static DashboardWidget CloneExact(DashboardWidget source)
    {
        DashboardWidget copy = NewOfSameType(source);

        foreach (PropertyInfo property in CopiedProperties(source.GetType()))
        {
            property.SetValue(copy, property.GetValue(source));
        }

        return copy;
    }

    // Every public read/write value a widget has, leaving out the navigation back to its dashboard.
    public static IEnumerable<PropertyInfo> CopiedProperties(Type widgetType) =>
        widgetType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0 && p.Name != nameof(DashboardWidget.Dashboard));

    private static DashboardWidget NewOfSameType(DashboardWidget source) =>
        DashboardWidgetCatalog.Get(source.Type)?.Create()
            ?? (DashboardWidget)Activator.CreateInstance(source.GetType())!;
}
