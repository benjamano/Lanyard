using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <summary>
    /// Seeds the built-in "Wait for Game" projection step template and its parameters. Data only,
    /// no schema change; idempotent like the other template seeds. Templates are matched by name,
    /// so the ids are random.
    /// </summary>
    public partial class AddingWaitForGameTemplate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO ""ProjectionProgramStepTemplates"" (""Id"", ""Name"", ""Description"", ""IsActive"")
                SELECT gen_random_uuid(), 'Wait for Game', 'Holds the program until a laser kiosk''s game gets ready, starts, ends or reaches a time left. Shows nothing new, so the previous step stays on screen.', TRUE
                WHERE NOT EXISTS (SELECT 1 FROM ""ProjectionProgramStepTemplates"" WHERE ""Name"" = 'Wait for Game');

                INSERT INTO ""ProjectionProgramStepTemplateParameters"" (""Id"", ""Name"", ""Description"", ""DataType"", ""IsRequired"", ""IsActive"", ""TemplateId"")
                SELECT gen_random_uuid(), 'Game Event', 'What to wait for: Get ready, the game starting, the game ending, or the time left dropping below Seconds Left.', 'GameEvent', TRUE, TRUE, t.""Id""
                FROM ""ProjectionProgramStepTemplates"" t
                WHERE t.""Name"" = 'Wait for Game'
                  AND NOT EXISTS (
                      SELECT 1 FROM ""ProjectionProgramStepTemplateParameters"" p
                      WHERE p.""TemplateId"" = t.""Id"" AND p.""Name"" = 'Game Event');

                INSERT INTO ""ProjectionProgramStepTemplateParameters"" (""Id"", ""Name"", ""Description"", ""DataType"", ""IsRequired"", ""IsActive"", ""TemplateId"")
                SELECT gen_random_uuid(), 'Laser Kiosk', 'The laser kiosk whose game to follow.', 'LaserKiosk', TRUE, TRUE, t.""Id""
                FROM ""ProjectionProgramStepTemplates"" t
                WHERE t.""Name"" = 'Wait for Game'
                  AND NOT EXISTS (
                      SELECT 1 FROM ""ProjectionProgramStepTemplateParameters"" p
                      WHERE p.""TemplateId"" = t.""Id"" AND p.""Name"" = 'Laser Kiosk');

                INSERT INTO ""ProjectionProgramStepTemplateParameters"" (""Id"", ""Name"", ""Description"", ""DataType"", ""IsRequired"", ""IsActive"", ""TemplateId"")
                SELECT gen_random_uuid(), 'Seconds Left', 'For Time left drops below: move on once this many seconds or fewer are left in the game.', 'Integer', FALSE, TRUE, t.""Id""
                FROM ""ProjectionProgramStepTemplates"" t
                WHERE t.""Name"" = 'Wait for Game'
                  AND NOT EXISTS (
                      SELECT 1 FROM ""ProjectionProgramStepTemplateParameters"" p
                      WHERE p.""TemplateId"" = t.""Id"" AND p.""Name"" = 'Seconds Left');

                INSERT INTO ""ProjectionProgramStepTemplateParameters"" (""Id"", ""Name"", ""Description"", ""DataType"", ""IsRequired"", ""IsActive"", ""TemplateId"")
                SELECT gen_random_uuid(), 'Give Up After (minutes)', 'Move on anyway after this many minutes. Leave blank or 0 to wait for as long as it takes.', 'Integer', FALSE, TRUE, t.""Id""
                FROM ""ProjectionProgramStepTemplates"" t
                WHERE t.""Name"" = 'Wait for Game'
                  AND NOT EXISTS (
                      SELECT 1 FROM ""ProjectionProgramStepTemplateParameters"" p
                      WHERE p.""TemplateId"" = t.""Id"" AND p.""Name"" = 'Give Up After (minutes)');
            ");
        }

        /// <inheritdoc />
        /// <remarks>
        /// Destructive: steps and parameter values reference the template with cascading deletes, so
        /// rolling back also removes every "Wait for Game" step from every program.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                DELETE FROM ""ProjectionProgramStepTemplateParameters""
                WHERE ""TemplateId"" IN (SELECT ""Id"" FROM ""ProjectionProgramStepTemplates"" WHERE ""Name"" = 'Wait for Game');

                DELETE FROM ""ProjectionProgramStepTemplates"" WHERE ""Name"" = 'Wait for Game';
            ");
        }
    }
}
