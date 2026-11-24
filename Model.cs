using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Repro;

public sealed class AppDb(string connectionString) : DbContext
{
    protected override void OnConfiguring(DbContextOptionsBuilder options)
        => options
            .UseNpgsql(connectionString)
            // The workaround (fix branch): hold the migration lock for the whole run.
            .ReplaceService<IHistoryRepository, AdvisoryLockHistoryRepository>();
}

// Three ordinary migrations. The second one takes a few seconds, the way a data migration or an index build does,
// so that instances started together overlap.
[DbContext(typeof(AppDb))]
[Migration("20260101000001_People")]
public sealed class People : Migration
{
    protected override void Up(MigrationBuilder b)
        => b.CreateTable(
            name: "People",
            columns: t => new
            {
                Id = t.Column<int>(nullable: false),
                Name = t.Column<string>(maxLength: 200, nullable: false),
            },
            constraints: t => t.PrimaryKey("PK_People", x => x.Id));

    protected override void Down(MigrationBuilder b) => b.DropTable("People");
}

[DbContext(typeof(AppDb))]
[Migration("20260101000002_SlowAddEmail")]
public sealed class SlowAddEmail : Migration
{
    protected override void Up(MigrationBuilder b)
    {
        b.Sql("SELECT pg_sleep(3);");
        b.AddColumn<string>("Email", "People", maxLength: 200, nullable: true);
    }

    protected override void Down(MigrationBuilder b) => b.DropColumn("Email", "People");
}

[DbContext(typeof(AppDb))]
[Migration("20260101000003_AddPhone")]
public sealed class AddPhone : Migration
{
    protected override void Up(MigrationBuilder b)
        => b.AddColumn<string>("Phone", "People", maxLength: 50, nullable: true);

    protected override void Down(MigrationBuilder b) => b.DropColumn("Phone", "People");
}
