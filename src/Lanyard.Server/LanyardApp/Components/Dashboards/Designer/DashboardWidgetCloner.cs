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
        DashboardWidget copy = DashboardWidgetCatalog.Get(source.Type)?.Create()
            ?? (DashboardWidget)Activator.CreateInstance(source.GetType())!;

        foreach (PropertyInfo property in source.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.CanRead && property.CanWrite && !NotCopied.Contains(property.Name) && property.GetIndexParameters().Length == 0)
            {
                property.SetValue(copy, property.GetValue(source));
            }
        }

        copy.IsActive = true;

        return copy;
    }
}
