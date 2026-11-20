using Lanyard.Infrastructure.DTO.Files;

namespace Lanyard.App.Components.Files.Browser;

public enum FilesView
{
    Folder = 0,
    Recent = 1,
    Type = 2,
    Search = 3,
    Bin = 4,
}

/// <summary>
/// Where Files is: a folder (null = the top level), a Quick access view, a search or the Recycle
/// bin. It lives in the address bar (/files?folder=...&amp;q=...), so Back, refresh and links all work.
/// </summary>
public sealed record FilesLocation(FilesView View, Guid? FolderId = null, FileTypeGroup? Type = null, string? Search = null)
{
    public static FilesLocation Home { get; } = new(FilesView.Folder);

    public static FilesLocation FromQuery(Guid? folder, string? view, string? type, string? search)
    {
        FileTypeGroup? group = Enum.TryParse(type, ignoreCase: true, out FileTypeGroup parsed) && Enum.IsDefined(parsed) && parsed != FileTypeGroup.Other ? parsed : null;

        if (!string.IsNullOrWhiteSpace(search))
            return new FilesLocation(FilesView.Search, Type: group, Search: search.Trim());

        return view?.ToLowerInvariant() switch
        {
            "bin" => new FilesLocation(FilesView.Bin),
            "recent" => new FilesLocation(FilesView.Recent),
            _ when group is not null => new FilesLocation(FilesView.Type, Type: group),
            _ => new FilesLocation(FilesView.Folder, folder),
        };
    }

    public string ToUrl(string basePath = "files")
    {
        List<string> query = [];

        switch (View)
        {
            case FilesView.Folder when FolderId is Guid folder:
                query.Add($"folder={folder}");
                break;
            case FilesView.Recent:
                query.Add("view=recent");
                break;
            case FilesView.Bin:
                query.Add("view=bin");
                break;
            case FilesView.Type when Type is FileTypeGroup type:
                query.Add($"type={type.ToString().ToLowerInvariant()}");
                break;
            case FilesView.Search:
                query.Add($"q={Uri.EscapeDataString(Search ?? "")}");

                if (Type is FileTypeGroup searchType)
                    query.Add($"type={searchType.ToString().ToLowerInvariant()}");

                break;
        }

        return query.Count == 0 ? basePath : $"{basePath}?{string.Join("&", query)}";
    }
}
