using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Lanyard.Infrastructure.DataAccess;
using Lanyard.Infrastructure.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;

namespace Lanyard.Tests.Integration;

// Identity's UserStore with the passkey methods kept in memory instead of in EF.
//
// Identity maps a passkey's details as an owned type stored as JSON (the jsonb Data column on
// Postgres), and EF InMemory - which the integration tests run on - doesn't round-trip it: rows
// come back with Data null and Identity throws a NullReferenceException. Everything else in
// the pipeline (AuthController, SignInManager, the WebAuthn checks, location claims, cookies)
// is the real code; only where the passkey rows live differs. The real EF mapping is covered
// by the migration and by running the app against Postgres.
internal sealed class InMemoryPasskeyUserStore(
    ApplicationDbContext context,
    InMemoryPasskeyUserStore.PasskeyTable table,
    IdentityErrorDescriber? describer = null)
    : UserStore<UserProfile, ApplicationRole, ApplicationDbContext, string>(context, describer)
{
    // One per test host: register as a singleton so every request's store sees the same rows.
    public sealed class PasskeyTable
    {
        public ConcurrentDictionary<string, (string UserId, UserPasskeyInfo Passkey)> Rows { get; } = new();
    }

    private static string Key(byte[] credentialId) => Convert.ToHexString(credentialId);

    public override Task AddOrUpdatePasskeyAsync(UserProfile user, UserPasskeyInfo passkey, CancellationToken cancellationToken)
    {
        table.Rows[Key(passkey.CredentialId)] = (user.Id, passkey);
        return Task.CompletedTask;
    }

    public override Task<IList<UserPasskeyInfo>> GetPasskeysAsync(UserProfile user, CancellationToken cancellationToken)
    {
        IList<UserPasskeyInfo> passkeys = table.Rows.Values.Where(x => x.UserId == user.Id).Select(x => x.Passkey).ToList();
        return Task.FromResult(passkeys);
    }

    public override async Task<UserProfile?> FindByPasskeyIdAsync(byte[] credentialId, CancellationToken cancellationToken)
    {
        return table.Rows.TryGetValue(Key(credentialId), out (string UserId, UserPasskeyInfo Passkey) row)
            ? await FindByIdAsync(row.UserId, cancellationToken)
            : null;
    }

    public override Task<UserPasskeyInfo?> FindPasskeyAsync(UserProfile user, byte[] credentialId, CancellationToken cancellationToken)
    {
        UserPasskeyInfo? passkey = table.Rows.TryGetValue(Key(credentialId), out (string UserId, UserPasskeyInfo Passkey) row) && row.UserId == user.Id
            ? row.Passkey
            : null;

        return Task.FromResult(passkey);
    }

    public override Task RemovePasskeyAsync(UserProfile user, byte[] credentialId, CancellationToken cancellationToken)
    {
        if (table.Rows.TryGetValue(Key(credentialId), out (string UserId, UserPasskeyInfo Passkey) row) && row.UserId == user.Id)
        {
            table.Rows.TryRemove(Key(credentialId), out _);
        }

        return Task.CompletedTask;
    }
}
