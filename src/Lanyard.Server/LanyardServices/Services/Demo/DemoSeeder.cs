using Lanyard.Application.Services.Chat;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.DataAccess.Tenancy;
using Lanyard.Infrastructure.Enum;
using Lanyard.Infrastructure.Models;
using Lanyard.Infrastructure.Models.Dmx;
using Lanyard.Shared.Enum;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Lanyard.Application.Services.Demo;

// Builds the public demo company: a made-up two-site entertainment venue with staff, a published
// rota, time off, training, announcements, chat, dashboards and (hardware-less) venue controls.
// Everything is dated relative to "today" so the demo always looks current.
//
// Runs after DemoDataWiper has emptied the company, so it only ever inserts. The company, its two
// locations and the people's accounts keep fixed identities across resets, which is what lets a
// visitor's open session survive the nightly reset.
public sealed class DemoSeeder(DbContextOptions<ApplicationDbContext> options, UserManager<UserProfile> userManager, TimeProvider timeProvider)
{
    public const string CompanyName = "Starlight Leisure";
    private const string ThemeColorHex = "#6D28D9";
    private const string Riverside = "Riverside";
    private const string Harbourside = "Harbourside";
    private const string EmailDomain = "demo.lanyard.invalid";

    private sealed record Person(string Id, string First, string Last, string[] Locations, string[] Roles, string? Position);

    // Ids are fixed so accounts, sessions and "who is who" stay stable across resets.
    private static readonly Person[] People =
    [
        new(DemoAccounts.AdminUserId, "Alex", "Morgan", [Riverside, Harbourside],
            ["Admin", "Manager", "Staff", "CanControlMusic", "CanManageDmxSystems", "CanManageFiles", "CanPostAnnouncements"], null),
        new(DemoAccounts.ManagerUserId, "Priya", "Patel", [Riverside],
            ["Manager", "Staff", "CanControlMusic", "CanPostAnnouncements"], "Duty manager"),
        new(DemoAccounts.StaffUserId, "Jordan", "Lee", [Riverside], ["Staff"], "Marshal"),
        new("lanyard-demo-sam", "Sam", "Okafor", [Riverside], ["Staff"], "Marshal"),
        new("lanyard-demo-ellie", "Ellie", "Chen", [Riverside], ["Staff"], "Party host"),
        new("lanyard-demo-tom", "Tom", "Hughes", [Riverside], ["Staff"], "Front desk"),
        new("lanyard-demo-maya", "Maya", "Singh", [Riverside], ["Staff"], "Café"),
        new("lanyard-demo-ben", "Ben", "Carter", [Riverside], ["Staff"], "Marshal"),
        new("lanyard-demo-chloe", "Chloe", "Adams", [Riverside], ["Staff"], "Party host"),
        new("lanyard-demo-ryan", "Ryan", "Brooks", [Harbourside], ["Manager", "Staff"], "Duty manager"),
        new("lanyard-demo-aisha", "Aisha", "Khan", [Harbourside], ["Staff"], "Marshal"),
        new("lanyard-demo-liam", "Liam", "Walsh", [Harbourside], ["Staff"], "Front desk"),
        new("lanyard-demo-zoe", "Zoe", "Price", [Harbourside], ["Staff"], "Café"),
    ];

    public static IReadOnlySet<string> PersonIds { get; } = People.Select(p => p.Id).ToHashSet();

    private static readonly (string Name, int Color)[] Positions =
    [
        ("Duty manager", 0), ("Marshal", 1), ("Front desk", 2), ("Party host", 3), ("Café", 4),
    ];

    private DateTime Now => timeProvider.GetUtcNow().UtcDateTime;

    // ---------------------------------------------------------------------------------------
    // Company, locations and accounts
    // ---------------------------------------------------------------------------------------

    // Finds the demo company, or creates it (and its locations) the first time. Returns its id.
    public async Task<int> EnsureCompanyAsync(CancellationToken ct)
    {
        await using ApplicationDbContext ctx = new(options);

        Company? company = await ctx.Companies.FirstOrDefaultAsync(x => x.IsDemo, ct);

        if (company is null)
        {
            company = new Company { Name = CompanyName, IsDemo = true, IsActive = true, CreateDate = Now, UpdateDate = Now, ThemeColorHex = ThemeColorHex };
            ctx.Companies.Add(company);
            await ctx.SaveChangesAsync(ct);
        }

        foreach (string name in new[] { Riverside, Harbourside })
        {
            if (!await ctx.Locations.AnyAsync(x => x.CompanyId == company.Id && x.Name == name, ct))
            {
                ctx.Locations.Add(new Location { CompanyId = company.Id, Name = name, IsActive = true, CreateDate = Now, UpdateDate = Now });
            }
        }

        await ctx.SaveChangesAsync(ct);

        return company.Id;
    }

