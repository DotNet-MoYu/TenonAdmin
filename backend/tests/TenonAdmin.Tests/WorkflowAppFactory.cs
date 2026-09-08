extern alias workflowhost;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace TenonAdmin.Tests;

/// <summary>仅工作流集成测试启用卫星包,避免共享 TestHost 为无关测试建 wf_* 表。</summary>
public sealed class WorkflowAppFactory : WebApplicationFactory<workflowhost::WorkflowProgram>
{
    public string DbPath { get; } = Path.Combine(Path.GetTempPath(), $"tenon-wf-it-{Guid.NewGuid():N}.db");

    /// <summary>每测试的服务覆盖(ConfigureTestServices;仿 <see cref="AdminAppFactory.Overrides"/>)。</summary>
    public Action<IServiceCollection>? Overrides { get; init; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("TenonAdmin:Database:DbType", TestDb.DbType);
        builder.UseSetting("TenonAdmin:Database:ConnectionString", TestDb.ConnectionString(DbPath, DbPath, "workflow"));
        if (TestDb.SqlServerTemplateEnabled && !TestDb.IsSqlServerTemplateInitialization)
        {
            builder.UseSetting("TenonAdmin:Database:EnableCodeFirst", "false");
            builder.UseSetting("TenonAdmin:Database:EnableSeed", "false");
        }
        builder.UseSetting("TenonAdmin:Seed:AdminPassword", "Test@123456");
        builder.UseSetting("TenonAdmin:Jwt:SecretKey", "tenon-workflow-test-signing-key-please-keep-32plus");
        builder.UseSetting("TenonAdmin:Security:DataProtection:Key", Convert.ToBase64String(new byte[32]));
        builder.UseSetting("TenonAdmin:Security:RateLimit:Enabled", "false");
        // WfTimeoutJobSeed 会往 sys_job 播一行 Ready 的超时扫描任务,而调度器默认是开的
        // (AdminJobsOptions.SchedulerEnabled = true)。不关掉,真调度器会在**每个**工作流集成测试的
        // 宿主里按 cron 触发 WfTimeoutJob,与测试自己手动调的 ExecuteAsync 并发操作同一张 wf_task →
        // 随机 flake,而症状会伪装成「CAS 竞争测试偶发」。超时测试一律手动 new JobExecutionContext
        // 直接调 ExecuteAsync(skills/create-job.md 第五节的官方姿势)。
        builder.UseSetting("TenonAdmin:Jobs:SchedulerEnabled", "false");
        if (Overrides != null) builder.ConfigureTestServices(Overrides);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && !TestDb.IsSqlServerTemplateInitialization) TestDb.Cleanup(DbPath, DbPath);
    }
}
