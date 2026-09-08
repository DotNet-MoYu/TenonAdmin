using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;

namespace TenonAdmin.Tests;

/// <summary>
/// 集成测试的数据库选择(§8 数据库矩阵)。默认 SQLite(本地 <c>dotnet test</c> 不变);CI/矩阵的其它腿
/// 通过环境变量切换:
/// <list type="bullet">
/// <item><c>TENON_TEST_DBTYPE=MySql</c> + <c>TENON_TEST_MYSQL=</c>(不含 Database 的服务器连接串)。</item>
/// <item><c>TENON_TEST_DBTYPE=SqlServer</c> + <c>TENON_TEST_SQLSERVER=</c>(不含 Database 的服务器连接串,
/// 需带 <c>TrustServerCertificate=True;Encrypt=False</c> —— MDS 4.x 默认加密且本地无受信证书)。</item>
/// <item><c>TENON_TEST_SQLSERVER_TEMPLATE=1</c> 时,普通 WebApplicationFactory 测试从模板备份恢复;
/// <c>TENON_TEST_SQLSERVER_BACKUP_DIR</c> 为 SQL Server 容器内可写目录(默认 <c>/var/opt/mssql/data</c>)。</item>
/// </list>
/// <para>库隔离:库名由 <c>identity</c> 确定性派生——同 identity → 同库(支持"同库二次启动"的幂等用例),
/// 不同 identity → 各自独立库。建库/删库经原始 <see cref="MySqlConnection"/> / <see cref="SqlConnection"/>
/// 连服务器(SqlSugar 不负责建库,CodeFirst 只建表)。</para>
/// </summary>
internal static class TestDb
{
    private static readonly object SqlServerTemplateGate = new();
    private static readonly Dictionary<string, string> SqlServerTemplateBackups = new(StringComparer.Ordinal);
    private static readonly string SqlServerTemplateProcessId = $"{Environment.ProcessId}_{Guid.NewGuid():N}";
    private static string? sqlServerTemplateInitializing;
    private static bool sqlServerTemplateCleanupRegistered;

    public static bool UseMySql =>
        string.Equals(Environment.GetEnvironmentVariable("TENON_TEST_DBTYPE"), "MySql", StringComparison.OrdinalIgnoreCase);

    public static bool UseSqlServer =>
        string.Equals(Environment.GetEnvironmentVariable("TENON_TEST_DBTYPE"), "SqlServer", StringComparison.OrdinalIgnoreCase);

    public static bool UsePostgreSql =>
        string.Equals(Environment.GetEnvironmentVariable("TENON_TEST_DBTYPE"), "PostgreSQL", StringComparison.OrdinalIgnoreCase);

    private static string MySqlBase =>
        Environment.GetEnvironmentVariable("TENON_TEST_MYSQL")
        ?? "Server=127.0.0.1;Port=3306;User ID=root;Password=root;AllowPublicKeyRetrieval=true;SSL Mode=None;";

    private static string SqlServerBase =>
        Environment.GetEnvironmentVariable("TENON_TEST_SQLSERVER")
        ?? "Server=127.0.0.1;User ID=sa;Password=sa;TrustServerCertificate=True;Encrypt=False;";

    private static string PostgreSqlBase =>
        Environment.GetEnvironmentVariable("TENON_TEST_POSTGRESQL")
        ?? "Server=127.0.0.1;Port=5432;User ID=postgres;Password=postgres;";

    /// <summary>当前腿的 DbType 字符串(直接喂给 <c>AdminDatabaseOptions.DbType</c> / 配置)。</summary>
    public static string DbType => UseMySql ? "MySql" : UseSqlServer ? "SqlServer" : UsePostgreSql ? "PostgreSQL" : "Sqlite";

    /// <summary>SQL Server 是否启用模板库优化(默认关闭,避免改变普通本地测试行为)。</summary>
    public static bool SqlServerTemplateEnabled => UseSqlServer &&
        IsTrue(Environment.GetEnvironmentVariable("TENON_TEST_SQLSERVER_TEMPLATE"));

