using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DTO;
using Lanyard.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Lanyard.Application.Services;

public class DashboardService(IDbContextFactory<ApplicationDbContext> factory) : IDashboardService
{
    // Organisation-wide rather than per-user, so it lives in the shared AppSettings key/value
    // table alongside "AutomationEngine.Enabled" instead of needing a column of its own. Writes go
    // through a read-then-upsert on this key, which keeps a single default without a second table.
    // AppSettings has no unique index on Key, so that is a convention this service holds to rather
    // than something the schema enforces - the same footing "AutomationEngine.Enabled" is on.
    private const string OrganisationDefaultDashboardSettingKey = "Dashboard.OrganisationDefaultDashboardId";

    private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;

    public async Task<Result<IEnumerable<Dashboard>>> GetDashboardsAsync()
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            List<Dashboard> dashboards = await ctx.Dashboards
                .AsNoTracking()
                .Where(x => x.IsActive)
                .Include(x => x.Widgets.Where(w => w.IsActive))
                .OrderBy(x => x.Name)
                .ToListAsync();

            return Result<IEnumerable<Dashboard>>.Ok(dashboards);
        }
        catch (Exception ex)
        {
            return Result<IEnumerable<Dashboard>>.Fail(ex.Message);
        }
    }

    public async Task<Result<Dashboard>> GetDashboardAsync(Guid dashboardId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Dashboard? dashboard = await ctx.Dashboards
                .AsNoTracking()
                .Include(x => x.Widgets.Where(w => w.IsActive))
                .FirstOrDefaultAsync(x => x.Id == dashboardId);

            if (dashboard is null)
            {
                return Result<Dashboard>.Fail("Dashboard not found.");
            }

            return Result<Dashboard>.Ok(dashboard);
        }
        catch (Exception ex)
        {
            return Result<Dashboard>.Fail(ex.Message);
        }
    }

    public async Task<Result<bool>> DeleteDashboardAsync(Guid dashboardId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Dashboard? dashboard = await ctx.Dashboards
                .Include(x => x.Widgets)
                .FirstOrDefaultAsync(x => x.Id == dashboardId);

            if (dashboard is null)
            {
                return Result<bool>.Fail("Dashboard not found.");
            }

            dashboard.IsActive = false;
            dashboard.LastUpdateDate = DateTime.UtcNow;

            foreach (DashboardWidget widget in dashboard.Widgets)
            {
                widget.IsActive = false;
            }

            // Unlike a per-user choice, a stale organisation default is unrecoverable from the UI:
            // the dashboards list only renders active dashboards, so there would be no row left to
            // click to clear it, while every inheriting user kept being bounced to the standard
            // home page. This is the only place that state can be reached from.
            AppSetting? organisationDefault = await ctx.AppSettings
                .FirstOrDefaultAsync(x => x.Key == OrganisationDefaultDashboardSettingKey);

            if (organisationDefault?.Value == dashboardId.ToString())
            {
                organisationDefault.Value = string.Empty;
            }

            await ctx.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result<bool>.Fail("The dashboard was modified by another operation. Please reload and try again.");
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail(ex.Message);
        }
    }

    public async Task<Result<bool>> CreateDashboardAsync(Dashboard dashboard)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dashboard.Name))
            {
                return Result<bool>.Fail("Dashboard name is required.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Dashboard newDashboard = new()
            {
                Name = dashboard.Name.Trim(),
                Description = dashboard.Description?.Trim(),
                IsActive = true,
                CreateDate = DateTime.UtcNow,
                LastUpdateDate = DateTime.UtcNow
            };

            ctx.Dashboards.Add(newDashboard);
            await ctx.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail(ex.Message);
        }
    }

    public async Task<Result<bool>> SaveDashboardAsync(Dashboard dashboard)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(dashboard);

            if (dashboard.Id == Guid.Empty)
            {
                return Result<bool>.Fail("Dashboard id is required.");
            }

            if (string.IsNullOrWhiteSpace(dashboard.Name))
            {
                return Result<bool>.Fail("Dashboard name is required.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            Dashboard? existingDashboard = await ctx.Dashboards
                .FirstOrDefaultAsync(x => x.Id == dashboard.Id);

            if (existingDashboard is null)
            {
                return Result<bool>.Fail("Dashboard not found.");
            }

            existingDashboard.Name = dashboard.Name.Trim();
            existingDashboard.Description = dashboard.Description?.Trim();
            existingDashboard.LastUpdateDate = DateTime.UtcNow;

            List<DashboardWidget> incomingWidgets = dashboard.Widgets ?? [];
            List<DashboardWidget> existingWidgets = await ctx.DashboardWidgets
                .Where(x => x.DashboardId == dashboard.Id)
                .ToListAsync();

            Dictionary<Guid, DashboardWidget> existingWidgetsById = existingWidgets
                .ToDictionary(x => x.Id, x => x);

            foreach (DashboardWidget incomingWidget in incomingWidgets)
            {
                if (incomingWidget.Id == Guid.Empty)
                {
                    incomingWidget.Id = Guid.NewGuid();
                }

                DashboardWidget? existingWidget = existingWidgetsById
                    .GetValueOrDefault(incomingWidget.Id);

                if (existingWidget is null)
                {
                    DashboardWidget newWidget = CreateWidgetCopy(incomingWidget, existingDashboard.Id);
                    await ctx.DashboardWidgets.AddAsync(newWidget);
                    continue;
                }

                if (existingWidget.GetType() != incomingWidget.GetType())
                {
                    return Result<bool>.Fail("Widget type mismatch.");
                }

                UpdateCommonMutableWidgetProperties(existingWidget, incomingWidget);
                UpdateTypeSpecificWidgetProperties(existingWidget, incomingWidget);

                // Being in the incoming list is what makes a widget active - the caller never sets
                // the flag, and the loop below is what deactivates anything left out.
                existingWidget.IsActive = true;
            }

            HashSet<Guid> incomingWidgetIds = incomingWidgets.Select(x => x.Id).ToHashSet();

            foreach (DashboardWidget existingWidget in existingWidgets)
            {
                if (!incomingWidgetIds.Contains(existingWidget.Id))
                {
                    existingWidget.IsActive = false;
                }
            }

            await ctx.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result<bool>.Fail("Dashboard was changed by another operation. Refresh and try saving again.");
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail(ex.Message);
        }
    }

    public async Task<Result<DashboardWidget>> SaveWidgetAsync(DashboardWidget widget)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(widget);

            if (widget.Id == Guid.Empty)
            {
                return Result<DashboardWidget>.Fail("Widget id is required.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            DashboardWidget? existingWidget = await ctx.DashboardWidgets
                .FirstOrDefaultAsync(x => x.Id == widget.Id);

            if (existingWidget is null)
            {
                return Result<DashboardWidget>.Fail("Widget not found.");
            }

            // IsActive is deliberately not copied - this saves a widget's configuration, and only
            // SaveDashboardAsync/DeleteDashboardAsync own the soft-delete flag.
            existingWidget.Title = widget.Title?.Trim();
            existingWidget.GridX = widget.GridX;
            existingWidget.GridY = widget.GridY;
            existingWidget.GridW = widget.GridW;
            existingWidget.GridH = widget.GridH;

            if (existingWidget.GetType() != widget.GetType())
            {
                return Result<DashboardWidget>.Fail("Widget type mismatch.");
            }

            switch (existingWidget)
            {
                case TextAreaWidget existingTextArea when widget is TextAreaWidget incomingTextArea:
                    existingTextArea.Content = incomingTextArea.Content;
                    break;

                case DigitalClockWidget existingClock when widget is DigitalClockWidget incomingClock:
                    existingClock.ShowDate = incomingClock.ShowDate;
                    existingClock.ShowMilliSeconds = incomingClock.ShowMilliSeconds;
                    existingClock.Is24HourFormat = incomingClock.Is24HourFormat;
                    break;
                case ClientZoneLaserScoreboardWidget existingScoreboard when widget is ClientZoneLaserScoreboardWidget incomingScoreboard:
                    existingScoreboard.ClientId = incomingScoreboard.ClientId;
                    break;
                case ClientZoneLaserGameStatusWidget existingLaserGameStatus when widget is ClientZoneLaserGameStatusWidget incomingLaserGameStatus:
                    existingLaserGameStatus.ShowCurrentGameStatus = incomingLaserGameStatus.ShowCurrentGameStatus;
                    existingLaserGameStatus.ShowTimeLeft = incomingLaserGameStatus.ShowTimeLeft;
                    existingLaserGameStatus.ClientId = incomingLaserGameStatus.ClientId;
                    break;
                case ButtonWidget existingButton when widget is ButtonWidget incomingButton:
                    existingButton.Label = incomingButton.Label;
                    existingButton.Appearance = incomingButton.Appearance;
                    existingButton.ActionType = incomingButton.ActionType;
                    existingButton.ClientId = incomingButton.ClientId;
                    existingButton.ProjectionProgramId = incomingButton.ProjectionProgramId;
                    existingButton.DisplayIndex = incomingButton.DisplayIndex;
                    existingButton.SkipToPreviousStep = incomingButton.SkipToPreviousStep;
                    break;
                case MusicPlaylistSelectorWidget existingPlaylistSelector when widget is MusicPlaylistSelectorWidget incomingPlaylistSelector:
                    existingPlaylistSelector.ClientId = incomingPlaylistSelector.ClientId;
                    break;
                case MusicTimelineWidget existingTimeline when widget is MusicTimelineWidget incomingTimeline:
                    existingTimeline.ClientId = incomingTimeline.ClientId;
                    existingTimeline.ShowSongTitle = incomingTimeline.ShowSongTitle;
                    break;
                case AutomationRuleStatusWidget existingRuleStatus when widget is AutomationRuleStatusWidget incomingRuleStatus:
                    existingRuleStatus.AutomationRuleId = incomingRuleStatus.AutomationRuleId;
                    break;
                case KioskHealthWidget existingKioskHealth when widget is KioskHealthWidget incomingKioskHealth:
                    existingKioskHealth.OnlyShowOffline = incomingKioskHealth.OnlyShowOffline;
                    break;
                case HallOfFameWidget existingHallOfFame when widget is HallOfFameWidget incomingHallOfFame:
                    existingHallOfFame.Period = incomingHallOfFame.Period;
                    existingHallOfFame.ShowTopScore = incomingHallOfFame.ShowTopScore;
                    existingHallOfFame.ShowBestAccuracy = incomingHallOfFame.ShowBestAccuracy;
                    existingHallOfFame.ShowBestTeam = incomingHallOfFame.ShowBestTeam;
                    existingHallOfFame.ClientId = incomingHallOfFame.ClientId;
                    break;
                case MyTrainingWidget existingMyTraining when widget is MyTrainingWidget incomingMyTraining:
                    existingMyTraining.IncludeCompleted = incomingMyTraining.IncludeCompleted;
                    existingMyTraining.MaxItems = incomingMyTraining.MaxItems;
                    break;
                case ProjectionStatusWidget existingProjectionStatus when widget is ProjectionStatusWidget incomingProjectionStatus:
                    existingProjectionStatus.ClientId = incomingProjectionStatus.ClientId;
                    existingProjectionStatus.DisplayIndex = incomingProjectionStatus.DisplayIndex;
                    existingProjectionStatus.ShowControls = incomingProjectionStatus.ShowControls;
                    break;
                case AnnouncementsWidget existingAnnouncements when widget is AnnouncementsWidget incomingAnnouncements:
                    existingAnnouncements.MaxItems = incomingAnnouncements.MaxItems;
                    break;
            }

            Dashboard? parentDashboard = await ctx.Dashboards
                .FirstOrDefaultAsync(x => x.Id == existingWidget.DashboardId);

            if (parentDashboard is not null)
            {
                parentDashboard.LastUpdateDate = DateTime.UtcNow;
            }

            await ctx.SaveChangesAsync();

            return Result<DashboardWidget>.Ok(existingWidget);
        }
        catch (Exception ex)
        {
            return Result<DashboardWidget>.Fail(ex.Message);
        }
    }

    public async Task<Result<Guid?>> GetDefaultDashboardIdAsync(string userId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(userId))
            {
                return Result<Guid?>.Fail("User id is required.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            // Deliberately returns the stored id without validating it. A dashboard that has
            // since been deactivated still comes back here so the caller can tell that apart
            // from "never chose one" and explain the fallback to the user.
            Guid? defaultDashboardId = await ctx.Users
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.Id == userId)
                .Select(x => x.DefaultDashboardId)
                .FirstOrDefaultAsync();

            return Result<Guid?>.Ok(defaultDashboardId);
        }
        catch (Exception ex)
        {
            return Result<Guid?>.Fail(ex.Message);
        }
    }

    public async Task<Result<bool>> SetDefaultDashboardIdAsync(string userId, Guid? dashboardId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(userId))
            {
                return Result<bool>.Fail("User id is required.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            if (dashboardId is Guid targetDashboardId)
            {
                bool dashboardIsUsable = await ctx.Dashboards
                    .AsNoTracking()
                    .TagWithCallSite()
                    .AnyAsync(x => x.Id == targetDashboardId && x.IsActive);

                if (!dashboardIsUsable)
                {
                    return Result<bool>.Fail("Dashboard not found.");
                }
            }

            UserProfile? user = await ctx.Users.FirstOrDefaultAsync(x => x.Id == userId);

            if (user is null)
            {
                return Result<bool>.Fail("User not found.");
            }

            user.DefaultDashboardId = dashboardId;

            // Picking a dashboard implicitly withdraws an earlier "always give me the standard
            // home page" choice - otherwise the flag would keep overriding the new selection.
            if (dashboardId is not null)
            {
                user.UseStandardHomePage = false;
            }

            await ctx.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail(ex.Message);
        }
    }

    public async Task<Result<bool>> SetUseStandardHomePageAsync(string userId, bool useStandardHomePage)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(userId))
            {
                return Result<bool>.Fail("User id is required.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            UserProfile? user = await ctx.Users.FirstOrDefaultAsync(x => x.Id == userId);

            if (user is null)
            {
                return Result<bool>.Fail("User not found.");
            }

            user.UseStandardHomePage = useStandardHomePage;

            // Asking for the standard home page and having a dashboard of your own are mutually
            // exclusive; a stale id left behind here would come back the moment the flag cleared.
            if (useStandardHomePage)
            {
                user.DefaultDashboardId = null;
            }

            await ctx.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail(ex.Message);
        }
    }

    public async Task<Result<Guid?>> GetOrganisationDefaultDashboardIdAsync()
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            string? storedValue = await ctx.AppSettings
                .AsNoTracking()
                .TagWithCallSite()
                .Where(x => x.Key == OrganisationDefaultDashboardSettingKey)
                .Select(x => x.Value)
                .FirstOrDefaultAsync();

            // Clearing the default blanks the value rather than deleting the row, so a missing
            // row and an empty one both have to read as "no organisation default".
            return Result<Guid?>.Ok(Guid.TryParse(storedValue, out Guid dashboardId) ? dashboardId : null);
        }
        catch (Exception ex)
        {
            return Result<Guid?>.Fail(ex.Message);
        }
    }

    public async Task<Result<bool>> SetOrganisationDefaultDashboardIdAsync(Guid? dashboardId)
    {
        try
        {
            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            if (dashboardId is Guid targetDashboardId)
            {
                bool dashboardIsUsable = await ctx.Dashboards
                    .AsNoTracking()
                    .TagWithCallSite()
                    .AnyAsync(x => x.Id == targetDashboardId && x.IsActive);

                if (!dashboardIsUsable)
                {
                    return Result<bool>.Fail("Dashboard not found.");
                }
            }

            AppSetting? setting = await ctx.AppSettings
                .FirstOrDefaultAsync(x => x.Key == OrganisationDefaultDashboardSettingKey);

            string value = dashboardId?.ToString() ?? string.Empty;

            if (setting is null)
            {
                ctx.AppSettings.Add(new AppSetting
                {
                    Id = Guid.NewGuid(),
                    Key = OrganisationDefaultDashboardSettingKey,
                    Value = value,
                    CreateDate = DateTime.UtcNow
                });
            }
            else
            {
                setting.Value = value;
            }

            await ctx.SaveChangesAsync();

            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            return Result<bool>.Fail(ex.Message);
        }
    }

    public async Task<Result<HomeScreenDashboardSelection>> GetHomeScreenDashboardAsync(string userId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(userId))
            {
                return Result<HomeScreenDashboardSelection>.Fail("User id is required.");
            }

            await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync();

            UserProfile? user = await ctx.Users
                .AsNoTracking()
                .TagWithCallSite()
                .FirstOrDefaultAsync(x => x.Id == userId);

            if (user is null)
            {
                return Result<HomeScreenDashboardSelection>.Fail("User not found.");
            }

            // An explicit "give me the standard home page" beats an organisation default. Without
            // this the admin's choice would be impossible for an individual to opt out of.
            if (user.UseStandardHomePage)
            {
                return Result<HomeScreenDashboardSelection>.Ok(new HomeScreenDashboardSelection());
            }

            bool personalDashboardUnavailable = false;

            if (user.DefaultDashboardId is Guid ownDashboardId)
            {
                if (await IsDashboardUsableAsync(ctx, ownDashboardId))
                {
                    return Result<HomeScreenDashboardSelection>.Ok(new HomeScreenDashboardSelection
                    {
                        DashboardId = ownDashboardId
                    });
                }

                // A choice pointing at a deleted dashboard is no longer a choice, so it falls
                // through to the organisation default rather than skipping straight past it to
                // the standard home page.
                personalDashboardUnavailable = true;
            }

            Result<Guid?> organisationDefault = await GetOrganisationDefaultDashboardIdAsync();

            if (!organisationDefault.IsSuccess)
            {
                return Result<HomeScreenDashboardSelection>.Fail(organisationDefault.Error ?? "Failed to read the organisation home screen.");
            }

            // Deleting a dashboard clears it as the organisation default, so a stale id here means
            // the row was changed outside the app. Checked anyway rather than trusted, since the
            // cost is one indexed lookup and the alternative is an empty home screen.
            bool organisationDashboardIsUsable = organisationDefault.Data is Guid organisationDashboardId
                && await IsDashboardUsableAsync(ctx, organisationDashboardId);

            return Result<HomeScreenDashboardSelection>.Ok(new HomeScreenDashboardSelection
            {
                DashboardId = organisationDashboardIsUsable ? organisationDefault.Data : null,
                IsOrganisationDefault = organisationDashboardIsUsable,
                PersonalDashboardUnavailable = personalDashboardUnavailable
            });
        }
        catch (Exception ex)
        {
            return Result<HomeScreenDashboardSelection>.Fail(ex.Message);
        }
    }

    private static Task<bool> IsDashboardUsableAsync(ApplicationDbContext ctx, Guid dashboardId)
    {
        return ctx.Dashboards
            .AsNoTracking()
            .TagWithCallSite()
            .AnyAsync(x => x.Id == dashboardId && x.IsActive);
    }

    private static DashboardWidget CreateWidgetCopy(DashboardWidget widget, Guid dashboardId)
    {
        DashboardWidget copy = widget switch
        {
            TextAreaWidget textAreaWidget => new TextAreaWidget
            {
                Content = textAreaWidget.Content
            },
            DigitalClockWidget clockWidget => new DigitalClockWidget
            {
                ShowDate = clockWidget.ShowDate,
                ShowMilliSeconds = clockWidget.ShowMilliSeconds,
                Is24HourFormat = clockWidget.Is24HourFormat
            },
            ClientZoneLaserGameStatusWidget laserGameStatusWidget => new ClientZoneLaserGameStatusWidget
            {
                ShowCurrentGameStatus = laserGameStatusWidget.ShowCurrentGameStatus,
                ShowTimeLeft = laserGameStatusWidget.ShowTimeLeft,
                ClientId = laserGameStatusWidget.ClientId
            },
            ClientZoneLaserScoreboardWidget scoreboardWidget => new ClientZoneLaserScoreboardWidget
            {
                ClientId = scoreboardWidget.ClientId
            },
            ButtonWidget buttonWidget => new ButtonWidget
            {
                Label = buttonWidget.Label,
                Appearance = buttonWidget.Appearance,
                ActionType = buttonWidget.ActionType,
                ClientId = buttonWidget.ClientId,
                ProjectionProgramId = buttonWidget.ProjectionProgramId,
                DisplayIndex = buttonWidget.DisplayIndex,
                SkipToPreviousStep = buttonWidget.SkipToPreviousStep
            },
            MusicPlaylistSelectorWidget playlistSelectorWidget => new MusicPlaylistSelectorWidget
            {
                ClientId = playlistSelectorWidget.ClientId
            },
            MusicTimelineWidget timelineWidget => new MusicTimelineWidget
            {
                ClientId = timelineWidget.ClientId,
                ShowSongTitle = timelineWidget.ShowSongTitle
            },
            AutomationRuleStatusWidget ruleStatusWidget => new AutomationRuleStatusWidget
            {
                AutomationRuleId = ruleStatusWidget.AutomationRuleId
            },
            KioskHealthWidget kioskHealthWidget => new KioskHealthWidget
            {
                OnlyShowOffline = kioskHealthWidget.OnlyShowOffline
            },
            HallOfFameWidget hallOfFameWidget => new HallOfFameWidget
            {
                Period = hallOfFameWidget.Period,
                ShowTopScore = hallOfFameWidget.ShowTopScore,
                ShowBestAccuracy = hallOfFameWidget.ShowBestAccuracy,
                ShowBestTeam = hallOfFameWidget.ShowBestTeam,
                ClientId = hallOfFameWidget.ClientId
            },
            MyTrainingWidget myTrainingWidget => new MyTrainingWidget
            {
                IncludeCompleted = myTrainingWidget.IncludeCompleted,
                MaxItems = myTrainingWidget.MaxItems
            },
            // No configurable properties - the greeting is derived from the clock and the
            // signed-in user - but the case is still required, or the switch below throws and
            // the whole dashboard save fails.
            GreetingWidget => new GreetingWidget(),
            ProjectionStatusWidget projectionStatusWidget => new ProjectionStatusWidget
            {
                ClientId = projectionStatusWidget.ClientId,
                DisplayIndex = projectionStatusWidget.DisplayIndex,
                ShowControls = projectionStatusWidget.ShowControls
            },
            AnnouncementsWidget announcementsWidget => new AnnouncementsWidget
            {
                MaxItems = announcementsWidget.MaxItems
            },
            _ => throw new InvalidOperationException("Unsupported widget type.")
        };

        copy.Id = widget.Id == Guid.Empty ? Guid.NewGuid() : widget.Id;
        copy.Type = widget.Type;
        UpdateCommonMutableWidgetProperties(copy, widget);
        copy.DashboardId = dashboardId;
        copy.IsActive = true;

        return copy;
    }

    private static void UpdateCommonMutableWidgetProperties(DashboardWidget target, DashboardWidget source)
    {
        target.Title = source.Title?.Trim();
        target.GridX = source.GridX;
        target.GridY = source.GridY;
        target.GridW = source.GridW;
        target.GridH = source.GridH;
    }

    private static void UpdateTypeSpecificWidgetProperties(DashboardWidget target, DashboardWidget source)
    {
        if (target is TextAreaWidget targetTextArea && source is TextAreaWidget sourceTextArea)
        {
            targetTextArea.Content = sourceTextArea.Content;
            return;
        }

        if (target is DigitalClockWidget targetClock && source is DigitalClockWidget sourceClock)
        {
            targetClock.ShowDate = sourceClock.ShowDate;
            targetClock.ShowMilliSeconds = sourceClock.ShowMilliSeconds;
            targetClock.Is24HourFormat = sourceClock.Is24HourFormat;
        }

        if (target is ClientZoneLaserGameStatusWidget targetLaserGameStatus && source is ClientZoneLaserGameStatusWidget sourceLaserGameStatus)
        {
            targetLaserGameStatus.ShowCurrentGameStatus = sourceLaserGameStatus.ShowCurrentGameStatus;
            targetLaserGameStatus.ShowTimeLeft = sourceLaserGameStatus.ShowTimeLeft;
            targetLaserGameStatus.ClientId = sourceLaserGameStatus.ClientId;
        }

        if (target is ClientZoneLaserScoreboardWidget targetScoreboard && source is ClientZoneLaserScoreboardWidget sourceScoreboard)
        {
            targetScoreboard.ClientId = sourceScoreboard.ClientId;
        }

        if (target is ButtonWidget targetButton && source is ButtonWidget sourceButton)
        {
            targetButton.Label = sourceButton.Label;
            targetButton.Appearance = sourceButton.Appearance;
            targetButton.ActionType = sourceButton.ActionType;
            targetButton.ClientId = sourceButton.ClientId;
            targetButton.ProjectionProgramId = sourceButton.ProjectionProgramId;
            targetButton.DisplayIndex = sourceButton.DisplayIndex;
            targetButton.SkipToPreviousStep = sourceButton.SkipToPreviousStep;
        }

        if (target is MusicPlaylistSelectorWidget targetPlaylistSelector && source is MusicPlaylistSelectorWidget sourcePlaylistSelector)
        {
            targetPlaylistSelector.ClientId = sourcePlaylistSelector.ClientId;
        }

        if (target is MusicTimelineWidget targetTimeline && source is MusicTimelineWidget sourceTimeline)
        {
            targetTimeline.ClientId = sourceTimeline.ClientId;
            targetTimeline.ShowSongTitle = sourceTimeline.ShowSongTitle;
        }

        if (target is AutomationRuleStatusWidget targetRuleStatus && source is AutomationRuleStatusWidget sourceRuleStatus)
        {
            targetRuleStatus.AutomationRuleId = sourceRuleStatus.AutomationRuleId;
            return;
        }

        if (target is KioskHealthWidget targetKioskHealth && source is KioskHealthWidget sourceKioskHealth)
        {
            targetKioskHealth.OnlyShowOffline = sourceKioskHealth.OnlyShowOffline;
            return;
        }

        if (target is HallOfFameWidget targetHallOfFame && source is HallOfFameWidget sourceHallOfFame)
        {
            targetHallOfFame.Period = sourceHallOfFame.Period;
            targetHallOfFame.ShowTopScore = sourceHallOfFame.ShowTopScore;
            targetHallOfFame.ShowBestAccuracy = sourceHallOfFame.ShowBestAccuracy;
            targetHallOfFame.ShowBestTeam = sourceHallOfFame.ShowBestTeam;
            targetHallOfFame.ClientId = sourceHallOfFame.ClientId;
            return;
        }

        if (target is MyTrainingWidget targetMyTraining && source is MyTrainingWidget sourceMyTraining)
        {
            targetMyTraining.IncludeCompleted = sourceMyTraining.IncludeCompleted;
            targetMyTraining.MaxItems = sourceMyTraining.MaxItems;
            return;
        }

        if (target is AnnouncementsWidget targetAnnouncements && source is AnnouncementsWidget sourceAnnouncements)
        {
            targetAnnouncements.MaxItems = sourceAnnouncements.MaxItems;
        }

        if (target is ProjectionStatusWidget targetProjectionStatus && source is ProjectionStatusWidget sourceProjectionStatus)
        {
            targetProjectionStatus.ClientId = sourceProjectionStatus.ClientId;
            targetProjectionStatus.DisplayIndex = sourceProjectionStatus.DisplayIndex;
            targetProjectionStatus.ShowControls = sourceProjectionStatus.ShowControls;
        }
    }
}
