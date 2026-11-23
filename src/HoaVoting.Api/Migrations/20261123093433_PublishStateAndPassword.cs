using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HoaVoting.Api.Migrations
{
    /// <inheritdoc />
    public partial class PublishStateAndPassword : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PublishError",
                table: "Proposals",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PublishJobId",
                table: "Proposals",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RequirePassword",
                table: "Proposals",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PublishError",
                table: "Proposals");

            migrationBuilder.DropColumn(
                name: "PublishJobId",
                table: "Proposals");

            migrationBuilder.DropColumn(
                name: "RequirePassword",
                table: "Proposals");
        }
    }
}
