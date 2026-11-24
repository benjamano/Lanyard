using Lanyard.App.Components.Dialogs;
using Lanyard.App;
using Lanyard.Application.Services;
using Lanyard.Application.Services.Locations;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Lanyard.App.Components.Dashboards;

// Opens a widget's settings dialog, loading whatever the dialog needs to offer (kiosks, programs,
// automation rules, locations). Moved out of DashboardGrid so the grid only draws widgets; the
// designer's inspector calls this from its Settings button.
public sealed class DashboardWidgetConfigurator(
    IDialogService dialogService,
    INotificationService toastService,
    IClientService clientService,
    IProjectionProgramService projectionProgramService,
    IAutomationRuleService automationRuleService,
    ICurrentLocationContext currentLocationContext,
    ICompanyLocationService companyLocationService)
{
    private readonly IDialogService _dialogService = dialogService;
    private readonly INotificationService _toastService = toastService;
    private readonly IClientService ClientService = clientService;
    private readonly IProjectionProgramService ProjectionProgramService = projectionProgramService;
    private readonly IAutomationRuleService AutomationRuleService = automationRuleService;
    private readonly ICurrentLocationContext _currentLocationContext = currentLocationContext;
    private readonly ICompanyLocationService _companyLocationService = companyLocationService;

    // Opens the widget's settings dialog. True if the user saved changes (the dialog edits the
    // widget in place), false if they cancelled or the widget has nothing to configure.
    public async Task<bool> ConfigureAsync(DashboardWidget widget)
    {
        switch (widget)
        {
            case DigitalClockWidget clockWidget:
                return await ShowConfigureDigitalClockDialog(clockWidget);
            case ClientZoneLaserGameStatusWidget laserGameStatusWidget:
                return await ShowConfigureLaserGameStatusDialog(laserGameStatusWidget);
            case ClientZoneLaserScoreboardWidget laserScoreboardWidget:
                return await ShowConfigureLaserScoreboardDialog(laserScoreboardWidget);
            case ButtonWidget buttonWidget:
                return await ShowConfigureButtonDialog(buttonWidget);
            case MusicPlaylistSelectorWidget playlistSelectorWidget:
                return await ShowConfigureMusicPlaylistSelectorDialog(playlistSelectorWidget);
            case MusicTimelineWidget musicTimelineWidget:
                return await ShowConfigureMusicTimelineDialog(musicTimelineWidget);
            case AutomationRuleStatusWidget automationRuleStatusWidget:
                return await ShowConfigureAutomationRuleStatusDialog(automationRuleStatusWidget);
            case KioskHealthWidget kioskHealthWidget:
                return await ShowConfigureKioskHealthDialog(kioskHealthWidget);
            case OperationsAlertsWidget operationsAlertsWidget:
                return await ShowConfigureOperationsAlertsDialog(operationsAlertsWidget);
            case HallOfFameWidget hallOfFameWidget:
                return await ShowConfigureHallOfFameDialog(hallOfFameWidget);
            case MyTrainingWidget myTrainingWidget:
                return await ShowConfigureMyTrainingDialog(myTrainingWidget);
            case AnnouncementsWidget announcementsWidget:
                return await ShowConfigureAnnouncementsDialog(announcementsWidget);
            case MyShiftsWidget myShiftsWidget:
                return await ShowSimpleConfigDialog<ConfigureMyShiftsDialog>(nameof(ConfigureMyShiftsDialog.Widget), myShiftsWidget);
            case WhoIsOnTodayWidget whoIsOnTodayWidget:
                return await ShowConfigureWhoIsOnTodayDialog(whoIsOnTodayWidget);
            case ChecklistsTodayWidget checklistsTodayWidget:
                return await ShowSimpleConfigDialog<ConfigureChecklistsTodayDialog>(nameof(ConfigureChecklistsTodayDialog.Widget), checklistsTodayWidget);
            case PendingTimeOffWidget pendingTimeOffWidget:
                return await ShowSimpleConfigDialog<ConfigurePendingTimeOffDialog>(nameof(ConfigurePendingTimeOffDialog.Widget), pendingTimeOffWidget);
            case ComplianceSummaryWidget complianceSummaryWidget:
                return await ShowConfigureComplianceSummaryDialog(complianceSummaryWidget);
            case DemandStaffingWidget demandStaffingWidget:
                return await ShowConfigureDemandStaffingDialog(demandStaffingWidget);
            case WhatsOnTodayWidget whatsOnTodayWidget:
                return await ShowConfigureWhatsOnTodayDialog(whatsOnTodayWidget);
            case ProjectionStatusWidget projectionStatusWidget:
                return await ShowConfigureProjectionStatusDialog(projectionStatusWidget);
            default:
                return false;
        }
    }

    private async Task<IEnumerable<AutomationRule>> GetAutomationRulesAsync()
    {
        Result<IEnumerable<AutomationRule>> result = await AutomationRuleService.GetRulesAsync();

        if (result.IsSuccess && result.Data != null)
        {
            return result.Data;
        }

        _toastService.ShowError($"Failed to load automation rules, {result.Error}");

        return [];
    }

    private async Task<bool> ShowConfigureAutomationRuleStatusDialog(AutomationRuleStatusWidget automationRuleStatusWidget)
    {
        IEnumerable<AutomationRule> rules = await GetAutomationRulesAsync();

        DialogResult dialogResult = await _dialogService.ShowDialogAsync<ConfigureAutomationRuleStatusDialog>(options =>
        {
            options.Modal = true;
            options.Parameters.Add(nameof(ConfigureAutomationRuleStatusDialog.Widget), automationRuleStatusWidget);
            options.Parameters.Add(nameof(ConfigureAutomationRuleStatusDialog.Rules), rules);
        });

        return !dialogResult.Cancelled;
    }

    // Loads all clients for the music widgets. Deliberately unfiltered by connectivity so a
    // widget can be pre-configured for a client that is currently offline.
    private async Task<IEnumerable<Client>> GetMusicClientsAsync()
    {
        Result<IEnumerable<ClientConnectedWithCapabilitiesDTO>> result = await ClientService.GetClientsWithCapabilitiesAsync();

        if (result.IsSuccess && result.Data != null)
        {
            return result.Data
                .Cast<Client>();
        }

        _toastService.ShowError($"Failed to load clients, {result.Error}");

        return [];
    }

    private async Task<bool> ShowConfigureMusicPlaylistSelectorDialog(MusicPlaylistSelectorWidget playlistSelectorWidget)
    {
        IEnumerable<Client> clients = await GetMusicClientsAsync();

        DialogResult dialogResult = await _dialogService.ShowDialogAsync<ConfigureMusicPlaylistSelectorDialog>(options =>
        {
            options.Modal = true;
            options.Parameters.Add(nameof(ConfigureMusicPlaylistSelectorDialog.Widget), playlistSelectorWidget);
            options.Parameters.Add(nameof(ConfigureMusicPlaylistSelectorDialog.Clients), clients);
        });

        return !dialogResult.Cancelled;
    }

    private async Task<bool> ShowConfigureMusicTimelineDialog(MusicTimelineWidget musicTimelineWidget)
    {
        IEnumerable<Client> clients = await GetMusicClientsAsync();

        DialogResult dialogResult = await _dialogService.ShowDialogAsync<ConfigureMusicTimelineDialog>(options =>
        {
            options.Modal = true;
            options.Parameters.Add(nameof(ConfigureMusicTimelineDialog.Widget), musicTimelineWidget);
            options.Parameters.Add(nameof(ConfigureMusicTimelineDialog.Clients), clients);
        });

        return !dialogResult.Cancelled;
    }

    // Loads the clients eligible for the zone scoreboard/game-status widgets.
    private async Task<IEnumerable<Client>> GetZoneScoreboardClientsAsync()
    {
        Result<IEnumerable<ClientConnectedWithCapabilitiesDTO>> result = await ClientService.GetClientsWithCapabilitiesAsync();

        if (result.IsSuccess && result.Data != null)
        {
            return result.Data
                .Where(x => x.ZoneScoreboardVersion == ZoneScoreboardVersion.Begeara2)
                .Cast<Client>();
        }

        _toastService.ShowError($"Failed to load clients, {result.Error}");

        return [];
    }

    private async Task<bool> ShowConfigureHallOfFameDialog(HallOfFameWidget hallOfFameWidget)
    {
        IEnumerable<Client> clients = await GetZoneScoreboardClientsAsync();

        DialogResult dialogResult = await _dialogService.ShowDialogAsync<ConfigureHallOfFameDialog>(options =>
        {
            options.Modal = true;
            options.Parameters.Add(nameof(ConfigureHallOfFameDialog.Widget), hallOfFameWidget);
            options.Parameters.Add(nameof(ConfigureHallOfFameDialog.Clients), clients);
        });

        return !dialogResult.Cancelled;
    }

    private async Task<bool> ShowConfigureKioskHealthDialog(KioskHealthWidget kioskHealthWidget)
    {
        DialogResult dialogResult = await _dialogService.ShowDialogAsync<ConfigureKioskHealthDialog>(options =>
        {
            options.Modal = true;
            options.Parameters.Add(nameof(ConfigureKioskHealthDialog.Widget), kioskHealthWidget);
        });

        return !dialogResult.Cancelled;
    }

    private async Task<bool> ShowConfigureOperationsAlertsDialog(OperationsAlertsWidget operationsAlertsWidget)
    {
        DialogResult dialogResult = await _dialogService.ShowDialogAsync<ConfigureOperationsAlertsDialog>(options =>
        {
            options.Modal = true;
            options.Parameters.Add(nameof(ConfigureOperationsAlertsDialog.Widget), operationsAlertsWidget);
        });

        return !dialogResult.Cancelled;
    }

    private async Task<bool> ShowConfigureMyTrainingDialog(MyTrainingWidget myTrainingWidget)
    {
        DialogResult dialogResult = await _dialogService.ShowDialogAsync<ConfigureMyTrainingDialog>(options =>
        {
            options.Modal = true;
            options.Parameters.Add(nameof(ConfigureMyTrainingDialog.Widget), myTrainingWidget);
        });

        return !dialogResult.Cancelled;
    }

    private async Task<bool> ShowSimpleConfigDialog<TDialog>(string widgetParameter, DashboardWidget widget) where TDialog : ComponentBase
    {
        DialogResult dialogResult = await _dialogService.ShowDialogAsync<TDialog>(options =>
        {
            options.Modal = true;
            options.Parameters.Add(widgetParameter, widget);
        });

        return !dialogResult.Cancelled;
    }

    private async Task<bool> ShowConfigureWhoIsOnTodayDialog(WhoIsOnTodayWidget whoIsOnTodayWidget)
    {
        // Admins can point the board at any location; a Manager only at their own.
        Result<LocationScope> scope = await _currentLocationContext.GetScopeAsync();
        Result<List<Location>> locations = await _companyLocationService.GetLocationsAsync();

        List<Location> pickable = (locations.Data ?? [])
            .Where(x => scope.Data?.IsAdmin == true || x.Id == scope.Data?.LocationId)
            .OrderBy(x => x.GetDisplayName())
            .ToList();

        DialogResult dialogResult = await _dialogService.ShowDialogAsync<ConfigureWhoIsOnTodayDialog>(options =>
        {
            options.Modal = true;
            options.Parameters.Add(nameof(ConfigureWhoIsOnTodayDialog.Widget), whoIsOnTodayWidget);
            options.Parameters.Add(nameof(ConfigureWhoIsOnTodayDialog.Locations), pickable);
        });

        return !dialogResult.Cancelled;
    }

    private async Task<bool> ShowConfigureComplianceSummaryDialog(ComplianceSummaryWidget complianceSummaryWidget)
    {
        // Only an Admin picks a location; a Manager's view is always their own location, so they
        // get no list to choose from.
        Result<LocationScope> scope = await _currentLocationContext.GetScopeAsync();
        List<Location> pickable = [];

        if (scope.Data?.IsAdmin == true)
        {
            Result<List<Location>> locations = await _companyLocationService.GetLocationsAsync();
            pickable = [.. (locations.Data ?? []).OrderBy(x => x.GetDisplayName())];
        }

        DialogResult dialogResult = await _dialogService.ShowDialogAsync<ConfigureComplianceSummaryDialog>(options =>
        {
            options.Modal = true;
            options.Parameters.Add(nameof(ConfigureComplianceSummaryDialog.Widget), complianceSummaryWidget);
            options.Parameters.Add(nameof(ConfigureComplianceSummaryDialog.Locations), pickable);
        });

        return !dialogResult.Cancelled;
    }

    private async Task<bool> ShowConfigureDemandStaffingDialog(DemandStaffingWidget demandStaffingWidget)
    {
        // As with compliance: only an Admin picks a location.
        Result<LocationScope> scope = await _currentLocationContext.GetScopeAsync();
        List<Location> pickable = [];

        if (scope.Data?.IsAdmin == true)
        {
            Result<List<Location>> locations = await _companyLocationService.GetLocationsAsync();
            pickable = [.. (locations.Data ?? []).OrderBy(x => x.GetDisplayName())];
        }

        DialogResult dialogResult = await _dialogService.ShowDialogAsync<ConfigureDemandStaffingDialog>(options =>
        {
            options.Modal = true;
            options.Parameters.Add(nameof(ConfigureDemandStaffingDialog.Widget), demandStaffingWidget);
            options.Parameters.Add(nameof(ConfigureDemandStaffingDialog.Locations), pickable);
        });

        return !dialogResult.Cancelled;
    }

    private async Task<bool> ShowConfigureWhatsOnTodayDialog(WhatsOnTodayWidget whatsOnTodayWidget)
    {
        // As with demand and staffing: only an Admin picks a location.
        Result<LocationScope> scope = await _currentLocationContext.GetScopeAsync();
        List<Location> pickable = [];

        if (scope.Data?.IsAdmin == true)
        {
            Result<List<Location>> locations = await _companyLocationService.GetLocationsAsync();
            pickable = [.. (locations.Data ?? []).OrderBy(x => x.GetDisplayName())];
        }

        DialogResult dialogResult = await _dialogService.ShowDialogAsync<ConfigureWhatsOnTodayDialog>(options =>
        {
            options.Modal = true;
            options.Parameters.Add(nameof(ConfigureWhatsOnTodayDialog.Widget), whatsOnTodayWidget);
            options.Parameters.Add(nameof(ConfigureWhatsOnTodayDialog.Locations), pickable);
        });

        return !dialogResult.Cancelled;
    }

    private async Task<bool> ShowConfigureAnnouncementsDialog(AnnouncementsWidget announcementsWidget)
    {
        DialogResult dialogResult = await _dialogService.ShowDialogAsync<ConfigureAnnouncementsDialog>(options =>
        {
            options.Modal = true;
            options.Parameters.Add(nameof(ConfigureAnnouncementsDialog.Widget), announcementsWidget);
        });

        return !dialogResult.Cancelled;
    }

    private async Task<bool> ShowConfigureDigitalClockDialog(DigitalClockWidget clockWidget)
    {
        DialogResult dialogResult = await _dialogService.ShowDialogAsync<ConfigureDigitalClockDialog>(options =>
        {
            options.Modal = true;
            options.Parameters.Add(nameof(ConfigureDigitalClockDialog.Widget), clockWidget);
        });

        return !dialogResult.Cancelled;
    }

    private async Task<bool> ShowConfigureLaserGameStatusDialog(ClientZoneLaserGameStatusWidget laserGameStatusWidget)
    {
        IEnumerable<Client> clients = await GetZoneScoreboardClientsAsync();

        DialogResult dialogResult = await _dialogService.ShowDialogAsync<ConfigureLaserGameStatusDialog>(options =>
        {
            options.Modal = true;
            options.Parameters.Add(nameof(ConfigureLaserGameStatusDialog.Widget), laserGameStatusWidget);
            options.Parameters.Add(nameof(ConfigureLaserGameStatusDialog.Clients), clients);
        });

        return !dialogResult.Cancelled;
    }

    private async Task<bool> ShowConfigureLaserScoreboardDialog(ClientZoneLaserScoreboardWidget laserScoreboardWidget)
    {
        IEnumerable<Client> clients = await GetZoneScoreboardClientsAsync();

        DialogResult dialogResult = await _dialogService.ShowDialogAsync<ConfigureLaserScoreboardDialog>(options =>
        {
            options.Modal = true;
            options.Parameters.Add(nameof(ConfigureLaserScoreboardDialog.Widget), laserScoreboardWidget);
            options.Parameters.Add(nameof(ConfigureLaserScoreboardDialog.Clients), clients);
        });

        return !dialogResult.Cancelled;
    }

    private async Task<bool> ShowConfigureButtonDialog(ButtonWidget buttonWidget)
    {
        IEnumerable<Client> clients = [];

        Result<IEnumerable<ClientConnectedWithCapabilitiesDTO>> clientResult = await ClientService.GetClientsWithCapabilitiesAsync();

        if (clientResult.IsSuccess && clientResult.Data != null)
        {
            clients = clientResult.Data
                .Cast<Client>();
        }
        else
        {
            _toastService.ShowError($"Failed to load clients, {clientResult.Error}");
        }

        IEnumerable<ProjectionProgram> programs = [];

        Result<IEnumerable<ProjectionProgram>> programResult = await ProjectionProgramService.GetProjectionProgramsAsync();

        if (programResult.IsSuccess && programResult.Data != null)
        {
            programs = programResult.Data;
        }
        else
        {
            _toastService.ShowError($"Failed to load projection programs, {programResult.Error}");
        }

        DialogResult dialogResult = await _dialogService.ShowDialogAsync<ConfigureButtonDialog>(options =>
        {
            options.Modal = true;
            options.Parameters.Add(nameof(ConfigureButtonDialog.Widget), buttonWidget);
            options.Parameters.Add(nameof(ConfigureButtonDialog.Clients), clients);
            options.Parameters.Add(nameof(ConfigureButtonDialog.Programs), programs);
        });

        return !dialogResult.Cancelled;
    }

    private async Task<bool> ShowConfigureProjectionStatusDialog(ProjectionStatusWidget projectionStatusWidget)
    {
        IEnumerable<Client> clients = [];

        Result<IEnumerable<ClientConnectedWithCapabilitiesDTO>> clientResult = await ClientService.GetClientsWithCapabilitiesAsync();

        if (clientResult.IsSuccess && clientResult.Data != null)
        {
            clients = clientResult.Data
                .Cast<Client>();
        }
        else
        {
            _toastService.ShowError($"Failed to load clients, {clientResult.Error}");
        }

        DialogResult dialogResult = await _dialogService.ShowDialogAsync<ConfigureProjectionStatusDialog>(options =>
        {
            options.Modal = true;
            options.Parameters.Add(nameof(ConfigureProjectionStatusDialog.Widget), projectionStatusWidget);
            options.Parameters.Add(nameof(ConfigureProjectionStatusDialog.Clients), clients);
        });

        return !dialogResult.Cancelled;
    }
}
