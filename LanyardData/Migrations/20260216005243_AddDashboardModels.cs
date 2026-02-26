using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDashboardModels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Dashboards",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreateDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastUpdateDate = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dashboards", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DashboardWidgets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DashboardId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    GridX = table.Column<int>(type: "int", nullable: false),
                    GridY = table.Column<int>(type: "int", nullable: false),
                    GridW = table.Column<int>(type: "int", nullable: false),
                    GridH = table.Column<int>(type: "int", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    ConfigJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DashboardWidgets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DashboardWidgets_Dashboards_DashboardId",
                        column: x => x.DashboardId,
                        principalTable: "Dashboards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Dashboards_IsActive_Name",
                table: "Dashboards",
                columns: new[] { "IsActive", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_DashboardWidgets_DashboardId_IsActive_SortOrder",
                table: "DashboardWidgets",
                columns: new[] { "DashboardId", "IsActive", "SortOrder" });

            migrationBuilder.Sql(@"
                IF NOT EXISTS (SELECT 1 FROM ProjectionProgramStepTemplates WHERE Name = 'Dashboard')
                BEGIN
                    INSERT INTO ProjectionProgramStepTemplates (Id, Name, Description, IsActive)
                    VALUES (NEWID(), 'Dashboard', 'Render a dashboard', 1);
                END

                IF NOT EXISTS (
                    SELECT 1
                    FROM ProjectionProgramStepTemplateParameters p
                    INNER JOIN ProjectionProgramStepTemplates t ON t.Id = p.TemplateId
                    WHERE t.Name = 'Dashboard' AND p.Name = 'Dashboard' AND p.IsActive = 1
                )
                BEGIN
                    INSERT INTO ProjectionProgramStepTemplateParameters (Id, TemplateId, Name, Description, IsRequired, DataType, IsActive)
                    SELECT NEWID(), t.Id, 'Dashboard', 'Dashboard to render', 1, 'Dashboard', 1
                    FROM ProjectionProgramStepTemplates t
                    WHERE t.Name = 'Dashboard';
                END
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DashboardWidgets");

            migrationBuilder.DropTable(
                name: "Dashboards");
        }
    }
}
