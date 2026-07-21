using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReleaseManager.Api.Data;
using ReleaseManager.Api.Domain;

namespace ReleaseManager.Api.Infrastructure;

public sealed class SessionAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, System.Text.Encodings.Web.UrlEncoder encoder, IServiceScopeFactory scopes)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var value = Request.Headers.Authorization.ToString();
        if (!value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && Request.Path.StartsWithSegments("/hubs") && Request.Query.TryGetValue("access_token", out var accessToken))
            value = $"Bearer {accessToken}";
        if (!value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return AuthenticateResult.NoResult();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value[7..].Trim())));
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = await db.Sessions.Include(x => x.User).SingleOrDefaultAsync(x => x.TokenHash == hash && x.ExpiresAtUtc > DateTime.UtcNow);
        if (session?.User is not { IsEnabled: true } user) return AuthenticateResult.Fail("登录已过期");
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Name, user.UserName), new Claim(ClaimTypes.Role, user.Role), new Claim("must_change_password", user.MustChangePassword.ToString()) };
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name));
    }
}

public static class AuthHelpers
{
    public static Guid UserId(this ClaimsPrincipal principal) => Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
    public static string UserName(this ClaimsPrincipal principal) => principal.Identity?.Name ?? "unknown";
    public static string NewToken()
    {
        Span<byte> bytes = stackalloc byte[32]; RandomNumberGenerator.Fill(bytes); return Convert.ToBase64String(bytes);
    }
    public static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

public static class DatabaseInitializer
{
    public static async Task InitializeAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<PlatformOptions>>().Value;
        if (await db.Users.AnyAsync())
        {
            if (!await db.Users.AnyAsync(x => x.Role == Roles.Admin && x.IsEnabled))
            {
                var recoveryAdmin = await db.Users.SingleOrDefaultAsync(x => x.UserName == options.DefaultAdminUser);
                if (recoveryAdmin != null)
                {
                    recoveryAdmin.Role = Roles.Admin;
                    recoveryAdmin.IsEnabled = true;
                    db.AuditLogs.Add(new AuditLog { Action = "System.AdminRecovery", Target = recoveryAdmin.UserName, Result = "Success", Details = "检测到系统没有启用的管理员，已恢复默认管理员角色" });
                    await db.SaveChangesAsync();
                }
            }
            return;
        }
        var user = new User { UserName = options.DefaultAdminUser, Role = Roles.Admin, MustChangePassword = true };
        user.PasswordHash = new PasswordHasher<User>().HashPassword(user, options.DefaultAdminPassword);
        db.Users.Add(user);
        db.AuditLogs.Add(new AuditLog { Action = "System.Initialize", Target = "Admin", Result = "Success", Details = "已创建初始管理员账户" });
        await db.SaveChangesAsync();
    }
}