    /// <summary>当前是否正在由模板宿主初始化模板库。</summary>
    public static bool IsSqlServerTemplateInitialization => sqlServerTemplateInitializing is not null;

    /// <summary>隔离库名(由 identity 派生,合法标识符、稳定;MySQL 与 SqlServer 共用规则)。</summary>
    private static string DbName(string identity) =>
        "tenon_it_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16].ToLowerInvariant();

    /// <summary>解析连接串:SQLite 指向文件;MySQL/SqlServer 先建库(幂等)再返回带 Database 的连接串。</summary>
    public static string ConnectionString(string identity, string sqliteFile)
    {
        if (UseMySql)
        {
            var db = DbName(identity);
            ExecMySql($"CREATE DATABASE IF NOT EXISTS `{db}` CHARACTER SET utf8mb4;");
            return $"{MySqlBase.TrimEnd(';')};Database={db};";
        }
        if (UseSqlServer)
        {
            var db = DbName(identity);
            // CREATE DATABASE 须为批次内唯一语句,IF 是控制流不算另一条语句,该惯用法可用。
            ExecSqlServer($"IF DB_ID(N'{db}') IS NULL CREATE DATABASE [{db}];");
            return $"{SqlServerBase.TrimEnd(';')};Database={db};";
        }
        if (UsePostgreSql)
        {
            var db = DbName(identity);
            // PG 不支持 CREATE DATABASE IF NOT EXISTS(且 CREATE DATABASE 不能进事务/DO 块),先查 pg_database 再按需建(幂等)。
            if (ExecPostgreSqlScalar($"SELECT 1 FROM pg_database WHERE datname = '{db}'") is null)
                ExecPostgreSql($"CREATE DATABASE \"{db}\";");
            return $"{PostgreSqlBase.TrimEnd(';')};Database={db};";
        }
        return $"Data Source={sqliteFile}";
    }

    /// <summary>
    /// SQL Server 模板模式的连接串。模板只由指定宿主 CodeFirst 一次,测试库从备份恢复;
    /// <paramref name="reset"/> 为 false 时保留同 identity 的已有库,兼容同库重启契约。
    /// </summary>
    public static string ConnectionString(string identity, string sqliteFile, string templateKind, bool reset = true)
    {
        if (!SqlServerTemplateEnabled) return ConnectionString(identity, sqliteFile);

        lock (SqlServerTemplateGate)
        {
            if (sqlServerTemplateInitializing is not null)
            {
                if (!string.Equals(sqlServerTemplateInitializing, templateKind, StringComparison.Ordinal))
                    throw new InvalidOperationException("SQL Server 模板初始化期间不能切换模板类型。");
                return SqlServerConnection(TemplateDbName(templateKind));
            }

            EnsureSqlServerTemplate(templateKind);
            var db = DbName(identity);
            if (reset || !SqlServerDatabaseExists(db))
                RestoreSqlServerDatabase(db, SqlServerTemplateBackups[templateKind]);
            return SqlServerConnection(db);
        }
    }

    /// <summary>清理:SQLite 删文件;MySQL/SqlServer 删库(尽力而为)。</summary>
    public static void Cleanup(string identity, string sqliteFile)
    {
        if (UseMySql)
            try { ExecMySql($"DROP DATABASE IF EXISTS `{DbName(identity)}`;"); } catch { /* 尽力而为 */ }
        else if (UseSqlServer)
            try
            {
                // 释放目标库连接池;ALTER DATABASE 的 ROLLBACK IMMEDIATE 再处理仍存活的会话。
                var db = DbName(identity);
                ClearSqlServerPool(db);
                ExecSqlServer($"IF DB_ID(N'{db}') IS NOT NULL BEGIN ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}]; END");
            }
            catch { /* 尽力而为 */ }
        else if (UsePostgreSql)
            try
            {
                NpgsqlConnection.ClearAllPools();
                var db = DbName(identity);
                // 先踢掉目标库上的其余会话(否则活动连接阻塞 DROP),再删;PG 支持 DROP DATABASE IF EXISTS。
                ExecPostgreSql($"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{db}' AND pid <> pg_backend_pid();");
                ExecPostgreSql($"DROP DATABASE IF EXISTS \"{db}\";");
            }
            catch { /* 尽力而为 */ }
        else
            try { if (File.Exists(sqliteFile)) File.Delete(sqliteFile); } catch { /* 尽力而为 */ }
    }

