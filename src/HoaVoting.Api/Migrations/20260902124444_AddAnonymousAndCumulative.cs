using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HoaVoting.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddAnonymousAndCumulative : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Anonymous",
                table: "Proposals",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "Budget",
                table: "ProposalQuestions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CostExponent",
                table: "ProposalQuestions",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Anonymous",
                table: "Proposals");

            migrationBuilder.DropColumn(
                name: "Budget",
                table: "ProposalQuestions");

            migrationBuilder.DropColumn(
                name: "CostExponent",
                table: "ProposalQuestions");
        }
    }
}
