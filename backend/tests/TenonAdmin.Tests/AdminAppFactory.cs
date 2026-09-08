using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace TenonAdmin.Tests;

/// <summary>
/// 集成测试工厂:用 <c>TenonAdmin.TestHost</c>(一个"用户 App")做被测宿主。每个实例独立 SQLite 文件 → 测试隔离。
/// <para>默认禁用内置 Dict 模块(让 TestHost 的自定义字典控制器接管其路由);固定超管密码与 JWT 密钥,避免文件竞争。</para>
/// </summary>
public sealed class AdminAppFactory : WebApplicationFactory<Program>
{
    /// <summary>本实例的 SQLite 库文件(默认唯一;幂等测试可传同一路径复用)</summary>
    public string DbPath { get; init; } = Path.Combine(Path.GetTempPath(), $"tenon-it-{Guid.NewGuid():N}.db");

    /// <summary>禁用的模块(默认禁 Dict)</summary>
    public IReadOnlyList<string> DisabledModules { get; init; } = ["Dict"];

    /// <summary>Dispose 时是否删库(幂等测试需跨实例复用库,置 false 自行清理)</summary>
    public bool DeleteDbOnDispose { get; init; } = true;

    /// <summary>每测试的服务覆盖(ConfigureTestServices;用于 Replace 框架服务)</summary>
    public Action<IServiceCollection>? Overrides { get; init; }

    /// <summary>额外的配置项覆盖(在 AddTenonAdmin 绑定前生效,如 CORS 源、会话模式等)</summary>
    public IReadOnlyDictionary<string, string?>? Settings { get; init; }

    /// <summary>宿主环境名(默认 Development;生产建表闸门用例传 "Production")</summary>
    public string EnvironmentName { get; init; } = "Development";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(EnvironmentName);
        // SQL Server/MySQL 可选模板库:普通开发测试从模板复制,显式同库重启/生产闸门用例保留原始初始化路径。
        builder.UseSetting("TenonAdmin:Database:DbType", TestDb.DbType);
        var useSchemaTemplate = EnvironmentName == "Development" &&
            Overrides is null &&
            (DeleteDbOnDispose || TestDb.IsSchemaTemplateInitializationFor("admin", DbPath));
        var connectionString = useSchemaTemplate
            ? TestDb.ConnectionString(DbPath, DbPath, "admin")
            : TestDb.ConnectionString(DbPath, DbPath);
        builder.UseSetting("TenonAdmin:Database:ConnectionString", connectionString);
        if (TestDb.SchemaTemplateEnabled && Overrides is null &&
            !TestDb.IsSchemaTemplateInitializationFor("admin", DbPath) &&
            EnvironmentName == "Development" && DeleteDbOnDispose)
        {
            builder.UseSetting("TenonAdmin:Database:EnableCodeFirst", "false");
            builder.UseSetting("TenonAdmin:Database:EnableSeed", "false");
        }
        builder.UseSetting("TenonAdmin:Seed:AdminPassword", "Test@123456");
        // 固定 >=32 字节 JWT 密钥:避免各测试并发写同一 ./data/dev-jwt.key 文件
        builder.UseSetting("TenonAdmin:Jwt:SecretKey", "tenon-integration-test-signing-key-please-keep-32plus");
        builder.UseSetting("TenonAdmin:Security:RateLimit:Enabled", "false");   // 默认关限流,隔离既有测试;限流用例经 Settings 显式开
        for (var i = 0; i < DisabledModules.Count; i++)
            builder.UseSetting($"TenonAdmin:Api:DisabledModules:{i}", DisabledModules[i]);
        if (Settings != null)
            foreach (var kv in Settings) builder.UseSetting(kv.Key, kv.Value);

        if (Overrides != null) builder.ConfigureTestServices(Overrides);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);   // 先释放宿主(关闭 SqlSugar 连接),再清理库
        if (disposing && DeleteDbOnDispose)
            TestDb.Cleanup(DbPath, DbPath);   // SQLite 删文件 / MySQL 删库
    }
}
