using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class addingProjectionProgramParameterValueModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsRequired",
                table: "ProjectionProgramStepTemplateParameters",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ProjectionProgramParameterValue",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectionProgramStepId = table.Column<int>(type: "int", nullable: false),
                    ProjectionProgramStepId1 = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ParameterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Value = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectionProgramParameterValue", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectionProgramParameterValue_ProjectionProgramStepTemplateParameters_ParameterId",
                        column: x => x.ParameterId,
                        principalTable: "ProjectionProgramStepTemplateParameters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProjectionProgramParameterValue_ProjectionProgramSteps_ProjectionProgramStepId1",
                        column: x => x.ProjectionProgramStepId1,
                        principalTable: "ProjectionProgramSteps",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectionProgramParameterValue_ParameterId",
                table: "ProjectionProgramParameterValue",
                column: "ParameterId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectionProgramParameterValue_ProjectionProgramStepId1",
                table: "ProjectionProgramParameterValue",
                column: "ProjectionProgramStepId1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProjectionProgramParameterValue");

            migrationBuilder.DropColumn(
                name: "IsRequired",
                table: "ProjectionProgramStepTemplateParameters");
        }
    }
}
