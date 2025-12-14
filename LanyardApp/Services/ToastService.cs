using LanyardData.DTO;

public class ToastService
{
    public event Action? OnShow;
    public event Action<int>? OnSetErrorLevel;
    public event Action<string>? OnSetTitle;
    public event Action<string>? OnSetMessage;

    public void Show()
    {
        OnShow?.Invoke();
    }

    public void SetLevel(int level)
    {
        OnSetErrorLevel?.Invoke(level);
    }

    public void SetTitle(string Title)
    {
        OnSetTitle?.Invoke(Title);
    }

    public void SetMessage(string Message)
    {
        OnSetMessage?.Invoke(Message);
    }

    public void ShowError(string? title, string message)
    {
        SetLevel(ToastErrorLevels.Error);

        if (title is not null)
        {
            SetTitle(title);
        }
        
        SetMessage(message);
        
        Show();
    }

    public void ShowSuccess(string? title, string message)
    {
        SetLevel(ToastErrorLevels.Success);

        if (title is not null)
        {
            SetTitle(title);
        }
        
        SetMessage(message);
        
        Show();
    }
}
