using System.Data.Common;
using System.Data.Odbc;
using System.Text.RegularExpressions;
using System.Net;
using System.Text;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using Microsoft.Data.Sqlite;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Nexus.Core.Data;
using Nexus.Core.Models;
using Nexus.Core.Security;
using Oracle.ManagedDataAccess.Client;

namespace Nexus.App.Services;

public sealed record DatabaseQueryResult(IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, bool Truncated);
public sealed record DatabaseCommandResult(int AffectedRows);
public sealed record EmailSearchItem(string Id, DateTimeOffset? Date, string From, string Subject);
public sealed record ApiRequestResult(int StatusCode, string ContentType, string Body);

public sealed class AccessVaultService
{
    private readonly NexusRepository _repository;
    private readonly WindowsCredentialStore _credentials;

    public AccessVaultService(NexusRepository repository, WindowsCredentialStore credentials)
    {
        _repository = repository;
        _credentials = credentials;
    }

    public Task<IReadOnlyList<CapabilityProfile>> GetProfilesAsync(
        bool enabledOnly = false, bool remoteOnly = false, CancellationToken cancellationToken = default) =>
        _repository.GetCapabilityProfilesAsync(enabledOnly, remoteOnly, cancellationToken);

    public Task<CapabilityProfile?> GetProfileAsync(string id, CancellationToken cancellationToken = default) =>
        _repository.GetCapabilityProfileAsync(id, cancellationToken);

