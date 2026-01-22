using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class updatingIdTypeForProjectionProgramStepTemplateParameterModeol : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ProjectionProgramParameterValue_ProjectionProgramSteps_ProjectionProgramStepId1",
                table: "ProjectionProgramParameterValue");

            migrationBuilder.DropIndex(
                name: "IX_ProjectionProgramParameterValue_ProjectionProgramStepId1",
                table: "ProjectionProgramParameterValue");

            migrationBuilder.DropColumn(
                name: "ProjectionProgramStepId1",
                table: "ProjectionProgramParameterValue");

            migrationBuilder.DropColumn(
                name: "ProjectionProgramStepId",
                table: "ProjectionProgramParameterValue");

            migrationBuilder.AddColumn<Guid>(
                name: "ProjectionProgramStepId",
                table: "ProjectionProgramParameterValue",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: Guid.Empty);

            migrationBuilder.CreateIndex(
                name: "IX_ProjectionProgramParameterValue_ProjectionProgramStepId",
                table: "ProjectionProgramParameterValue",
                column: "ProjectionProgramStepId");

            migrationBuilder.AddForeignKey(
                name: "FK_ProjectionProgramParameterValue_ProjectionProgramSteps_ProjectionProgramStepId",
                table: "ProjectionProgramParameterValue",
                column: "ProjectionProgramStepId",
                principalTable: "ProjectionProgramSteps",
                principalColumn: "Id",
                onDelete: ReferentialAction.NoAction);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ProjectionProgramParameterValue_ProjectionProgramSteps_ProjectionProgramStepId",
                table: "ProjectionProgramParameterValue");

            migrationBuilder.DropIndex(
                name: "IX_ProjectionProgramParameterValue_ProjectionProgramStepId",
                table: "ProjectionProgramParameterValue");

            migrationBuilder.DropColumn(
                name: "ProjectionProgramStepId",
                table: "ProjectionProgramParameterValue");

            migrationBuilder.AddColumn<int>(
                name: "ProjectionProgramStepId",
                table: "ProjectionProgramParameterValue",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "ProjectionProgramStepId1",
                table: "ProjectionProgramParameterValue",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProjectionProgramParameterValue_ProjectionProgramStepId1",
                table: "ProjectionProgramParameterValue",
                column: "ProjectionProgramStepId1");

            migrationBuilder.AddForeignKey(
                name: "FK_ProjectionProgramParameterValue_ProjectionProgramSteps_ProjectionProgramStepId1",
                table: "ProjectionProgramParameterValue",
                column: "ProjectionProgramStepId1",
                principalTable: "ProjectionProgramSteps",
                principalColumn: "Id");
        }
    }
}