    private static void ExecMySql(string sql)
    {
        using var conn = new MySqlConnection(MySqlBase);   // 连服务器(不指定 Database)以建/删库
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void ExecSqlServer(string sql)
    {
        using var conn = new SqlConnection(SqlServerBase);   // 连 master(不指定 Database)以建/删库
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string SqlServerConnection(string db) => $"{SqlServerBase.TrimEnd(';')};Database={db};";

    private static void EnsureSqlServerTemplate(string templateKind)
    {
        if (SqlServerTemplateBackups.ContainsKey(templateKind)) return;

        var templateDb = TemplateDbName(templateKind);
        CreateSqlServerDatabase(templateDb);
        sqlServerTemplateInitializing = templateKind;
        try
        {
            if (string.Equals(templateKind, "workflow", StringComparison.Ordinal))
            {
                using var factory = new WorkflowAppFactory();
                _ = factory.CreateClient();
            }
            else
            {
                using var factory = new AdminAppFactory { DbPath = templateDb, DeleteDbOnDispose = false };
                _ = factory.CreateClient();
            }

            var backup = SqlServerTemplateBackupPath(templateKind);
            BackupSqlServerDatabase(templateDb, backup);
            SqlServerTemplateBackups[templateKind] = backup;
            RegisterSqlServerTemplateCleanup();
        }
        finally
        {
            sqlServerTemplateInitializing = null;
        }
    }

    private static string TemplateDbName(string templateKind) =>
        DbName($"template:{SqlServerTemplateProcessId}:{templateKind}");

    private static string SqlServerTemplateBackupPath(string templateKind) =>
        Path.Combine(
            Environment.GetEnvironmentVariable("TENON_TEST_SQLSERVER_BACKUP_DIR") ?? "/var/opt/mssql/data",
            $"tenon-template-{SqlServerTemplateProcessId}-{templateKind}.bak");

    private static void CreateSqlServerDatabase(string db)
    {
        ExecSqlServer($"IF DB_ID(N'{SqlLiteral(db)}') IS NULL CREATE DATABASE {SqlIdentifier(db)};");
    }

    private static bool SqlServerDatabaseExists(string db)
    {
        using var conn = new SqlConnection(SqlServerBase);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT CASE WHEN DB_ID(@db) IS NULL THEN 0 ELSE 1 END;";
        cmd.Parameters.AddWithValue("@db", db);
        return Convert.ToInt32(cmd.ExecuteScalar()) != 0;
    }

    private static void BackupSqlServerDatabase(string db, string backup)
    {
        using var conn = new SqlConnection(SqlServerBase);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        cmd.CommandText = $"BACKUP DATABASE {SqlIdentifier(db)} TO DISK = N'{SqlLiteral(backup)}' " +
                          "WITH COPY_ONLY, INIT, CHECKSUM, COMPRESSION;";
        cmd.ExecuteNonQuery();
    }

    private static void RestoreSqlServerDatabase(string db, string backup)
    {
        ClearSqlServerPool(db);
        using var conn = new SqlConnection(SqlServerBase);
        conn.Open();

        if (SqlServerDatabaseExists(db))
        {
            using var singleUser = conn.CreateCommand();
            singleUser.CommandText = $"ALTER DATABASE {SqlIdentifier(db)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;";
            singleUser.ExecuteNonQuery();
        }

        var files = ReadSqlServerBackupFiles(conn, backup);
        var dataIndex = 0;
        var logIndex = 0;
        var moves = new List<string>(files.Count);
        foreach (var file in files)
        {
            var index = file.Type == 'L' ? logIndex++ : dataIndex++;
            var suffix = index == 0 ? "" : $"_{index}";
            var extension = file.Type == 'L' ? ".ldf" : ".mdf";
            var destination = Path.Combine(
                Environment.GetEnvironmentVariable("TENON_TEST_SQLSERVER_BACKUP_DIR") ?? "/var/opt/mssql/data",
                $"{db}{suffix}{extension}");
            moves.Add($"MOVE N'{SqlLiteral(file.LogicalName)}' TO N'{SqlLiteral(destination)}'");
        }

        if (dataIndex == 0 || logIndex == 0)
            throw new InvalidOperationException("SQL Server 模板备份缺少数据文件或日志文件。");

        using var restore = conn.CreateCommand();
        restore.CommandTimeout = 300;
        restore.CommandText = $"RESTORE DATABASE {SqlIdentifier(db)} FROM DISK = N'{SqlLiteral(backup)}' " +
                              $"WITH REPLACE, RECOVERY, {string.Join(", ", moves)};";
        restore.ExecuteNonQuery();

        using var multiUser = conn.CreateCommand();
        multiUser.CommandText = $"ALTER DATABASE {SqlIdentifier(db)} SET MULTI_USER;";
        multiUser.ExecuteNonQuery();
    }

    private static void ClearSqlServerPool(string db)
    {
        using var conn = new SqlConnection(SqlServerConnection(db));
        SqlConnection.ClearPool(conn);
    }

    private static List<(string LogicalName, char Type)> ReadSqlServerBackupFiles(SqlConnection conn, string backup)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 300;
        cmd.CommandText = $"RESTORE FILELISTONLY FROM DISK = N'{SqlLiteral(backup)}';";
        using var reader = cmd.ExecuteReader();
        var files = new List<(string, char)>();
        var logicalName = reader.GetOrdinal("LogicalName");
        var type = reader.GetOrdinal("Type");
        while (reader.Read())
            files.Add((reader.GetString(logicalName), reader.GetString(type)[0]));
        return files;
    }

    private static string SqlIdentifier(string value) => $"[{value.Replace("]", "]]", StringComparison.Ordinal)}]";

    private static string SqlLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static bool IsTrue(string? value) => value is "1" or "true" or "True" or "TRUE";

    private static void RegisterSqlServerTemplateCleanup()
    {
        if (sqlServerTemplateCleanupRegistered) return;
        sqlServerTemplateCleanupRegistered = true;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => CleanupSqlServerTemplates();
    }

    private static void CleanupSqlServerTemplates()
    {
        lock (SqlServerTemplateGate)
        {
            foreach (var (kind, backup) in SqlServerTemplateBackups)
            {
                try
                {
                    var db = TemplateDbName(kind);
                    ClearSqlServerPool(db);
                    ExecSqlServer($"IF DB_ID(N'{SqlLiteral(db)}') IS NOT NULL BEGIN ALTER DATABASE {SqlIdentifier(db)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {SqlIdentifier(db)}; END");
                }
                catch { /* 进程退出时尽力清理 */ }

                try
                {
                    ExecSqlServer($"EXEC master.dbo.xp_delete_file 0, N'{SqlLiteral(backup)}', N'bak';");
                }
                catch { /* 部分 SQL Server 环境禁用扩展过程,容器销毁时一并清理 */ }
            }
        }
    }

    // PG 必须连到某个已存在的库才能建/删其它库,统一连维护库 postgres。
    private static string PostgreSqlAdmin => $"{PostgreSqlBase.TrimEnd(';')};Database=postgres;";

    private static void ExecPostgreSql(string sql)
    {
        using var conn = new NpgsqlConnection(PostgreSqlAdmin);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static object? ExecPostgreSqlScalar(string sql)
    {
        using var conn = new NpgsqlConnection(PostgreSqlAdmin);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }
}