    public async Task SaveAsync(CapabilityProfile profile, string? newSecret,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(profile.Name)) throw new InvalidOperationException("A capability name is required.");
        if (ContainsInlineSecret(profile.Endpoint))
            throw new InvalidOperationException("Do not place passwords in the endpoint. Use the protected secret field.");
        if (profile.Kind == CapabilityKind.Database && profile.DatabaseProvider is null)
            throw new InvalidOperationException("Select a database provider.");
        if (!string.IsNullOrEmpty(newSecret))
            _credentials.Save(profile.SecretTarget, profile.Username ?? profile.Name, newSecret);
        await _repository.SaveCapabilityProfileAsync(profile, cancellationToken);
    }

    public async Task DeleteAsync(CapabilityProfile profile, CancellationToken cancellationToken = default)
    {
        _credentials.Delete(profile.SecretTarget);
        await _repository.DeleteCapabilityProfileAsync(profile.Id, cancellationToken);
    }

    public async Task<string> TestAsync(CapabilityProfile profile, string? unsavedSecret = null,
        CancellationToken cancellationToken = default)
    {
        if (profile.Kind == CapabilityKind.Email)
        {
            using var client = await OpenEmailAsync(profile, unsavedSecret, cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);
            return "Email connection verified successfully.";
        }
        if (profile.Kind == CapabilityKind.WebApi)
        {
            var apiResult = await RequestApiAsync(profile, HttpMethod.Get, "/api/sql-status", null, unsavedSecret, cancellationToken);
            return $"Biometric API verified successfully (HTTP {apiResult.StatusCode}).";
        }
        if (profile.Kind != CapabilityKind.Database) return "Profile saved. This capability is validated when used.";
        await using var connection = await OpenDatabaseAsync(profile, unsavedSecret, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = profile.DatabaseProvider == DatabaseProvider.Oracle ? "SELECT 1 FROM DUAL" : "SELECT 1";
        command.CommandTimeout = 15;
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null ? "Connected, but the validation query returned no value." : "Connection verified successfully.";
    }

    public async Task<DatabaseQueryResult> QueryReadOnlyAsync(CapabilityProfile profile, string query,
        int maximumRows = 500, CancellationToken cancellationToken = default)
    {
        ValidateReadOnlyQuery(query);
        maximumRows = Math.Clamp(maximumRows, 1, 500);
        await using var connection = await OpenDatabaseAsync(profile, null, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = query.Trim();
        command.CommandTimeout = 30;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        var truncated = false;
        while (await reader.ReadAsync(cancellationToken))
        {
            if (rows.Count >= maximumRows) { truncated = true; break; }
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < reader.FieldCount; index++)
            {
                var value = await reader.IsDBNullAsync(index, cancellationToken) ? null : reader.GetValue(index);
                row[columns[index]] = value is string text && text.Length > 4000 ? text[..4000] + "…" : value;
            }
            rows.Add(row);
        }
        return new DatabaseQueryResult(columns, rows, truncated);
    }

    public async Task<DatabaseCommandResult> ExecuteControlledWriteAsync(CapabilityProfile profile, string query,
        CancellationToken cancellationToken = default)
    {
        if (!HasScope(profile, "write") && !HasScope(profile, "crud"))
            throw new InvalidOperationException("This database capability is not authorized for write operations.");
        DatabaseCommandPolicy.ValidateControlledWrite(query);
        await using var connection = await OpenDatabaseAsync(profile, null, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = query.Trim();
            command.CommandTimeout = 30;
            var affected = await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new DatabaseCommandResult(affected);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<IReadOnlyList<EmailSearchItem>> SearchEmailAsync(CapabilityProfile profile, string query,
        int maximumResults = 50, CancellationToken cancellationToken = default)
    {
        if (profile.Kind != CapabilityKind.Email) throw new InvalidOperationException("An email profile is required.");
        if (string.IsNullOrWhiteSpace(query)) throw new InvalidOperationException("An email search query is required.");
        maximumResults = Math.Clamp(maximumResults, 1, 50);
        using var client = await OpenEmailAsync(profile, null, cancellationToken);
        var folderName = string.IsNullOrWhiteSpace(profile.Resource) ? "INBOX" : profile.Resource;
        var folder = folderName.Equals("INBOX", StringComparison.OrdinalIgnoreCase)
            ? client.Inbox : await client.GetFolderAsync(folderName, cancellationToken);
        await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken);
        var search = SearchQuery.SubjectContains(query).Or(SearchQuery.BodyContains(query)).Or(SearchQuery.FromContains(query));
        var ids = await folder.SearchAsync(search, cancellationToken);
        var selected = ids.TakeLast(maximumResults).ToList();
        var summaries = await folder.FetchAsync(selected, MessageSummaryItems.UniqueId |
            MessageSummaryItems.Envelope | MessageSummaryItems.InternalDate, cancellationToken);
        var results = summaries.OrderByDescending(item => item.InternalDate).Select(item => new EmailSearchItem(
            item.UniqueId.Id.ToString(), item.InternalDate, item.Envelope?.From?.ToString() ?? string.Empty,
            item.Envelope?.Subject ?? "(no subject)")).ToArray();
        await client.DisconnectAsync(true, cancellationToken);
        return results;
    }

    public async Task<ApiRequestResult> RequestApiAsync(CapabilityProfile profile, HttpMethod method,
        string relativePath, string? jsonBody = null, string? unsavedSecret = null,
        CancellationToken cancellationToken = default)
    {
        if (profile.Kind != CapabilityKind.WebApi) throw new InvalidOperationException("A web API profile is required.");
        if (string.IsNullOrWhiteSpace(profile.Endpoint) || !Uri.TryCreate(profile.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("A secure HTTPS API endpoint is required.");
        if (string.IsNullOrWhiteSpace(relativePath) || !relativePath.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) || Uri.TryCreate(relativePath, UriKind.Absolute, out _))
            throw new InvalidOperationException("Only relative /api/ paths are allowed.");
        var secret = unsavedSecret ?? _credentials.Read(profile.SecretTarget) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(secret)) throw new InvalidOperationException("No protected API credential is stored.");
        var cookies = new CookieContainer();
        using var handler = new HttpClientHandler { CookieContainer = cookies, AllowAutoRedirect = true };
        using var client = new HttpClient(handler) { BaseAddress = endpoint, Timeout = TimeSpan.FromSeconds(30) };
        using var login = new FormUrlEncodedContent(new Dictionary<string, string> { ["passcode"] = secret, ["next"] = "/" });
        using var loginResponse = await client.PostAsync("/dhalor/login", login, cancellationToken);
        loginResponse.EnsureSuccessStatusCode();
        using var request = new HttpRequestMessage(method, relativePath);
        if (jsonBody is not null) request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (body.Length > 100_000) body = body[..100_000] + "…";
        response.EnsureSuccessStatusCode();
        return new ApiRequestResult((int)response.StatusCode, response.Content.Headers.ContentType?.MediaType ?? string.Empty, body);
    }

    private async Task<DbConnection> OpenDatabaseAsync(CapabilityProfile profile, string? unsavedSecret,
        CancellationToken cancellationToken)
    {
        var secret = unsavedSecret ?? _credentials.Read(profile.SecretTarget) ?? string.Empty;
        DbConnection connection = profile.DatabaseProvider switch
        {
            DatabaseProvider.Oracle => new OracleConnection(BuildOracleConnectionString(profile, secret)),
            DatabaseProvider.Odbc => new OdbcConnection(BuildOdbcConnectionString(profile, secret)),
            DatabaseProvider.Sqlite => new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = profile.Resource ?? profile.Endpoint ?? string.Empty,
                Mode = SqliteOpenMode.ReadOnly
            }.ToString()),
            DatabaseProvider.SqlServer => new SqlConnection(BuildSqlServerConnectionString(profile, secret)),
            DatabaseProvider.MySql => new MySqlConnection(BuildMySqlConnectionString(profile, secret)),
            _ => throw new InvalidOperationException("Unsupported database provider.")
        };
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private async Task<ImapClient> OpenEmailAsync(CapabilityProfile profile, string? unsavedSecret,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(profile.Endpoint))
            throw new InvalidOperationException("IMAP host is required, for example outlook.office365.com:993.");
        if (string.IsNullOrWhiteSpace(profile.Username)) throw new InvalidOperationException("Email username is required.");
        var secret = unsavedSecret ?? _credentials.Read(profile.SecretTarget) ?? string.Empty;
        if (string.IsNullOrEmpty(secret)) throw new InvalidOperationException("No protected email password or token is stored.");
        var (host, port) = ParseHost(profile.Endpoint, 993);
        var client = new ImapClient { Timeout = 30000 };
        try
        {
            await client.ConnectAsync(host, port, SecureSocketOptions.SslOnConnect, cancellationToken);
            if (profile.AllowedScopes?.Contains("oauth2", StringComparison.OrdinalIgnoreCase) == true)
                await client.AuthenticateAsync(new SaslMechanismOAuth2(profile.Username, secret), cancellationToken);
            else
                await client.AuthenticateAsync(profile.Username, secret, cancellationToken);
            return client;
        }
        catch { client.Dispose(); throw; }
    }

    private static (string Host, int Port) ParseHost(string endpoint, int defaultPort)
    {
        var value = endpoint.Trim();
        var separator = value.LastIndexOf(':');
        if (separator > 0 && int.TryParse(value[(separator + 1)..], out var port))
            return (value[..separator], port);
        return (value, defaultPort);
    }

    private static string BuildOracleConnectionString(CapabilityProfile profile, string secret)
    {
        if (string.IsNullOrWhiteSpace(profile.Endpoint)) throw new InvalidOperationException("Oracle data source is required.");
        if (string.IsNullOrWhiteSpace(profile.Username)) throw new InvalidOperationException("Oracle username is required.");
        var builder = new OracleConnectionStringBuilder
        {
            DataSource = profile.Endpoint,
            UserID = profile.Username,
            Password = secret,
            Pooling = true,
            ConnectionTimeout = 15
        };
        return builder.ConnectionString;
    }

    private static string BuildOdbcConnectionString(CapabilityProfile profile, string secret)
    {
        if (string.IsNullOrWhiteSpace(profile.Endpoint)) throw new InvalidOperationException("ODBC connection or DSN is required.");
        var builder = new OdbcConnectionStringBuilder { ConnectionString = profile.Endpoint };
        if (!string.IsNullOrWhiteSpace(profile.Username)) builder["Uid"] = profile.Username;
        if (!string.IsNullOrEmpty(secret)) builder["Pwd"] = secret;
        return builder.ConnectionString;
    }

    private static string BuildSqlServerConnectionString(CapabilityProfile profile, string secret)
    {
        if (string.IsNullOrWhiteSpace(profile.Endpoint)) throw new InvalidOperationException("SQL Server endpoint is required.");
        if (string.IsNullOrWhiteSpace(profile.Resource)) throw new InvalidOperationException("SQL Server database is required.");
        if (string.IsNullOrWhiteSpace(profile.Username)) throw new InvalidOperationException("SQL Server username is required.");
        return new SqlConnectionStringBuilder
        {
            DataSource = profile.Endpoint,
            InitialCatalog = profile.Resource,
            UserID = profile.Username,
            Password = secret,
            Encrypt = true,
            TrustServerCertificate = true,
            ConnectTimeout = 15,
            ApplicationName = "DAGNUE Capability Vault"
        }.ConnectionString;
    }

    private static string BuildMySqlConnectionString(CapabilityProfile profile, string secret)
    {
        if (string.IsNullOrWhiteSpace(profile.Endpoint)) throw new InvalidOperationException("MySQL endpoint is required.");
        if (string.IsNullOrWhiteSpace(profile.Resource)) throw new InvalidOperationException("MySQL database is required.");
        if (string.IsNullOrWhiteSpace(profile.Username)) throw new InvalidOperationException("MySQL username is required.");
        var (host, port) = ParseHost(profile.Endpoint, 3306);
        return new MySqlConnectionStringBuilder
        {
            Server = host,
            Port = (uint)port,
            Database = profile.Resource,
            UserID = profile.Username,
            Password = secret,
            ConnectionTimeout = 15,
            DefaultCommandTimeout = 30,
            SslMode = MySqlSslMode.Preferred,
            ApplicationName = "DAGNUE Capability Vault"
        }.ConnectionString;
    }

    public static void ValidateReadOnlyQuery(string query)
        => DatabaseCommandPolicy.ValidateReadOnly(query);

    private static bool HasScope(CapabilityProfile profile, string scope) =>
        (profile.AllowedScopes ?? string.Empty).Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)
        .Any(value => value.Equals(scope, StringComparison.OrdinalIgnoreCase));

    private static bool ContainsInlineSecret(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        (Regex.IsMatch(value, @"(?i)(password|pwd|access[_ -]?token|refresh[_ -]?token)\s*=") ||
         (Uri.TryCreate(value, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.UserInfo)));
}
