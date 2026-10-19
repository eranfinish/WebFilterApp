using System.Reflection;
using Dapper;
using Microsoft.Data.Sqlite;

namespace WebFilter.Service.Storage;

/// <summary>
/// One SQLite file and its numbered migrations (embedded <c>Storage/Migrations/{schema}/NNN_name.sql</c>).
/// Only the service opens these files (CLAUDE.md rule 1).
/// </summary>
public sealed class SqliteDatabase
{
    private readonly string _connectionString;

    public SqliteDatabase(string path, string schema)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        Migrate(schema);
    }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        connection.Execute("PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA foreign_keys = ON;");
        return connection;
    }

    public int SchemaVersion
    {
        get
        {
            using var connection = Open();
            return connection.ExecuteScalar<int>("SELECT COALESCE(MAX(version), 0) FROM schema_version");
        }
    }

    private void Migrate(string schema)
    {
        using var connection = Open();
        connection.Execute("CREATE TABLE IF NOT EXISTS schema_version (version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL)");
        var applied = connection.Query<int>("SELECT version FROM schema_version").ToHashSet();
        foreach (var (version, sql) in Migrations(schema).Where(m => !applied.Contains(m.Version)))
        {
            using var transaction = connection.BeginTransaction();
            connection.Execute(sql, transaction: transaction);
            connection.Execute("INSERT INTO schema_version (version, applied_at) VALUES (@version, @at)",
                new { version, at = DateTimeOffset.UtcNow.ToString("o") }, transaction);
            transaction.Commit();
        }
    }

    private static IEnumerable<(int Version, string Sql)> Migrations(string schema)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var prefix = $"Migrations.{schema}\\";
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            var file = name[prefix.Length..];
            using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
            yield return (int.Parse(file[..3], System.Globalization.CultureInfo.InvariantCulture), reader.ReadToEnd());
        }
    }
}
