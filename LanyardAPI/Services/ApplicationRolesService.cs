using LanyardData.DataAccess;
using LanyardData.Models;
using Microsoft.EntityFrameworkCore;

namespace LanyardAPI.Services
{
    public class ApplicationRolesService(IDbContextFactory<ApplicationDbContext> factory, SecurityService secApi)
    {
        private readonly IDbContextFactory<ApplicationDbContext> _factory = factory;
        private readonly SecurityService _secApi = secApi;

        public async Task<List<ApplicationRole>> GetAllApplicationRolesAsync()
        {
            using ApplicationDbContext ctx = _factory.CreateDbContext();

            return await ctx.Roles.Include(x=> x.CreatedByUser).Where(x => x.IsActive).ToListAsync();
        }

        public async Task CreateNewRoleAsync(string RoleName)
        {
            using ApplicationDbContext ctx = _factory.CreateDbContext();

            ApplicationRole newRole = new()
            {
                Name = RoleName,
                CreateDate = DateTime.Now,
                CreatedByUserId = await _secApi.GetCurrentUserIdAsync()
            };

            await ctx.AddAsync(newRole);

            await ctx.SaveChangesAsync();

            return;
        }

        public async Task<IEnumerable<ApplicationUserRole>> GetUserRolesAsync(string UserId)
        {
            using ApplicationDbContext ctx = _factory.CreateDbContext();

            return await ctx.UserRoles
                .Include(x=> x.Role)
                .Where(x => x.UserId == UserId)
                .Where(x=> x.IsActive == true)
                .ToListAsync();
        }

        public async Task UnassignRoleFromAllUsers(string RoleId)
        {
            using ApplicationDbContext ctx = _factory.CreateDbContext();

            await ctx.UserRoles
                .Where(x => x.RoleId == RoleId)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.IsActive, r => false));
        }

        public async Task DeleteRole(string RoleId)
        {
            using ApplicationDbContext ctx = _factory.CreateDbContext();

            await UnassignRoleFromAllUsers(RoleId);

            (await ctx.Roles.Where(x => x.Id == RoleId).FirstOrDefaultAsync())?.IsActive = false;

            await ctx.SaveChangesAsync();
        }
    }
}
