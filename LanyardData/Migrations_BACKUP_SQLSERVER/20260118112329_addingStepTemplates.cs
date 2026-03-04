using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class addingStepTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Description",
                table: "ProjectionProgramStepTemplates",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.AlterColumn<string>(
                name: "Description",
                table: "ProjectionProgramStepTemplateParameters",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.Sql($@"
                INSERT INTO ProjectionProgramStepTemplates (Id, Name, Description, IsActive)
                VALUES ('{Guid.NewGuid()}', 'Show Text', 'Show a string as a block of text', 1);

                INSERT INTO ProjectionProgramStepTemplateParameters (Id, Name, Description, DataType, TemplateId, IsActive)
                SELECT '{Guid.NewGuid()}', 'Text', 'The text to display', 'String', Id, 1
                FROM (SELECT Id FROM ProjectionProgramStepTemplates WHERE Name = 'Show Text') AS T;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Description",
                table: "ProjectionProgramStepTemplates",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Description",
                table: "ProjectionProgramStepTemplateParameters",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);
        }
    }
}
