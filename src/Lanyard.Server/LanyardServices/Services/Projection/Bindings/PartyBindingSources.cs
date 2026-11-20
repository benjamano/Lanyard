using Lanyard.Application.Services.Parties;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.DTO;
using Microsoft.EntityFrameworkCore;

namespace Lanyard.Application.Services.Projection.Bindings;

/// <summary>
/// Shared by the party live-text sources: picks this kiosk's party (its room's, else the
/// location's) from IPartyScheduleProvider, which already keeps to the company, skips cancelled
/// parties, provisional holds and private hires, and returns nothing when Parties is switched off.
/// </summary>
public abstract class PartyBindingSource(IPartyScheduleProvider schedule, ISystemDbContextFactory factory) : IProjectionBindingSource
{
    private readonly IPartyScheduleProvider _schedule = schedule;
    private readonly ISystemDbContextFactory _factory = factory;

    public abstract string Key { get; }

    /// <summary>Whether this source shows the child's name, and so needs the company's say-so.</summary>
    protected abstract bool ShowsName { get; }

    protected abstract string? Describe(PartySlot party, DateTime nowUtc);

    public async Task<Result<string?>> ResolveAsync(ProjectionBindingContext context, CancellationToken cancellationToken)
    {
        if (context.LocationId is not int locationId)
        {
            return Result<string?>.Ok(null);
        }

        try
        {
            if (ShowsName && !await NamesAllowedAsync(context.CompanyId, cancellationToken))
            {
                return Result<string?>.Ok(null);
            }

            Result<IReadOnlyList<PartySlot>> parties = await _schedule.GetDayAsync(context.CompanyId, locationId, context.NowUtc, cancellationToken);

            if (!parties.IsSuccess)
            {
                return Result<string?>.Fail(parties.Error ?? "Couldn't load today's parties.");
            }

            PartySlot? party = PartyScreen.Choose(parties.Data ?? [], context.PartyRoom, context.NowUtc);

            return Result<string?>.Ok(party is null ? null : Describe(party, context.NowUtc));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result<string?>.Fail($"Couldn't load the party: {ex.Message}");
        }
    }

    // Read every time rather than cached: switching it off has to take a name off the screens on
    // the next refresh. The resolver's 10-second cache already keeps this to a trickle.
    private async Task<bool> NamesAllowedAsync(int companyId, CancellationToken cancellationToken)
    {
        await using ApplicationDbContext ctx = await _factory.CreateDbContextAsync(cancellationToken);

        return await ctx.Companies
            .AsNoTracking()
            .TagWithCallSite()
            .Where(c => c.Id == companyId && c.IsActive)
            .Select(c => c.ShowPartyNamesOnScreens)
            .FirstOrDefaultAsync(cancellationToken);
    }
}

/// <summary>"Happy 7th birthday, Ava!" Only when the company lets screens show names.</summary>
public sealed class PartyWelcomeBindingSource(IPartyScheduleProvider schedule, ISystemDbContextFactory factory) : PartyBindingSource(schedule, factory)
{
    public override string Key => ProjectionBindingKeys.PartyWelcome;
    protected override bool ShowsName => true;
    protected override string? Describe(PartySlot party, DateTime nowUtc) => PartyScreen.Welcome(party);
}

/// <summary>"Ava", for artwork that has its own wording. Only when the company lets screens show names.</summary>
public sealed class PartyChildNameBindingSource(IPartyScheduleProvider schedule, ISystemDbContextFactory factory) : PartyBindingSource(schedule, factory)
{
    public override string Key => ProjectionBindingKeys.PartyChildName;
    protected override bool ShowsName => true;
    protected override string? Describe(PartySlot party, DateTime nowUtc) => PartyScreen.FirstName(party.ChildName);
}

/// <summary>"Laser tag at 14:30" / "Food at 15:00". No name, so it shows whatever the company switch says.</summary>
public sealed class PartyNextUpBindingSource(IPartyScheduleProvider schedule, ISystemDbContextFactory factory) : PartyBindingSource(schedule, factory)
{
    public override string Key => ProjectionBindingKeys.PartyNextUp;
    protected override bool ShowsName => false;
    protected override string? Describe(PartySlot party, DateTime nowUtc) => PartyScreen.NextUp(party, nowUtc);
}
