using System;

public class ToastService
{
    public event Action<string>? OnShow;
    public event Action<int>? OnSetErrorLevel;

    public void Show(string message)
    {
        OnShow?.Invoke(message);
    }

    public void SetErrorLevel(int errorLevel)
    {
        OnSetErrorLevel?.Invoke(errorLevel);
    }
}
