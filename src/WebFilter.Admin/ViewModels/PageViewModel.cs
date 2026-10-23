using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WebFilter.Admin.Services;
using WebFilter.Contracts.Admin;
using WebFilter.Contracts.Hub;

namespace WebFilter.Admin.ViewModels;

public interface INavigator
{
    void NavigateTo<TPage>() where TPage : PageViewModel;

    /// <summary>Opens the member page on <paramref name="memberId"/>.</summary>
    void ShowMember(string memberId);
}

/// <summary>A screen in the main window: loads its data through the admin pipe and reports failures in plain words.</summary>
public abstract partial class PageViewModel : ObservableObject
{
    private Task? _refresh;
    private bool _refreshing;
    private bool _refreshAgain;
    private bool _hasLoaded;
    private int _busy;

    protected PageViewModel(FamilyContext context) => Context = context ?? throw new ArgumentNullException(nameof(context));

    public FamilyContext Context { get; }

    protected IAdminClient Client => Context.Client;

    public abstract string Title { get; }

    public abstract string Intro { get; }

    /// <summary>Whether the main window reloads this page on its periodic tick while it is shown.</summary>
    public virtual bool AutoRefresh => false;

    public INavigator? Navigator { get; set; }

    /// <summary>True once a load has succeeded (until then the page shows no data rather than "nothing here").</summary>
    public bool HasLoaded
    {
        get => _hasLoaded;
        private set => SetProperty(ref _hasLoaded, value);
    }

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>Short text next to the page's name in the navigation list (e.g. a count), or null.</summary>
    [ObservableProperty]
    private string? _badge;

    /// <summary>What <see cref="Badge"/> counts, as its tooltip; null for none.</summary>
    public virtual string? BadgeToolTip => null;

    /// <summary>
    /// Loads fresh data. A call while a load runs schedules one more load after it (so the latest filter wins) and
    /// returns the running load.
    /// </summary>
    public Task RefreshAsync()
    {
        if (_refreshing)
        {
            _refreshAgain = true;
            return _refresh ?? Task.CompletedTask;
        }
        _refreshing = true;
        return _refresh = RefreshLoopAsync();
    }

    [RelayCommand]
    private Task Refresh() => RefreshAsync();

    protected abstract Task LoadAsync();

    /// <summary>Runs an admin operation; a failure becomes <see cref="ErrorMessage"/> and returns false.</summary>
    protected async Task<bool> RunAsync(Func<Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        IsBusy = ++_busy > 0;
        try
        {
            await operation();
            ErrorMessage = null;
            return true;
        }
        catch (AdminClientException ex)
        {
            ErrorMessage = Texts.Error(ex);
            return false;
        }
        finally
        {
            IsBusy = --_busy > 0;
        }
    }

    /// <summary>Sends a command that answers <see cref="AdminOk"/>; on success shows <paramref name="done"/> and reloads the page.</summary>
    protected async Task<bool> ActAsync(AdminMessage command, string done)
    {
        StatusMessage = null;
        if (!await RunAsync(() => Client.ExecuteAsync(command))) return false;
        StatusMessage = done;
        await RefreshAsync();
        return true;
    }

    /// <summary>
    /// Changes part of the settings (<see cref="FamilyContext.UpdateSettingsAsync"/>); on success shows <paramref name="done"/>
    /// and reloads the page. A refusal from the service goes to <paramref name="showError"/> when given (next to the form
    /// the parent is looking at, which may be far from the page's header); connection problems stay with the window's banner.
    /// </summary>
    internal async Task<bool> SaveSettingsAsync(Func<FamilyBundle, FamilyBundle> change, string done, Action<string?>? showError = null)
    {
        StatusMessage = null;
        showError?.Invoke(null);
        if (!await RunAsync(() => Context.UpdateSettingsAsync(change)))
        {
            if (showError is not null && ErrorMessage is { } error)
            {
                showError(error);
                ErrorMessage = null;
            }
            return false;
        }
        StatusMessage = done;
        await RefreshAsync();
        return true;
    }

    private async Task RefreshLoopAsync()
    {
        try
        {
            do
            {
                _refreshAgain = false;
                if (await RunAsync(LoadAsync)) HasLoaded = true;
            }
            while (_refreshAgain);
        }
        finally
        {
            _refreshing = false;
        }
    }
}
