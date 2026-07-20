using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HoaVoting.Api.Migrations
{
    /// <inheritdoc />
    public partial class ProcessChainId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ChainId",
                table: "Proposals",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ChainId",
                table: "Proposals");
        }
    }
}
