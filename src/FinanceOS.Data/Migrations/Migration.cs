using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace FinanceOS.Data.Migrations;

/// <summary>One numbered schema script. The checksum is recorded when applied and verified on every start.</summary>
public sealed record Migration(int Version, string Name, string Sql)
{
    /// <summary>
    /// SHA-256 of the script with line endings normalized: git may check the file out with CRLF on one
    /// machine and LF on another, and that must not look like an edited migration.
    /// </summary>
    public string Checksum { get; } = ComputeChecksum(Sql);

    public static string ComputeChecksum(string sql)
    {
        var normalized = sql.TrimStart('﻿').Replace("\r\n", "\n", StringComparison.Ordinal);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }
}

/// <summary>The migrations embedded in this assembly (<c>Migrations/NNN_name.sql</c>), in order.</summary>
public static class MigrationCatalog
{
    private const string Prefix = "FinanceOS.Data.Migrations.";

    private static readonly Lazy<IReadOnlyList<Migration>> Embedded = new(LoadEmbedded);

    public static IReadOnlyList<Migration> All => Embedded.Value;

    /// <summary>Highest schema version this build of the app understands.</summary>
    public static int LatestVersion => All[^1].Version;

    private static IReadOnlyList<Migration> LoadEmbedded()
    {
        var assembly = typeof(MigrationCatalog).Assembly;
        var migrations = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .Select(n => Parse(assembly, n))
            .OrderBy(m => m.Version)
            .ToList();

        Validate(migrations);
        return migrations;
    }

    /// <summary>Versions must be 1, 2, 3, … with no gaps or duplicates: a gap means a lost script.</summary>
    public static void Validate(IReadOnlyList<Migration> migrations)
    {
        if (migrations.Count == 0)
        {
            throw new InvalidOperationException("No migrations found.");
        }

        for (var i = 0; i < migrations.Count; i++)
        {
            if (migrations[i].Version != i + 1)
            {
                throw new InvalidOperationException(
                    $"Migration versions must be contiguous from 1; found {migrations[i].Version} at position {i + 1}.");
            }
        }
    }

    private static Migration Parse(Assembly assembly, string resource)
    {
        var file = resource[Prefix.Length..^".sql".Length]; // "001_initial"
        var separator = file.IndexOf('_', StringComparison.Ordinal);
        if (separator <= 0 || !int.TryParse(file[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out var version))
        {
            throw new InvalidOperationException($"Migration resource '{resource}' must be named NNN_name.sql.");
        }

        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return new Migration(version, file[(separator + 1)..], reader.ReadToEnd());
    }
}
