using Npgsql;

namespace DomainCopilot.Infrastructure.Persistence;

/// <summary>Converts PostgreSQL URI DSNs used by the Python deployment to Npgsql's key/value format.</summary>
public static class PostgresConnectionString
{
    public static string ToNpgsql(string value)
    {
        if (!value.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) &&
            !value.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
            return new NpgsqlConnectionStringBuilder(value).ConnectionString;

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "postgres" && uri.Scheme != "postgresql"))
            throw new FormatException("DATABASE_URL is not a valid PostgreSQL connection URI.");

        var userInfo = uri.UserInfo.Split(':', 2);
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port < 0 || uri.IsDefaultPort ? 5432 : uri.Port,
            Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')),
            Username = Uri.UnescapeDataString(userInfo[0])
        };
        if (userInfo.Length == 2) builder.Password = Uri.UnescapeDataString(userInfo[1]);

        var query = uri.Query.TrimStart('?');
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var key = Uri.UnescapeDataString(separator < 0 ? pair : pair[..separator]).Replace('+', ' ');
            var option = Uri.UnescapeDataString(separator < 0 ? string.Empty : pair[(separator + 1)..]).Replace('+', ' ');
            try
            {
                switch (key.ToLowerInvariant())
                {
                    case "sslmode": builder.SslMode = option.ToLowerInvariant() switch { "verify-ca" => SslMode.VerifyCA, "verify-full" => SslMode.VerifyFull, _ => Enum.Parse<SslMode>(option, true) }; break;
                    case "connect_timeout":
                    case "timeout": builder.Timeout = int.Parse(option, System.Globalization.CultureInfo.InvariantCulture); break;
                    case "application_name": builder.ApplicationName = option; break;
                    case "target_session_attrs": builder.TargetSessionAttributes = option; break;
                    default: builder[key] = option; break;
                }
            }
            catch (Exception error) when (error is ArgumentException or FormatException or OverflowException)
            {
                throw new FormatException($"DATABASE_URL has an unsupported or invalid connection option '{key}'.");
            }
        }
        return builder.ConnectionString;
    }
}