    // Puts the company, its locations and every demo account back to their seeded state. Runs
    // after the wipe, so nothing company-owned still points at the users or locations it removes.
    public async Task ResetPeopleAndPlacesAsync(int companyId, CancellationToken ct)
    {
        await using ApplicationDbContext ctx = new(options);

        Company company = await ctx.Companies.SingleAsync(x => x.Id == companyId && x.IsDemo, ct);
        company.Name = CompanyName;
        company.IsActive = true;
        company.ThemeColorHex = ThemeColorHex;
        company.LogoFileId = null;
        company.BackgroundImageFileId = null;
        company.UpdateDate = Now;

        await ctx.CompanyFeatureSettings.Where(x => x.CompanyId == companyId).ExecuteDeleteAsync(ct);

        List<int> locationIds = await ctx.Locations.Where(x => x.CompanyId == companyId).Select(x => x.Id).ToListAsync(ct);

        // Accounts visitors created in the demo. Only users who belong to nothing but the demo -
        // anyone who's also a member of a real company is never touched.
        List<string> strayUserIds = await ctx.UserLocationMemberships
            .Where(m => locationIds.Contains(m.LocationId) && !PersonIds.Contains(m.UserId))
            .Select(m => m.UserId)
            .Distinct()
            .Where(userId => !ctx.UserLocationMemberships.Any(other => other.UserId == userId && !locationIds.Contains(other.LocationId)))
            .ToListAsync(ct);

        List<string> demoUserIds = [.. PersonIds, .. strayUserIds];

        // Per-user rows that aren't company-owned, so the wipe didn't reach them.
        await ctx.NotificationPreferences.Where(x => demoUserIds.Contains(x.UserId)).ExecuteDeleteAsync(ct);
        await ctx.PushSubscriptions.Where(x => demoUserIds.Contains(x.UserId)).ExecuteDeleteAsync(ct);
        await ctx.AppInstallations.Where(x => demoUserIds.Contains(x.UserId)).ExecuteDeleteAsync(ct);
        await ctx.UserClockInPins.Where(x => demoUserIds.Contains(x.UserId)).ExecuteDeleteAsync(ct);
        await ctx.ChatBlocks.Where(x => demoUserIds.Contains(x.BlockerUserId) || demoUserIds.Contains(x.BlockedUserId)).ExecuteDeleteAsync(ct);
        await ctx.UserLocationMemberships.Where(x => locationIds.Contains(x.LocationId)).ExecuteDeleteAsync(ct);

        // Locations a visitor added. Everything that referenced them went with the wipe.
        await ctx.Locations.Where(x => x.CompanyId == companyId && x.Name != Riverside && x.Name != Harbourside).ExecuteDeleteAsync(ct);
        await ctx.Locations.Where(x => x.CompanyId == companyId).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, true), ct);

        await ctx.SaveChangesAsync(ct);

        foreach (string strayUserId in strayUserIds)
        {
            if (await userManager.FindByIdAsync(strayUserId) is UserProfile stray)
            {
                await userManager.DeleteAsync(stray);
            }
        }

        Dictionary<string, int> locationByName = await ctx.Locations
            .Where(x => x.CompanyId == companyId)
            .ToDictionaryAsync(x => x.Name, x => x.Id, ct);

        foreach (Person person in People)
        {
            await ResetAccountAsync(person, ct);

            foreach (string location in person.Locations)
            {
                ctx.UserLocationMemberships.Add(new UserLocationMembership { UserId = person.Id, LocationId = locationByName[location], CreateDate = Now });
            }
        }

        await ctx.SaveChangesAsync(ct);
    }

    private async Task ResetAccountAsync(Person person, CancellationToken ct)
    {
        UserProfile? user = await userManager.FindByIdAsync(person.Id);
        string userName = person.Id;

        if (user is null)
        {
            user = new UserProfile { Id = person.Id, UserName = userName };

            // No password: the demo accounts are only reachable through the demo login endpoint.
            IdentityResult created = await userManager.CreateAsync(user);

            if (!created.Succeeded)
            {
                throw new InvalidOperationException($"Couldn't create demo account {person.Id}: {string.Join(", ", created.Errors.Select(e => e.Description))}");
            }
        }

        user.UserName = userName;
        user.FirstName = person.First;
        user.LastName = person.Last;
        user.Email = $"{person.First}.{person.Last}@{EmailDomain}".ToLowerInvariant();
        user.EmailConfirmed = true;
        user.PhoneNumber = null;
        user.TwoFactorEnabled = false;
        user.LockoutEnd = null;
        user.AccessFailedCount = 0;
        user.DefaultDashboardId = null;
        user.UseStandardHomePage = false;
        user.PreferredCulture = null;
        user.InvitedDate ??= Now;
        user.PasswordSetDate ??= Now;

        await userManager.UpdateAsync(user);
        await userManager.UpdateNormalizedUserNameAsync(user);
        await userManager.UpdateNormalizedEmailAsync(user);

        IList<string> currentRoles = await userManager.GetRolesAsync(user);
        await userManager.RemoveFromRolesAsync(user, currentRoles.Except(person.Roles));
        await userManager.AddToRolesAsync(user, person.Roles.Except(currentRoles));
    }

    // ---------------------------------------------------------------------------------------
    // Company data
    // ---------------------------------------------------------------------------------------

    public async Task SeedDataAsync(int companyId, CancellationToken ct)
    {
        // A context acting as the demo company: every row it creates is stamped with the demo's
        // CompanyId, and it can't write anywhere else.
        await using ApplicationDbContext ctx = new(options, new FixedTenantProvider(companyId));

        Dictionary<string, Location> locations = await ctx.Locations.Where(x => x.CompanyId == companyId).ToDictionaryAsync(x => x.Name, ct);
        Random random = new(RotaTime.Today(Now).DayNumber);

        Dictionary<string, StaffPosition> positions = SeedPositions(ctx, companyId);
        SeedSchedulingSettings(ctx, companyId, locations);
        await ctx.SaveChangesAsync(ct);

        SeedRota(ctx, locations, positions, random);
        SeedTimeOff(ctx, companyId, locations);
        SeedTraining(ctx, locations);
        SeedAnnouncements(ctx, locations);
        SeedStaffDocumentTypes(ctx, companyId);
        SeedOnboarding(ctx, companyId);
        SeedChat(ctx, companyId, locations);
        (Client arena, Client cafe) = SeedKiosks(ctx);
        SeedMusic(ctx);
        DmxScene gameOn = SeedDmx(ctx, arena);
        SeedAutomation(ctx, arena, gameOn);
        SeedGameResults(ctx, arena, random);
        SeedDashboards(ctx, arena, cafe);

        await ctx.SaveChangesAsync(ct);
    }

    private static Dictionary<string, StaffPosition> SeedPositions(ApplicationDbContext ctx, int companyId)
    {
        Dictionary<string, StaffPosition> positions = [];

        for (int i = 0; i < Positions.Length; i++)
        {
            StaffPosition position = new() { Id = Guid.NewGuid(), CompanyId = companyId, Name = Positions[i].Name, ColorIndex = Positions[i].Color, SortOrder = i, IsActive = true };
            positions[position.Name] = position;
            ctx.StaffPositions.Add(position);
        }

        foreach (Person person in People.Where(p => p.Position is not null))
        {
            ctx.UserPositions.Add(new UserPosition { Id = Guid.NewGuid(), UserId = person.Id, StaffPositionId = positions[person.Position!].Id, IsPrimary = true, CreateDate = DateTime.UtcNow });
        }

        return positions;
    }

    private void SeedSchedulingSettings(ApplicationDbContext ctx, int companyId, Dictionary<string, Location> locations)
    {
        ctx.CompanySchedulingSettings.Add(new CompanySchedulingSettings { Id = Guid.NewGuid(), CompanyId = companyId, UpdateDate = Now });

        foreach (Location location in locations.Values)
        {
            ctx.LocationSchedulingSettings.Add(new LocationSchedulingSettings { Id = Guid.NewGuid(), LocationId = location.Id, ClaimsNeedApproval = true, UpdateDate = Now });
        }

        ctx.ContractRequirements.Add(new ContractRequirement { Id = Guid.NewGuid(), CompanyId = companyId, MinHoursPerWeek = 8, MaxHoursPerWeek = 40, UpdateDate = Now });
    }

    // Two published weeks (this and next), a draft week after that for the Publish button, a
    // couple of open shifts to pick up, and clock-ins for the days already worked this week.
    private void SeedRota(ApplicationDbContext ctx, Dictionary<string, Location> locations, Dictionary<string, StaffPosition> positions, Random random)
    {
        DateOnly today = RotaTime.Today(Now);
        DateOnly weekStart = RotaTime.GetWeekStart(today);

        (TimeOnly Start, TimeOnly End, string Position)[] riversidePattern =
        [
            (new(9, 0), new(17, 0), "Duty manager"),
            (new(9, 30), new(15, 30), "Front desk"),
            (new(10, 0), new(18, 0), "Marshal"),
            (new(12, 0), new(20, 0), "Marshal"),
            (new(11, 0), new(17, 0), "Party host"),
            (new(9, 0), new(15, 0), "Café"),
            (new(15, 0), new(22, 0), "Marshal"),
        ];

        (TimeOnly Start, TimeOnly End, string Position)[] harboursidePattern =
        [
            (new(10, 0), new(18, 0), "Duty manager"),
            (new(10, 0), new(18, 0), "Marshal"),
            (new(11, 0), new(17, 0), "Front desk"),
            (new(12, 0), new(18, 0), "Café"),
        ];

        List<(Shift Shift, bool Worked)> worked = [];

        for (int day = 0; day < 21; day++)
        {
            DateOnly date = weekStart.AddDays(day);
            bool published = day < 14;

            worked.AddRange(AddDay(ctx, locations[Riverside], positions, riversidePattern, date, day, published, random));
            worked.AddRange(AddDay(ctx, locations[Harbourside], positions, harboursidePattern, date, day, published, random));
        }

        // Up for grabs next week: one open to anyone, one only for marshals.
        Shift openAny = NewShift(locations[Riverside], null, positions["Front desk"], weekStart.AddDays(9), new(12, 0), new(18, 0), published: true);
        Shift openMarshal = NewShift(locations[Riverside], null, positions["Marshal"], weekStart.AddDays(12), new(16, 0), new(22, 0), published: true);
        ctx.Shifts.AddRange(openAny, openMarshal);

        // Sam has asked to pick up the marshal shift - it's waiting in Shift Requests.
        ctx.ShiftClaims.Add(new ShiftClaim
        {
            Id = Guid.NewGuid(),
            ShiftId = openMarshal.Id,
            UserId = "lanyard-demo-sam",
            Kind = ShiftClaimKind.Pickup,
            Status = ShiftClaimStatus.Pending,
            Note = "Happy to do this one, I'm free all evening.",
            RequestedUtc = Now.AddHours(-3),
        });

        // Clock-ins for shifts that have already finished, so timesheets have something in them.
        foreach ((Shift shift, _) in worked.Where(x => x.Worked))
        {
            int lateMinutes = random.Next(-5, 8);
            int overMinutes = random.Next(0, 15);

            ctx.TimeEntries.Add(new TimeEntry
            {
                Id = Guid.NewGuid(),
                UserId = shift.UserId!,
                LocationId = shift.LocationId,
                ShiftId = shift.Id,
                ClockInUtc = shift.StartUtc.AddMinutes(lateMinutes),
                ClockOutUtc = shift.EndUtc.AddMinutes(overMinutes),
                ClockInMethod = ClockMethod.Pin,
                ClockOutMethod = ClockMethod.Pin,
                CreateDate = shift.StartUtc,
                IsActive = true,
            });
        }
    }

    private IEnumerable<(Shift, bool)> AddDay(
        ApplicationDbContext ctx, Location location, Dictionary<string, StaffPosition> positions,
        (TimeOnly Start, TimeOnly End, string Position)[] pattern, DateOnly date, int dayIndex, bool published, Random random)
    {
        // Quieter midweek: Tuesday and Wednesday drop the last two slots.
        int slots = date.DayOfWeek is DayOfWeek.Tuesday or DayOfWeek.Wednesday ? Math.Max(2, pattern.Length - 2) : pattern.Length;
        HashSet<string> workingToday = [];

        for (int i = 0; i < slots; i++)
        {
            (TimeOnly start, TimeOnly end, string positionName) = pattern[i];

            // Rotate people through the slots so everyone gets a few shifts a week: one shift a
            // day each, and everyone has two days off (their own pair of weekdays).
            Person[] candidates = People
                .Where(p => p.Position == positionName && p.Locations.Contains(location.Name))
                .Where(p => !workingToday.Contains(p.Id))
                .Where(p => !IsDayOff(p, date))
                .ToArray();

            if (candidates.Length == 0)
            {
                continue;
            }

            Person person = candidates[(dayIndex + i) % candidates.Length];
            workingToday.Add(person.Id);

            Shift shift = NewShift(location, person.Id, positions[positionName], date, start, end, published);
            shift.BreakMinutes = (end - start).TotalHours >= 6 ? 30 : 0;
            ctx.Shifts.Add(shift);

            yield return (shift, shift.EndUtc < Now);
        }

        _ = random;
    }

    private static bool IsDayOff(Person person, DateOnly date)
    {
        int first = Array.IndexOf(People, person) % 7;
        int day = ((int)date.DayOfWeek + 6) % 7;

        return day == first || day == (first + 3) % 7;
    }

    private Shift NewShift(Location location, string? userId, StaffPosition position, DateOnly date, TimeOnly start, TimeOnly end, bool published)
    {
        DateTime startUtc = RotaTime.ToUtc(date, start);

        return new Shift
        {
            Id = Guid.NewGuid(),
            LocationId = location.Id,
            UserId = userId,
            StaffPositionId = position.Id,
            StartUtc = startUtc,
            EndUtc = RotaTime.ToUtc(date, end),
            IsActive = true,
            CreateDate = Now.AddDays(-10),
            CreateByUserId = DemoAccounts.ManagerUserId,
            PublishedDateUtc = published ? Now.AddDays(-7) : null,
            PublishedByUserId = published ? DemoAccounts.ManagerUserId : null,
            PublishedStartUtc = published ? startUtc : null,
        };
    }

    private void SeedTimeOff(ApplicationDbContext ctx, int companyId, Dictionary<string, Location> locations)
    {
        TimeOffType annual = new() { Id = Guid.NewGuid(), CompanyId = companyId, Name = "Annual leave", IsPaid = true, DeductsFromAllowance = true, SortOrder = 0, IsActive = true };
        TimeOffType unpaid = new() { Id = Guid.NewGuid(), CompanyId = companyId, Name = "Unpaid leave", IsPaid = false, DeductsFromAllowance = false, SortOrder = 1, IsActive = true };
        TimeOffType sickness = new() { Id = Guid.NewGuid(), CompanyId = companyId, Name = "Sickness", IsPaid = true, DeductsFromAllowance = false, SortOrder = 2, IsActive = true };
        ctx.TimeOffTypes.AddRange(annual, unpaid, sickness);

        ctx.TimeOffAllowances.Add(new TimeOffAllowance { Id = Guid.NewGuid(), CompanyId = companyId, TimeOffTypeId = annual.Id, AllowanceHours = 224, UpdateDate = Now });

        DateOnly today = RotaTime.Today(Now);
        int riverside = locations[Riverside].Id;

        ctx.TimeOffRequests.AddRange(
            Request(DemoAccounts.StaffUserId, annual, riverside, today.AddDays(24), 2, TimeOffStatus.Pending, "Family wedding."),
            Request("lanyard-demo-ellie", annual, riverside, today.AddDays(17), 5, TimeOffStatus.Pending, "Holiday booked a while back."),
            Request("lanyard-demo-sam", annual, riverside, today.AddDays(-12), 3, TimeOffStatus.Approved, null),
            Request("lanyard-demo-tom", unpaid, riverside, today.AddDays(6), 1, TimeOffStatus.Rejected, "Driving test.", "Sorry Tom, we're short that day - can you move it?"),
            Request("lanyard-demo-maya", sickness, riverside, today.AddDays(-5), 1, TimeOffStatus.Approved, null),
            Request(DemoAccounts.StaffUserId, annual, riverside, today.AddDays(-30), 2, TimeOffStatus.Approved, null));
    }

    private TimeOffRequest Request(string userId, TimeOffType type, int locationId, DateOnly start, int days, TimeOffStatus status, string? notes, string? reason = null)
    {
        bool decided = status is TimeOffStatus.Approved or TimeOffStatus.Rejected;

        return new TimeOffRequest
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TimeOffTypeId = type.Id,
            LocationId = locationId,
            StartDate = start,
            EndDate = start.AddDays(days - 1),
            Hours = days * 8,
            Notes = notes,
            Status = status,
            RequestedDateUtc = Now.AddDays(-4),
            RequestedByUserId = userId,
            DecidedByUserId = decided ? DemoAccounts.ManagerUserId : null,
            DecidedDateUtc = decided ? Now.AddDays(-2) : null,
            DecisionReason = reason,
        };
    }

    private void SeedTraining(ApplicationDbContext ctx, Dictionary<string, Location> locations)
    {
        Course fire = NewCourse("Fire Safety Essentials", "What to do if the alarm goes off, and how to keep the building safe every day.", null, isShared: true, recurrenceMonths: 12,
            [
                ("Before the alarm", "<p>Know your nearest two exits, where the fire points are and where the extinguishers live. Keep fire doors shut and exits clear at all times.</p>"),
                ("When the alarm sounds", "<p>Stop the game, turn the arena lights up, and lead your group calmly to the assembly point in the car park. Never go back inside.</p>"),
            ],
            [
                ("Where do you take guests when the alarm sounds?", ["The assembly point in the car park", "The café", "Wait in the arena"], 0),
                ("A fire door is propped open with a chair. What should you do?", ["Leave it, it's airy", "Remove the chair and let it close", "Put a sign on it"], 1),
                ("Can you go back in for your bag?", ["Yes, if you're quick", "Only with a manager", "No, never"], 2),
            ]);

        Course marshalling = NewCourse("Laser Arena Marshalling", "Briefing players, running a game and keeping the arena safe.", locations[Riverside].Id, isShared: false, recurrenceMonths: null,
            [
                ("The briefing", "<p>Every game starts with the safety briefing: no running, no physical contact, and vests stay on. Check shoelaces and tuck in anything dangling.</p>"),
                ("During the game", "<p>Keep moving around the arena, watch the blind corners and stop the game straight away if anyone is hurt.</p>"),
            ],
            [
                ("When do you give the safety briefing?", ["Before every game", "Only for new players", "If there's time"], 0),
                ("A player falls over. What do you do?", ["Keep the game going", "Stop the game and check on them", "Tell them to get up"], 1),
            ]);

        Course food = NewCourse("Food Hygiene Basics", "Handling food safely in the café and at parties.", null, isShared: true, recurrenceMonths: 24,
            [
                ("Clean hands, clean surfaces", "<p>Wash your hands before handling food and after clearing tables. Use the blue cloths for food areas only.</p>"),
            ],
            [
                ("When should you wash your hands?", ["Before handling food", "Only at the start of a shift", "When they look dirty"], 0),
            ]);

        ctx.Courses.AddRange(fire, marshalling, food);

        DateTime now = Now;

        foreach (Person person in People.Where(p => p.Locations.Contains(Riverside)))
        {
            int n = Array.IndexOf(People, person);

            // A spread of states across the team: done, in progress, overdue, not started.
            Assign(ctx, fire, person.Id, locations[Riverside].Id, (n % 4) switch
            {
                0 => AssignmentState.Completed,
                1 => AssignmentState.InProgress,
                2 => AssignmentState.Overdue,
                _ => AssignmentState.Completed,
            }, now);

            if (person.Position is "Marshal" || person.Id == DemoAccounts.StaffUserId)
            {
                Assign(ctx, marshalling, person.Id, locations[Riverside].Id, n % 2 == 0 ? AssignmentState.InProgress : AssignmentState.NotStarted, now);
            }

            if (person.Position is "Café" or "Party host")
            {
                Assign(ctx, food, person.Id, locations[Riverside].Id, AssignmentState.NotStarted, now);
            }
        }

        // The demo staff account gets one of each so My Training has something in every state.
        Assign(ctx, food, DemoAccounts.StaffUserId, locations[Riverside].Id, AssignmentState.Overdue, now);

        foreach (Person person in People.Where(p => p.Locations.Contains(Harbourside) && !p.Locations.Contains(Riverside)))
        {
            Assign(ctx, fire, person.Id, locations[Harbourside].Id, AssignmentState.Completed, now);
        }
    }

    private enum AssignmentState { NotStarted, InProgress, Completed, Overdue }

    private static Course NewCourse(string name, string description, int? locationId, bool isShared, int? recurrenceMonths,
        (string Title, string Html)[] sections, (string Text, string[] Options, int Correct)[] questions)
    {
        Course course = new() { Id = Guid.NewGuid(), Name = name, Description = description, LocationId = locationId, IsShared = isShared, RecurrenceMonths = recurrenceMonths, PassMarkPercent = 80, IsActive = true };

        for (int i = 0; i < sections.Length; i++)
        {
            course.Sections.Add(new CourseSection { Id = Guid.NewGuid(), CourseId = course.Id, Title = sections[i].Title, BodyHtml = sections[i].Html, SortOrder = i, IsActive = true });
        }

        for (int i = 0; i < questions.Length; i++)
        {
            CourseQuestion question = new() { Id = Guid.NewGuid(), CourseId = course.Id, QuestionText = questions[i].Text, SortOrder = i, IsActive = true };

            for (int o = 0; o < questions[i].Options.Length; o++)
            {
                question.Options.Add(new CourseQuestionOption { Id = Guid.NewGuid(), QuestionId = question.Id, OptionText = questions[i].Options[o], IsCorrect = o == questions[i].Correct, SortOrder = o, IsActive = true });
            }

            course.Questions.Add(question);
        }

        return course;
    }

    private static void Assign(ApplicationDbContext ctx, Course course, string userId, int locationId, AssignmentState state, DateTime now)
    {
        CourseAssignment assignment = new()
        {
            Id = Guid.NewGuid(),
            CourseId = course.Id,
            UserId = userId,
            AssignedByUserId = DemoAccounts.ManagerUserId,
            AssignedDate = now.AddDays(-21),
            DueDate = state == AssignmentState.Overdue ? now.AddDays(-3) : now.AddDays(14),
            LocationId = locationId,
            IsActive = true,
        };

        List<CourseSection> sections = [.. course.Sections.OrderBy(x => x.SortOrder)];

        if (state is AssignmentState.InProgress or AssignmentState.Completed)
        {
            assignment.StartedDate = now.AddDays(-6);
            int entered = state == AssignmentState.Completed ? sections.Count : 1;

            foreach (CourseSection section in sections.Take(entered))
            {
                ctx.CourseSectionProgresses.Add(new CourseSectionProgress { Id = Guid.NewGuid(), AssignmentId = assignment.Id, SectionId = section.Id, EnteredDate = now.AddDays(-6), LeftDate = now.AddDays(-6).AddMinutes(4) });
            }
        }

        if (state == AssignmentState.Completed)
        {
            assignment.CompletedDate = now.AddDays(-5);
            ctx.CourseQuizAttempts.Add(new CourseQuizAttempt { Id = Guid.NewGuid(), AssignmentId = assignment.Id, AttemptNumber = 1, SubmittedDate = now.AddDays(-5), ScorePercent = 100, Passed = true });
        }

        ctx.CourseAssignments.Add(assignment);
    }

    private void SeedAnnouncements(ApplicationDbContext ctx, Dictionary<string, Location> locations)
    {
        ctx.Announcements.AddRange(
            NewAnnouncement(locations[Riverside].Id, "Welcome to the Lanyard demo!", "This is a made-up venue for you to explore. Try the rota, approve some time off, take a course or post in the team chat - it all resets tonight.", pinned: true, daysAgo: 0),
            NewAnnouncement(locations[Riverside].Id, "Half-term is coming", "We're open 9am-10pm every day next week. Please check your shifts and pick up an open one if you can.", pinned: false, daysAgo: 1),
            NewAnnouncement(locations[Riverside].Id, "New vests in the arena", "The new vests are charging in the kit room. Please plug them back in after every game.", pinned: false, daysAgo: 4),
            NewAnnouncement(locations[Harbourside].Id, "Café menu update", "Hot dogs are back on the menu from Saturday.", pinned: true, daysAgo: 2));
    }

    private Announcement NewAnnouncement(int locationId, string title, string body, bool pinned, int daysAgo) => new()
    {
        Id = Guid.NewGuid(),
        LocationId = locationId,
        Title = title,
        Body = body,
        IsPinned = pinned,
        IsActive = true,
        CreateDate = Now.AddDays(-daysAgo).AddHours(-1),
        CreatedByUserId = DemoAccounts.ManagerUserId,
    };

    private static void SeedStaffDocumentTypes(ApplicationDbContext ctx, int companyId)
    {
        ctx.StaffDocumentTypes.AddRange(
            new StaffDocumentType { Id = Guid.NewGuid(), CompanyId = companyId, Name = "Right to work", RequiresExpiryDate = false, SortOrder = 0, IsActive = true },
            new StaffDocumentType { Id = Guid.NewGuid(), CompanyId = companyId, Name = "First aid certificate", RequiresExpiryDate = true, SortOrder = 1, IsActive = true },
            new StaffDocumentType { Id = Guid.NewGuid(), CompanyId = companyId, Name = "DBS check", RequiresExpiryDate = true, SortOrder = 2, IsActive = true });
    }

    private void SeedOnboarding(ApplicationDbContext ctx, int companyId)
    {
        ctx.CompanyOnboardingSettings.Add(new CompanyOnboardingSettings
        {
            Id = Guid.NewGuid(),
            CompanyId = companyId,
            SendWelcomeEmail = true,
            WelcomeEmailSubject = "Welcome to Starlight Leisure!",
            WelcomeEmailBodyHtml = "<p>Hi there, and welcome to the team!</p><p>Your first shift is on the rota in Lanyard. Please complete your training before you start.</p>",
            AutoAttachStandingDocuments = false,
            UpdateDate = Now,
        });
    }

    private void SeedChat(ApplicationDbContext ctx, int companyId, Dictionary<string, Location> locations)
    {
        DateTime now = Now;

        ChatConversation everyone = new() { Id = Guid.NewGuid(), CompanyId = companyId, Kind = ChatConversationKind.CompanyChannel, Name = $"{CompanyName} everyone", CreateDate = now.AddDays(-30), IsActive = true };
        ChatConversation riverside = new() { Id = Guid.NewGuid(), CompanyId = companyId, Kind = ChatConversationKind.LocationChannel, LocationId = locations[Riverside].Id, Name = $"{Riverside} team", CreateDate = now.AddDays(-30), IsActive = true };
        ChatConversation hosts = new() { Id = Guid.NewGuid(), CompanyId = companyId, Kind = ChatConversationKind.Group, Name = "Party hosts", CreatedByUserId = DemoAccounts.ManagerUserId, CreateDate = now.AddDays(-12), IsActive = true };
        ChatConversation direct = new() { Id = Guid.NewGuid(), CompanyId = companyId, Kind = ChatConversationKind.Direct, DirectKey = DirectKey(DemoAccounts.StaffUserId, DemoAccounts.ManagerUserId), CreateDate = now.AddDays(-2), IsActive = true };
        ctx.ChatConversations.AddRange(everyone, riverside, hosts, direct);

        foreach (Person person in People)
        {
            AddMember(ctx, everyone, person.Id, now, isAdmin: false);

            if (person.Locations.Contains(Riverside))
            {
                AddMember(ctx, riverside, person.Id, now, isAdmin: false);
            }
        }

        foreach (string userId in new[] { DemoAccounts.ManagerUserId, DemoAccounts.AdminUserId, DemoAccounts.StaffUserId, "lanyard-demo-ellie", "lanyard-demo-chloe" })
        {
            AddMember(ctx, hosts, userId, now, isAdmin: userId == DemoAccounts.ManagerUserId);
        }

        AddMember(ctx, direct, DemoAccounts.StaffUserId, now, isAdmin: false);
        AddMember(ctx, direct, DemoAccounts.ManagerUserId, now, isAdmin: false);

        Message(ctx, everyone, DemoAccounts.AdminUserId, "Welcome to Starlight Leisure's team chat! Channels for each site, groups for teams, and direct messages.", now.AddDays(-6), pinned: true);
        Message(ctx, everyone, "lanyard-demo-ryan", "Harbourside is fully booked for parties this Saturday - thanks everyone who picked up extra shifts.", now.AddDays(-1).AddHours(-3));

        Message(ctx, riverside, DemoAccounts.ManagerUserId, "Morning all! Reminder that the arena vests need charging after every game.", now.AddHours(-26), pinned: true);
        Message(ctx, riverside, "lanyard-demo-sam", "Arena 2's door is sticking again, I've put a note on it.", now.AddHours(-5));
        Message(ctx, riverside, "lanyard-demo-tom", "Thanks Sam, I'll let maintenance know.", now.AddHours(-4).AddMinutes(-20));
        Message(ctx, riverside, "lanyard-demo-ellie", "Anyone fancy swapping my Saturday morning for a Sunday?", now.AddMinutes(-50));

        Message(ctx, hosts, DemoAccounts.ManagerUserId, "We've got three birthday parties on Saturday. I'll put the running order up in here.", now.AddDays(-1));
        Message(ctx, hosts, "lanyard-demo-chloe", "Can we get more party bags ordered? We're down to the last box.", now.AddHours(-7));

        Message(ctx, direct, DemoAccounts.ManagerUserId, "Hi Jordan, would you be able to do a couple of extra marshal shifts next week?", now.AddHours(-2));
    }

    private static string DirectKey(string a, string b) => string.CompareOrdinal(a, b) <= 0 ? $"{a}|{b}" : $"{b}|{a}";

    private static void AddMember(ApplicationDbContext ctx, ChatConversation conversation, string userId, DateTime now, bool isAdmin)
    {
        // Read up to a day ago, so the newest messages show as unread.
        ctx.ChatMembers.Add(new ChatMember { Id = Guid.NewGuid(), ConversationId = conversation.Id, UserId = userId, JoinedUtc = now.AddDays(-30), LastReadUtc = now.AddDays(-1), IsGroupAdmin = isAdmin });
    }

    private static void Message(ApplicationDbContext ctx, ChatConversation conversation, string authorId, string text, DateTime at, bool pinned = false)
    {
        ChatHtml.Cleaned cleaned = ChatHtml.Clean($"<p>{System.Net.WebUtility.HtmlEncode(text)}</p>")!;

        ctx.ChatMessages.Add(new ChatMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            AuthorUserId = authorId,
            BodyHtml = cleaned.Html,
            BodyText = cleaned.Text,
            CreateUtc = at,
            Kind = ChatMessageKind.Text,
            IsPinned = pinned,
            PinnedByUserId = pinned ? authorId : null,
            PinnedUtc = pinned ? at : null,
        });

        // LastMessageUtc orders the inbox and shows as its time, so it tracks the newest message.
        if (at > conversation.LastMessageUtc)
        {
            conversation.LastMessageUtc = at;
        }
    }

    private (Client Arena, Client Cafe) SeedKiosks(ApplicationDbContext ctx)
    {
        // Offline "kiosks": the venue PCs a real site would have. There's no hardware behind them.
        Client arena = new() { Id = Guid.NewGuid(), Name = "Riverside arena PC", Notes = "Drives the arena lights, music and scoreboard.", CreateDate = Now.AddDays(-90), LastLogin = Now.AddDays(-1), LastDisconnectDate = Now.AddDays(-1).AddHours(8) };
        Client cafe = new() { Id = Guid.NewGuid(), Name = "Riverside café PC", Notes = "Café music and the menu screen.", CreateDate = Now.AddDays(-90), LastLogin = Now.AddDays(-1), LastDisconnectDate = Now.AddDays(-1).AddHours(8) };
        ctx.Clients.AddRange(arena, cafe);

        return (arena, cafe);
    }

    private void SeedMusic(ApplicationDbContext ctx)
    {
        (string Name, string Album, double Seconds, double Bpm)[] tracks =
        [
            ("Neon Rush", "Arena Anthems", 212, 128), ("Countdown", "Arena Anthems", 198, 132), ("Laser Lights", "Arena Anthems", 225, 126),
            ("Final Round", "Arena Anthems", 240, 140), ("Victory Lap", "Arena Anthems", 205, 124),
            ("Slow Sunday", "Café Mornings", 256, 92), ("Oat Latte", "Café Mornings", 231, 88), ("Window Seat", "Café Mornings", 244, 96),
        ];

        List<Song> songs = [];

        foreach ((string name, string album, double seconds, double bpm) in tracks)
        {
            Song song = new() { Id = Guid.NewGuid(), Name = name, AlbumName = album, FilePath = $"demo/{name.ToLowerInvariant().Replace(' ', '-')}.mp3", DurationSeconds = seconds, CreateDate = Now.AddDays(-60), IsActive = true, Bpm = bpm, BpmAnalysisStatus = BpmAnalysisStatus.TagOnly, BpmAnalysisDate = Now.AddDays(-60) };
            songs.Add(song);
            ctx.Songs.Add(song);
        }

        AddPlaylist(ctx, "Arena warm-up", "High-energy tracks for game time.", songs.Take(5));
        AddPlaylist(ctx, "Café chill", "Easy listening for the café.", songs.Skip(5));
    }

    private void AddPlaylist(ApplicationDbContext ctx, string name, string description, IEnumerable<Song> songs)
    {
        Playlist playlist = new() { Id = Guid.NewGuid(), Name = name, Description = description, CreateByUserId = DemoAccounts.AdminUserId, CreateDate = Now.AddDays(-50) };
        ctx.Playlists.Add(playlist);

        foreach (Song song in songs)
        {
            ctx.PlaylistSongMembers.Add(new PlaylistSongMember { SongId = song.Id, PlaylistId = playlist.Id, CreateByUserId = DemoAccounts.AdminUserId, CreateDate = Now.AddDays(-50) });
        }
    }

    private DmxScene SeedDmx(ApplicationDbContext ctx, Client arena)
    {
        for (int i = 0; i < 4; i++)
        {
            ctx.DmxFixtures.Add(new DmxFixture { Id = Guid.NewGuid(), ClientId = arena.Id, Name = $"Arena par {i + 1}", StartChannel = 1 + (i * 3), FixtureType = DmxFixtureType.Rgb, IsActive = true, CreateDate = Now, CreateByUserId = DemoAccounts.AdminUserId });
        }

        DmxScene standby = NewScene(arena, "Arena standby", loop: false, bpmSync: false, [[(0, 40, 120)]]);
        DmxScene gameOn = NewScene(arena, "Game on chase", loop: true, bpmSync: true, [[(255, 0, 0)], [(0, 255, 0)], [(0, 0, 255)]]);
        ctx.DmxScenes.AddRange(standby, gameOn);

        return gameOn;
    }

    private DmxScene NewScene(Client arena, string name, bool loop, bool bpmSync, (byte R, byte G, byte B)[][] steps)
    {
        DmxScene scene = new() { Id = Guid.NewGuid(), ClientId = arena.Id, Name = name, Loop = loop, BpmSyncEnabled = bpmSync, IsActive = true, CreateDate = Now, CreateByUserId = DemoAccounts.AdminUserId };

        for (int s = 0; s < steps.Length; s++)
        {
            DmxSceneStep step = new() { Id = Guid.NewGuid(), SceneId = scene.Id, StepNumber = s + 1, Name = $"Step {s + 1}", Duration = TimeSpan.FromMilliseconds(500), Beats = 1, CreateDate = Now, CreateByUserId = DemoAccounts.AdminUserId };
            (byte r, byte g, byte b) = steps[s][0];

            // Same colour on all four pars.
            for (int fixture = 0; fixture < 4; fixture++)
            {
                int first = 1 + (fixture * 3);
                step.ChannelValues.Add(new DmxSceneStepChannelValue { Id = Guid.NewGuid(), SceneStepId = step.Id, ChannelNumber = first, Value = r, CreateDate = Now, CreateByUserId = DemoAccounts.AdminUserId });
                step.ChannelValues.Add(new DmxSceneStepChannelValue { Id = Guid.NewGuid(), SceneStepId = step.Id, ChannelNumber = first + 1, Value = g, CreateDate = Now, CreateByUserId = DemoAccounts.AdminUserId });
                step.ChannelValues.Add(new DmxSceneStepChannelValue { Id = Guid.NewGuid(), SceneStepId = step.Id, ChannelNumber = first + 2, Value = b, CreateDate = Now, CreateByUserId = DemoAccounts.AdminUserId });
            }

            scene.Steps.Add(step);
        }

        return scene;
    }

    private void SeedAutomation(ApplicationDbContext ctx, Client arena, DmxScene gameOn)
    {
        AutomationRule rule = new()
        {
            Id = Guid.NewGuid(),
            Name = "Game starts: lights to chase",
            TriggerClientId = arena.Id,
            TriggerType = AutomationTriggerType.GameStatusTransition,
            TriggerEvent = GameStatus.InGame,
            IsActive = true,
            IsEnabled = true,
            CreateDate = Now.AddDays(-20),
        };

        rule.Actions.Add(new AutomationRuleAction
        {
            Id = Guid.NewGuid(),
            AutomationRuleId = rule.Id,
            ActionType = AutomationActionTypes.DmxSceneControl,
            ParametersJson = System.Text.Json.JsonSerializer.Serialize(new { TargetClientId = arena.Id, Operation = "StartScene", SceneId = gameOn.Id }),
            SortOrder = 0,
            IsActive = true,
        });

        ctx.AutomationRules.Add(rule);
    }

    // A month of laser tag games in the arena, for the Hall of Fame widget.
    private void SeedGameResults(ApplicationDbContext ctx, Client arena, Random random)
    {
        for (int day = 0; day < 30; day++)
        {
            int games = random.Next(1, 4);

            for (int g = 0; g < games; g++)
            {
                GameResult result = new() { Id = Guid.NewGuid(), ClientId = arena.Id, PlayedAtUtc = Now.AddDays(-day).AddHours(-(g * 2) - 1), DurationSeconds = 900 };
                int players = random.Next(6, 11);

                for (int p = 0; p < players; p++)
                {
                    result.PlayerScores.Add(new GameResultPlayerScore
                    {
                        Id = Guid.NewGuid(),
                        GameResultId = result.Id,
                        GunId = p + 1,
                        Score = random.Next(800, 9500),
                        Accuracy = random.Next(12, 68),
                        Team = p % 2 == 0 ? Team.Red : Team.Green,
                    });
                }

                ctx.GameResults.Add(result);
            }
        }
    }

    // The company's home screen: set as the organisation default, so every demo account lands on it.
    private void SeedDashboards(ApplicationDbContext ctx, Client arena, Client cafe)
    {
        Dashboard home = new() { Id = Guid.NewGuid(), Name = "Team home", Description = "What everyone sees when they sign in.", IsActive = true, CreateDate = Now.AddDays(-30) };
        home.Widgets.Add(Place(new GreetingWidget(), 0, 0, 8, 1));
        home.Widgets.Add(Place(new DigitalClockWidget(), 8, 0, 4, 1));
        home.Widgets.Add(Place(new MyShiftsWidget { Title = "My shifts" }, 0, 1, 4, 4));
        home.Widgets.Add(Place(new WhoIsOnTodayWidget { Title = "Who's on today" }, 4, 1, 4, 4));
        home.Widgets.Add(Place(new AnnouncementsWidget { Title = "Announcements" }, 8, 1, 4, 4));
        home.Widgets.Add(Place(new MyTrainingWidget { Title = "My training" }, 0, 5, 4, 3));
        home.Widgets.Add(Place(new HallOfFameWidget { Title = "Hall of fame", Period = HallOfFamePeriod.ThisMonth, ClientId = arena.Id }, 4, 5, 4, 3));
        home.Widgets.Add(Place(new PendingTimeOffWidget { Title = "Time off to approve" }, 8, 5, 4, 3));

        Dashboard venue = new() { Id = Guid.NewGuid(), Name = "Venue control", Description = "Music, lighting and kiosks at Riverside.", IsActive = true, CreateDate = Now.AddDays(-30) };
        venue.Widgets.Add(Place(new KioskHealthWidget { Title = "Kiosks" }, 0, 0, 6, 3));
        venue.Widgets.Add(Place(new MusicPlaylistSelectorWidget { Title = "Café music", ClientId = cafe.Id }, 6, 0, 6, 3));
        venue.Widgets.Add(Place(new MusicTimelineWidget { Title = "Arena music", ClientId = arena.Id }, 0, 3, 12, 2));

        ctx.Dashboards.AddRange(home, venue);

        ctx.AppSettings.Add(new AppSetting { Id = Guid.NewGuid(), Key = "Dashboard.OrganisationDefaultDashboardId", Value = home.Id.ToString(), CreateDate = Now });
    }

    private static DashboardWidget Place(DashboardWidget widget, int x, int y, int w, int h)
    {
        widget.GridX = x;
        widget.GridY = y;
        widget.GridW = w;
        widget.GridH = h;
        widget.IsActive = true;

        return widget;
    }
}
