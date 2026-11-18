using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lanyard.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class UniqueDmxStepChannelValues : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Two saves racing on the same fader could each insert a row. Keep the most recently
            // written one per (step, channel) so the unique index below can be created. Only
            // these duplicate rows are deleted; Down() can't bring them back.
            migrationBuilder.Sql("""
                DELETE FROM "DmxSceneStepChannelValues" v
                USING (
                    SELECT "Id",
                           ROW_NUMBER() OVER (
                               PARTITION BY "SceneStepId", "ChannelNumber"
                               ORDER BY COALESCE("UpdateDate", "CreateDate") DESC, "Id" DESC) AS rn
                    FROM "DmxSceneStepChannelValues"
                ) ranked
                WHERE v."Id" = ranked."Id" AND ranked.rn > 1;
                """);

            migrationBuilder.DropIndex(
                name: "IX_DmxSceneStepChannelValues_SceneStepId",
                table: "DmxSceneStepChannelValues");

            migrationBuilder.CreateIndex(
                name: "IX_DmxSceneStepChannelValues_SceneStepId_ChannelNumber",
                table: "DmxSceneStepChannelValues",
                columns: new[] { "SceneStepId", "ChannelNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DmxSceneStepChannelValues_SceneStepId_ChannelNumber",
                table: "DmxSceneStepChannelValues");

            migrationBuilder.CreateIndex(
                name: "IX_DmxSceneStepChannelValues_SceneStepId",
                table: "DmxSceneStepChannelValues",
                column: "SceneStepId");
        }
    }
}
