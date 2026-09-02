using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HoaVoting.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddOpenChoiceIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "OpenChoiceIndex",
                table: "ProposalQuestions",
                type: "INTEGER",
                nullable: false,
                defaultValue: -1); // -1 = no open choice (existing questions predate #577)
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OpenChoiceIndex",
                table: "ProposalQuestions");
        }
    }
}
