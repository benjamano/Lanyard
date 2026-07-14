using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddingLiveCaptureTemplate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO ""ProjectionProgramStepTemplates"" (""Id"", ""Name"", ""Description"", ""IsActive"")
                SELECT gen_random_uuid(), 'Live Capture', 'Shows the live output of a video capture device (webcam or capture card).', TRUE
                WHERE NOT EXISTS (SELECT 1 FROM ""ProjectionProgramStepTemplates"" WHERE ""Name"" = 'Live Capture');

                INSERT INTO ""ProjectionProgramStepTemplateParameters"" (""Id"", ""Name"", ""Description"", ""DataType"", ""IsRequired"", ""IsActive"", ""TemplateId"")
                SELECT gen_random_uuid(), 'Video Device', 'The capture device to display. Leave as default to use the first available device.', 'VideoDevice', FALSE, TRUE, t.""Id""
                FROM ""ProjectionProgramStepTemplates"" t
                WHERE t.""Name"" = 'Live Capture'
                  AND NOT EXISTS (
                      SELECT 1 FROM ""ProjectionProgramStepTemplateParameters"" p
                      WHERE p.""TemplateId"" = t.""Id"" AND p.""Name"" = 'Video Device');

                INSERT INTO ""ProjectionProgramStepTemplateParameters"" (""Id"", ""Name"", ""Description"", ""DataType"", ""IsRequired"", ""IsActive"", ""TemplateId"")
                SELECT gen_random_uuid(), 'Enable Audio', 'Whether to also play audio from the capture device.', 'Boolean', FALSE, TRUE, t.""Id""
                FROM ""ProjectionProgramStepTemplates"" t
                WHERE t.""Name"" = 'Live Capture'
                  AND NOT EXISTS (
                      SELECT 1 FROM ""ProjectionProgramStepTemplateParameters"" p
                      WHERE p.""TemplateId"" = t.""Id"" AND p.""Name"" = 'Enable Audio');
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                DELETE FROM ""ProjectionProgramStepTemplateParameters""
                WHERE ""TemplateId"" IN (SELECT ""Id"" FROM ""ProjectionProgramStepTemplates"" WHERE ""Name"" = 'Live Capture');

                DELETE FROM ""ProjectionProgramStepTemplates"" WHERE ""Name"" = 'Live Capture';
            ");
        }
    }
}
