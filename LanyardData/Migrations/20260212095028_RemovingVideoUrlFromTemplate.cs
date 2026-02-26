using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RemovingVideoUrlFromTemplate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FileId",
                table: "ProjectionProgramStepTemplateParameters");

            migrationBuilder.Sql("UPDATE ProjectionProgramStepTemplateParameters SET IsActive = 0 WHERE Name = 'VideoURL'");
            
            migrationBuilder.Sql($@"
                INSERT INTO ProjectionProgramStepTemplateParameters (Id, Name, Description, DataType, TemplateId, IsActive)
                SELECT '{Guid.NewGuid()}', 'Video', 'The URL of the video to play', 'VideoFile', Id, 1
                FROM (SELECT Id FROM ProjectionProgramStepTemplates WHERE Name = 'Play Video') AS T;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "FileId",
                table: "ProjectionProgramStepTemplateParameters",
                type: "uniqueidentifier",
                nullable: true);
        }
    }
}
