using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class addingPlayVideoTemplate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($@"
                INSERT INTO ProjectionProgramStepTemplates (Id, Name, Description, IsActive)
                VALUES ( NEWID(), 'Play Video', 'Play a Video', 1);

                INSERT INTO ProjectionProgramStepTemplateParameters (Id, Name, Description, DataType, TemplateId, IsActive)
                SELECT '{Guid.NewGuid()}', 'VideoURL', 'The URL of the video to play', 'String', Id, 1
                FROM (SELECT Id FROM ProjectionProgramStepTemplates WHERE Name = 'Play Video') AS T;

                INSERT INTO ProjectionProgramStepTemplateParameters (Id, Name, Description, DataType, TemplateId, IsActive)
                SELECT '{Guid.NewGuid()}', 'ShouldRepeat', 'Whether the video should repeat', 'Boolean', Id, 1
                FROM (SELECT Id FROM ProjectionProgramStepTemplates WHERE Name = 'Play Video') AS T;

                INSERT INTO ProjectionProgramStepTemplateParameters (Id, Name, Description, DataType, TemplateId, IsActive)
                SELECT '{Guid.NewGuid()}', 'Volume', 'The volume of the video to play', 'Integer', Id, 1
                FROM (SELECT Id FROM ProjectionProgramStepTemplates WHERE Name = 'Play Video') AS T;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
