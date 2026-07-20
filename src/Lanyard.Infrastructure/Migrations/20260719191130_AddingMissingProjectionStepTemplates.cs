using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddingMissingProjectionStepTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO ""ProjectionProgramStepTemplates"" (""Id"", ""Name"", ""Description"", ""IsActive"")
                SELECT gen_random_uuid(), 'Show Text', 'Displays a block of text on screen.', TRUE
                WHERE NOT EXISTS (SELECT 1 FROM ""ProjectionProgramStepTemplates"" WHERE ""Name"" = 'Show Text');

                INSERT INTO ""ProjectionProgramStepTemplateParameters"" (""Id"", ""Name"", ""Description"", ""DataType"", ""IsRequired"", ""IsActive"", ""TemplateId"")
                SELECT gen_random_uuid(), 'Text', 'The text to display.', 'String', TRUE, TRUE, t.""Id""
                FROM ""ProjectionProgramStepTemplates"" t
                WHERE t.""Name"" = 'Show Text'
                  AND NOT EXISTS (
                      SELECT 1 FROM ""ProjectionProgramStepTemplateParameters"" p
                      WHERE p.""TemplateId"" = t.""Id"" AND p.""Name"" = 'Text');

                INSERT INTO ""ProjectionProgramStepTemplates"" (""Id"", ""Name"", ""Description"", ""IsActive"")
                SELECT gen_random_uuid(), 'Play a Video', 'Plays a video file.', TRUE
                WHERE NOT EXISTS (SELECT 1 FROM ""ProjectionProgramStepTemplates"" WHERE ""Name"" = 'Play a Video');

                INSERT INTO ""ProjectionProgramStepTemplateParameters"" (""Id"", ""Name"", ""Description"", ""DataType"", ""IsRequired"", ""IsActive"", ""TemplateId"")
                SELECT gen_random_uuid(), 'Video File', 'The video file to play.', 'File', TRUE, TRUE, t.""Id""
                FROM ""ProjectionProgramStepTemplates"" t
                WHERE t.""Name"" = 'Play a Video'
                  AND NOT EXISTS (
                      SELECT 1 FROM ""ProjectionProgramStepTemplateParameters"" p
                      WHERE p.""TemplateId"" = t.""Id"" AND p.""Name"" = 'Video File');

                INSERT INTO ""ProjectionProgramStepTemplateParameters"" (""Id"", ""Name"", ""Description"", ""DataType"", ""IsRequired"", ""IsActive"", ""TemplateId"")
                SELECT gen_random_uuid(), 'ShouldRepeat', 'Whether the video should loop.', 'Boolean', FALSE, TRUE, t.""Id""
                FROM ""ProjectionProgramStepTemplates"" t
                WHERE t.""Name"" = 'Play a Video'
                  AND NOT EXISTS (
                      SELECT 1 FROM ""ProjectionProgramStepTemplateParameters"" p
                      WHERE p.""TemplateId"" = t.""Id"" AND p.""Name"" = 'ShouldRepeat');

                INSERT INTO ""ProjectionProgramStepTemplateParameters"" (""Id"", ""Name"", ""Description"", ""DataType"", ""IsRequired"", ""IsActive"", ""TemplateId"")
                SELECT gen_random_uuid(), 'Volume', 'The playback volume as a percentage (0-100).', 'String', FALSE, TRUE, t.""Id""
                FROM ""ProjectionProgramStepTemplates"" t
                WHERE t.""Name"" = 'Play a Video'
                  AND NOT EXISTS (
                      SELECT 1 FROM ""ProjectionProgramStepTemplateParameters"" p
                      WHERE p.""TemplateId"" = t.""Id"" AND p.""Name"" = 'Volume');

                INSERT INTO ""ProjectionProgramStepTemplates"" (""Id"", ""Name"", ""Description"", ""IsActive"")
                SELECT gen_random_uuid(), 'Dashboard', 'Displays a dashboard.', TRUE
                WHERE NOT EXISTS (SELECT 1 FROM ""ProjectionProgramStepTemplates"" WHERE ""Name"" = 'Dashboard');

                INSERT INTO ""ProjectionProgramStepTemplateParameters"" (""Id"", ""Name"", ""Description"", ""DataType"", ""IsRequired"", ""IsActive"", ""TemplateId"")
                SELECT gen_random_uuid(), 'Dashboard', 'The dashboard to display.', 'Dashboard', TRUE, TRUE, t.""Id""
                FROM ""ProjectionProgramStepTemplates"" t
                WHERE t.""Name"" = 'Dashboard'
                  AND NOT EXISTS (
                      SELECT 1 FROM ""ProjectionProgramStepTemplateParameters"" p
                      WHERE p.""TemplateId"" = t.""Id"" AND p.""Name"" = 'Dashboard');

                INSERT INTO ""ProjectionProgramStepTemplates"" (""Id"", ""Name"", ""Description"", ""IsActive"")
                SELECT gen_random_uuid(), 'Delay', 'Pauses on the current step for a fixed duration before continuing.', TRUE
                WHERE NOT EXISTS (SELECT 1 FROM ""ProjectionProgramStepTemplates"" WHERE ""Name"" = 'Delay');
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                DELETE FROM ""ProjectionProgramStepTemplateParameters""
                WHERE ""TemplateId"" IN (
                    SELECT ""Id"" FROM ""ProjectionProgramStepTemplates""
                    WHERE ""Name"" IN ('Show Text', 'Play a Video', 'Dashboard', 'Delay'));

                DELETE FROM ""ProjectionProgramStepTemplates""
                WHERE ""Name"" IN ('Show Text', 'Play a Video', 'Dashboard', 'Delay');
            ");
        }
    }
}
