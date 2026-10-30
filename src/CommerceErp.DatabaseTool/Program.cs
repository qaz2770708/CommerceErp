using CommerceErp.Infrastructure;
using CommerceErp.Core;

try
{
    string Arg(string name) { var i=Array.IndexOf(args,name); return i>=0&&i+1<args.Length?args[i+1]:throw new ArgumentException("缺少参数："+name); }
    var mode = args.FirstOrDefault();
    if(mode=="initialize-business")
    {
        var init=new WorkspaceInitialization(Arg("--name"),Arg("--warehouse-name"));
        if(args.Contains("--data")){var data=Path.GetFullPath(Arg("--data"));if(Directory.Exists(data)&&Directory.EnumerateFileSystemEntries(data).Any())throw new InvalidDataException("初始化目录必须为空，禁止覆盖已有数据。");_=new CommerceDatabase(data,init);}
        else CommerceDatabase.InitializeSqlServerBusiness(Environment.GetEnvironmentVariable("ERP_SQL_CONNECTION")??throw new ArgumentException("缺少 SQL 连接。"),Arg("--database"),init);
        Console.WriteLine("正式工作区已建立，无演示商品和库存；请建立管理员并维护基础资料。");return 0;
    }
    if(mode=="upgrade-inventory"){CommerceDatabase.UpgradeInventory(Environment.GetEnvironmentVariable("ERP_SQL_CONNECTION")??throw new ArgumentException("缺少 SQL 连接。"),Arg("--database"));Console.WriteLine("库存单据升级完成。");return 0;}
    if(mode=="upgrade-review"){CommerceDatabase.UpgradeReview(Environment.GetEnvironmentVariable("ERP_SQL_CONNECTION")??throw new ArgumentException("缺少 SQL 连接。"),Arg("--database"));Console.WriteLine("审批与索引升级完成。");return 0;}
    if(mode=="upgrade-master"){CommerceDatabase.UpgradeMaster(Environment.GetEnvironmentVariable("ERP_SQL_CONNECTION")??throw new ArgumentException("缺少 SQL 连接。"),Arg("--database"));Console.WriteLine("主数据升级完成。");return 0;}
    if(mode=="identity")
    {
        var identity=CommerceDatabase.InspectExisting(args.Contains("--data")?Arg("--data"):null,Environment.GetEnvironmentVariable("ERP_SQL_CONNECTION"));
        Console.WriteLine(CommerceDatabase.Json(new{identity.InstanceId,identity.Schema}));return 0;
    }
    if(mode is "create-admin" or "recover-admin")
    {
        var expectedInstance=mode=="recover-admin"?Arg("--instance"):"";
        if(mode=="recover-admin")
        {
            var identity=CommerceDatabase.InspectExisting(args.Contains("--data")?Arg("--data"):null,Environment.GetEnvironmentVariable("ERP_SQL_CONNECTION"));
            if(identity.Schema!=CommerceDatabase.SchemaVersion||identity.InstanceId!=expectedInstance)throw new ArgumentException("数据库版本或身份不符；请先核对 identity 输出，恢复未执行。");
        }
        var db=args.Contains("--data")?new CommerceDatabase(Arg("--data")):CommerceDatabase.SqlServer(Environment.GetEnvironmentVariable("ERP_SQL_CONNECTION")??throw new ArgumentException("请在本机设置 ERP_SQL_CONNECTION。"));
        var accounts=new AccountService(db);
        if(mode=="create-admin"&&!accounts.Status().SetupRequired)throw new ArgumentException("已存在管理员，初始化入口已关闭。");
        if(Console.IsInputRedirected)throw new ArgumentException("请在本机交互式终端输入账号密码；不接受重定向输入。");
        Console.Write("账号名：");var username=Console.ReadLine()??"";var name="";if(mode=="create-admin"){Console.Write("显示名：");name=Console.ReadLine()??"";}
        var reason="";if(mode=="recover-admin"){Console.Write("恢复原因：");reason=Console.ReadLine()??"";}
        Console.Write("密码（不回显）：");var password=new System.Text.StringBuilder();
        while(true){var key=Console.ReadKey(true);if(key.Key==ConsoleKey.Enter)break;if(key.Key==ConsoleKey.Backspace){if(password.Length>0)password.Length--;}else if(!char.IsControl(key.KeyChar)&&password.Length<128)password.Append(key.KeyChar);}
        Console.WriteLine();
        if(mode=="create-admin")accounts.Setup(new(username,name,password.ToString()));
        else accounts.RecoverAdministrator(username,password.ToString(),reason,expectedInstance);
        password.Clear();Console.WriteLine(mode=="create-admin"?"管理员已建立，请在桌面客户端登录。":"管理员已恢复；全部旧会话已撤销，下次登录须修改临时密码。");return 0;
    }
    if (mode == "snapshot")
    {
        Console.WriteLine(CommerceDatabase.CreateVerifiedSnapshot(Arg("--source"), Arg("--output"))); return 0;
    }
    var connection = Environment.GetEnvironmentVariable("ERP_SQL_CONNECTION") ?? throw new ArgumentException("请在本机设置 ERP_SQL_CONNECTION；不要放入命令行或报告。");
    if(mode=="upgrade-tasks"){CommerceDatabase.UpgradeTasks(connection,Arg("--database"));Console.WriteLine("任务结构已升级至版本 7。请同步升级全部服务和客户端。");return 0;}
    if(mode=="upgrade-accounts") {CommerceDatabase.UpgradeAccounts(connection,Arg("--database"));Console.WriteLine("账号结构已升级至版本 6。请升级所有服务和客户端后再开放连接。");return 0;}
    if (mode == "initialize-demo")
    {
        CommerceDatabase.InitializeSqlServerDemo(connection, Arg("--database")); Console.WriteLine("已创建模拟业务数据库。未接入真实店铺。"); return 0;
    }
    if (mode == "migrate")
    {
        var reportPath = Path.GetFullPath(Arg("--report"));
        if (File.Exists(reportPath)) throw new ArgumentException("报告文件已存在，不能覆盖。");
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        // Reserve a writable report before any target changes.
        await File.WriteAllTextAsync(reportPath, "{\"status\":\"NotCommitted\"}");
        using var sourceLease = CommerceDatabase.LockMigrationSource(Arg("--source"));
        var snapshot = CommerceDatabase.CreateVerifiedSnapshot(Arg("--source"), Arg("--snapshot"));
        var report = CommerceDatabase.MigrateToSqlServer(snapshot, connection, Arg("--database"));
        var json = CommerceDatabase.Json(report); Console.WriteLine(json);
        try { await File.WriteAllTextAsync(reportPath, json); }
        catch { Console.Error.WriteLine("迁移已经提交，但报告保存失败。请保留终端中的报告，不要重复迁移。"); return 2; }
        return 0;
    }
    throw new ArgumentException("用法：identity [--data 目录]；create-admin [--data 目录]；recover-admin [--data 目录] --instance 库身份；snapshot --source 文件 --output 新目录；initialize-demo --database 库名；upgrade-accounts --database 库名；upgrade-tasks --database 库名；migrate --source 文件 --snapshot 新目录 --database 库名 --report 新文件。SQL 模式使用本机 ERP_SQL_CONNECTION。");
}
catch (Exception e)
{
    // Driver exceptions may contain host names or credential material; never print raw connection diagnostics.
    Console.Error.WriteLine(e is ArgumentException or InvalidDataException or FileNotFoundException or BusinessRuleException or ConflictException ? e.Message : "数据库操作失败（" + e.GetType().Name + "）。请检查连接、证书、权限和目标空库；未提交事务会回滚。");
    return 1;
}
