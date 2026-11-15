using DomainCopilot.Domain.Workflow;

namespace DomainCopilot.Application.Security;

public sealed class AuthError(string message) : WorkflowError(message);

public static class Authentication
{
    public static IReadOnlyDictionary<string, object?> Login(string tenantId, string username, string password,
        IUserRepository userRepo, string secret, int ttlSeconds)
    {
        var user = userRepo.GetByUsername(tenantId, username);
        var dummy = ApplicationSecurity.DummyHash();
        var stored = user?.PasswordHash ?? dummy;
        var ok = user is not null && user.Active && ApplicationSecurity.VerifyPassword(password, stored);
        if (!ok || user is null) throw new AuthError("invalid credentials");
        var token = ApplicationSecurity.CreateToken(tenantId, user.Id, user.Role, secret, ttlSeconds);
        return new Dictionary<string, object?> { ["access_token"] = token, ["token_type"] = "bearer", ["tenant_id"] = tenantId,
            ["user_id"] = user.Id, ["role"] = user.Role, ["username"] = user.Username };
    }

    public static IReadOnlyDictionary<string, object?> VerifyAndLoad(string token, string secret, IUserRepository userRepo)
        => VerifyAndLoad(token, secret, () => userRepo);

    public static IReadOnlyDictionary<string, object?> VerifyAndLoad(string token, string secret, Func<IUserRepository> userRepoFactory)
    {
        IReadOnlyDictionary<string, object?> payload;
        try { payload = ApplicationSecurity.VerifyToken(token, secret); }
        catch { throw new AuthError("invalid token"); }
        string tenantId;
        long userId;
        try
        {
            tenantId = payload["tenant_id"] is null ? "None" : Convert.ToString(payload["tenant_id"], System.Globalization.CultureInfo.InvariantCulture)!;
            userId = payload["user_id"] switch
            {
                long value => value,
                int value => value,
                System.Numerics.BigInteger value => checked((long)value),
                double value => checked((long)Math.Truncate(value)),
                bool value => value ? 1L : 0L,
                string value => long.Parse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture),
                _ => throw new AuthError("invalid token")
            };
        }
        catch (Exception error) when (error is FormatException or OverflowException or InvalidCastException)
        { throw new AuthError("invalid token"); }
        var user = userRepoFactory().Get(tenantId, userId);
        if (user is null || !user.Active) throw new AuthError("invalid token");
        return new Dictionary<string, object?> { ["tenant_id"] = tenantId, ["user_id"] = user.Id, ["role"] = user.Role, ["username"] = user.Username };
    }
}
