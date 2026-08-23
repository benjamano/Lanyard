using System.Linq.Expressions;
using Microsoft.AspNetCore.Components.Forms;

namespace Lanyard.App.Extensions;

public static class EditContextExtensions
{
    public static string? GetValidationMessage<T>(this EditContext editContext, Expression<Func<T>> accessor)
    {
        FieldIdentifier fieldId = FieldIdentifier.Create(accessor);
        return editContext.GetValidationMessages(fieldId).FirstOrDefault();
    }
}
