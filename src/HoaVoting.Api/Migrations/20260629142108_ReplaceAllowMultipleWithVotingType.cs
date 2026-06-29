using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HoaVoting.Api.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceAllowMultipleWithVotingType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "AllowMultiple",
                table: "Proposals",
                newName: "VotingType");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "VotingType",
                table: "Proposals",
                newName: "AllowMultiple");
        }
    }
}
