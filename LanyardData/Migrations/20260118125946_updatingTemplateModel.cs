using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class updatingTemplateModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Type",
                table: "ProjectionProgramSteps");

            migrationBuilder.AddColumn<Guid>(
                name: "TemplateId",
                table: "ProjectionProgramSteps",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "IX_ProjectionProgramSteps_TemplateId",
                table: "ProjectionProgramSteps",
                column: "TemplateId");

            migrationBuilder.AddForeignKey(
                name: "FK_ProjectionProgramSteps_ProjectionProgramStepTemplates_TemplateId",
                table: "ProjectionProgramSteps",
                column: "TemplateId",
                principalTable: "ProjectionProgramStepTemplates",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ProjectionProgramSteps_ProjectionProgramStepTemplates_TemplateId",
                table: "ProjectionProgramSteps");

            migrationBuilder.DropIndex(
                name: "IX_ProjectionProgramSteps_TemplateId",
                table: "ProjectionProgramSteps");

            migrationBuilder.DropColumn(
                name: "TemplateId",
                table: "ProjectionProgramSteps");

            migrationBuilder.AddColumn<int>(
                name: "Type",
                table: "ProjectionProgramSteps",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }
    }
}
