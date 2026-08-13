---
name: service-testing-patterns
description: The actual EF InMemory + Moq test-setup pattern used across all 17 DB-touching test classes in src/Lanyard.Tests — no shared base class, but a very consistent copy-paste convention (GetInMemoryOptions() + Mock<IDbContextFactory<T>>). Use whenever writing a new test for a service that uses IDbContextFactory, or when testing anything backed by ASP.NET Core Identity (UserManager), which needs a different approach.
---

# Service test setup pattern

`src/Lanyard.Tests` has no shared test-base class. Instead, every DB-touching test class (17 of them — `ClientServiceTests`, `SecurityServiceTests`, `FileServiceTests`, `AutomationRuleServiceTests`, `DashboardServiceTests`, etc.) hand-rolls the same small helper, copy-pasted near-verbatim. Follow this pattern rather than inventing a new one or introducing a shared base class — consistency with the other 17 files matters more here than DRY-ing it up.

## The standard pattern

```csharp
private DbContextOptions<ApplicationDbContext> GetInMemoryOptions()
{
    return new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .Options;
}

private ClientService GetService(DbContextOptions<ApplicationDbContext> options)
{
    var factoryMock = new Mock<IDbContextFactory<ApplicationDbContext>>();
    factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
        .ReturnsAsync(() => new ApplicationDbContext(options));

    // ...mock any other constructor dependencies (SignalR hubs, other services) the same way...

    return new ClientService(factoryMock.Object, /* ... */);
}
```

`Guid.NewGuid().ToString()` as the InMemory database name guarantees a fresh, isolated database every time `GetInMemoryOptions()` is called — that's what gives test isolation, not `[TestInitialize]`/`[TestCleanup]`. Because the service under test consumes `IDbContextFactory<ApplicationDbContext>` (per the repo-wide DI rule — see CLAUDE.md), the mock only needs to make `CreateDbContextAsync` return a real `ApplicationDbContext` built from the shared `options`; you don't need to mock EF itself.

Within a test, seed data either through the real `ApplicationDbContext` directly (`ctx.Clients.Add(client); await ctx.SaveChangesAsync();`) or by calling the service's own create method first — both are used across the existing suite depending on what's being tested. Every non-DB dependency (loggers, SignalR hub contexts, `IWebHostEnvironment`, other injected services) gets mocked the same uniform way: `new Mock<T>()` + `.Setup(...).Returns/ReturnsAsync(...)`.

**Canonical example**: `src/Lanyard.Tests/Services/Clients/ClientServiceTests.cs` — simple CRUD, straightforward mocks, good template to copy for a new service test class.

## Testing anything backed by ASP.NET Core Identity

`UserManager<UserProfile>`'s default token provider is internal, so it can't just be mocked like everything else. `src/Lanyard.Tests/Services/Security/SecurityServiceTests.cs` builds a real `ServiceCollection`/DI container to get a working `UserManager` with a real token provider, rather than trying to mock around it. If you're testing anything involving password reset tokens, email confirmation tokens, or other Identity-token-dependent flows, use `SecurityServiceTests.cs` as the reference, not `ClientServiceTests.cs` — the plain-mock approach won't work for token generation/validation.

## Asserting results

Assert on `Result<T>` directly (`Assert.IsTrue(result.Success)`, `Assert.IsTrue(result.Success, result.Error)` to surface the failure message on assert failure) — no custom assertion helpers exist for the `Result<T>` pattern.
