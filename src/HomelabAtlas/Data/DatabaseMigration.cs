using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace HomelabAtlas.Data;

internal static class DatabaseMigration
{
    private const int CurrentApplicationDatabaseVersion = 1;

    internal static async Task<string?> DatabaseMigrationAsync(HomelabAtlasDbContext db, ILogger? logger = null)
    {
        int databseVersion = 0;

        //Check if in the databse is a value for the current version, if not set it to CurrentVersion
        var hasVersionParameter = await db.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS Value FROM DatabaseParameter WHERE Parameter = 'Version'")
            .SingleAsync() > 0;

        if(!hasVersionParameter)
        {
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO DatabaseParameter (Parameter, Value) VALUES ('Version', {0})",
                CurrentApplicationDatabaseVersion);
        }
        else
        {
            //Get the current version from the database
            databseVersion = await db.Database.SqlQueryRaw<int>(
                "SELECT Value AS Value FROM DatabaseParameter WHERE Parameter = 'Version'")
                .SingleAsync();
        }

        //Exception if the database is newer then the application
        if (databseVersion > CurrentApplicationDatabaseVersion)
            throw new InvalidOperationException($"Database schema version {databseVersion} is newer than supported version {CurrentApplicationDatabaseVersion}.");

        //Same version everything is fine
        if (databseVersion == CurrentApplicationDatabaseVersion)
            return null;

        //Backup for update
        logger?.LogInformation(
            "Database backup before the update. Database version {DatabaseVersion}; application database version {CurrentApplicationDatabaseVersion}",
            databseVersion,
            CurrentApplicationDatabaseVersion);
        var backupPath = await CreateBackupAsync(db, databseVersion);

        await using var transaction = await db.Database.BeginTransactionAsync();

        //Migration von 0 zu 1
        if (databseVersion == 0)
        {
            logger?.LogInformation("Migration from version {DatabaseVersion} to 1", databseVersion);
            //No change insert only 1
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE DatabaseParameter SET Value = {0} WHERE Parameter = 'Version'", 1);

            databseVersion = 1;
        }

        await transaction.CommitAsync();
        return backupPath;
    }


    private static async Task<string> CreateBackupAsync(HomelabAtlasDbContext db, int version)
    {
        if (db.Database.GetDbConnection() is not SqliteConnection source)
            throw new InvalidOperationException("Database backups are supported only for SQLite.");

        var backupDirectory = Path.Combine(Path.GetDirectoryName(source.DataSource)!, "backups");
        Directory.CreateDirectory(backupDirectory);

        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'");
        var fileName = $"homelabatlas-v{version}-to-v{CurrentApplicationDatabaseVersion}-{timestamp}.db";
        var backupPath = Path.Combine(backupDirectory, fileName);
        for (var suffix = 1; File.Exists(backupPath); suffix++)
            backupPath = Path.Combine(backupDirectory, $"homelabatlas-v{version}-to-v{CurrentApplicationDatabaseVersion}-{timestamp}-{suffix}.db");

        var closeSource = source.State != ConnectionState.Open;
        if (closeSource)
            await source.OpenAsync();

        try
        {
            var backupConnectionString = new SqliteConnectionStringBuilder { DataSource = backupPath }.ToString();
            await using var destination = new SqliteConnection(backupConnectionString);
            await destination.OpenAsync();
            source.BackupDatabase(destination);
        }
        finally
        {
            if (closeSource)
                await source.CloseAsync();
        }

        return backupPath;
    }
}
